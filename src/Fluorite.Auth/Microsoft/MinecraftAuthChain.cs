using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Fluorite.Auth.Microsoft;

public sealed record MinecraftLogin(string AccessToken, DateTimeOffset Expires, string? Xuid, MinecraftProfile? Profile, bool OwnsGame);

/// <summary>MSA token → Xbox Live user token → XSTS (Minecraft RP) → Minecraft access token → entitlements + profile.</summary>
public sealed class MinecraftAuthChain(HttpClient http)
{
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string EntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    public async Task<MinecraftLogin> LoginAsync(string msaAccessToken, CancellationToken ct = default)
    {
        var xbl = await PostXboxAsync(XblUrl, new XboxAuthRequest
        {
            Properties = new XboxAuthProperties { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + msaAccessToken },
            RelyingParty = "http://auth.xboxlive.com",
        }, ct).ConfigureAwait(false);

        var xsts = await PostXboxAsync(XstsUrl, new XboxAuthRequest
        {
            Properties = new XboxAuthProperties { SandboxId = "RETAIL", UserTokens = [xbl.Token!] },
            RelyingParty = "rp://api.minecraftservices.com/",
        }, ct).ConfigureAwait(false);

        var uhs = xsts.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs
            ?? throw new AuthException(AuthFailure.Unknown, "XSTS response is missing the user hash.");

        using var loginReq = new HttpRequestMessage(HttpMethod.Post, McLoginUrl)
        {
            Content = JsonContent.Create(new MinecraftLoginRequest { IdentityToken = $"XBL3.0 x={uhs};{xsts.Token}" }, AuthJsonContext.Default.MinecraftLoginRequest),
        };
        using var loginResp = await http.SendAsync(loginReq, ct).ConfigureAwait(false);
        if (loginResp.StatusCode == HttpStatusCode.Forbidden)
            throw new AuthException(AuthFailure.AppNotApprovedForMinecraft, "This build's Azure app is not approved for the Minecraft API.");
        if (loginResp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new AuthException(AuthFailure.RateLimited, "Too many sign-in attempts. Wait a minute and try again.");
        loginResp.EnsureSuccessStatusCode();
        var login = await loginResp.Content.ReadFromJsonAsync(AuthJsonContext.Default.MinecraftLoginResponse, ct).ConfigureAwait(false);
        var mcToken = login?.AccessToken ?? throw new AuthException(AuthFailure.Unknown, "Minecraft login returned no token.");
        var expires = DateTimeOffset.UtcNow.AddSeconds(login.ExpiresIn > 0 ? login.ExpiresIn : 86400);

        var profile = await GetProfileAsync(mcToken, ct).ConfigureAwait(false);
        var owns = profile is not null || await HasEntitlementAsync(mcToken, ct).ConfigureAwait(false);
        return new MinecraftLogin(mcToken, expires, ReadXuidFromJwt(mcToken), profile, owns);
    }

    public async Task<MinecraftProfile?> GetProfileAsync(string mcToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null; // Owns no profile (didn't buy, or Game Pass user who hasn't picked a name yet).
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.MinecraftProfile, ct).ConfigureAwait(false);
    }

    private async Task<bool> HasEntitlementAsync(string mcToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, EntitlementsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return false;
        var ent = await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.MinecraftEntitlements, ct).ConfigureAwait(false);
        return ent?.Items?.Any(i => i.Name is "game_minecraft" or "product_minecraft") == true;
    }

    private async Task<XboxAuthResponse> PostXboxAsync(string url, XboxAuthRequest body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, AuthJsonContext.Default.XboxAuthRequest),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("x-xbl-contract-version", "1");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var err = await TryReadAsync(resp, AuthJsonContext.Default.XboxErrorResponse, ct).ConfigureAwait(false);
            var failure = err is null ? AuthFailure.MicrosoftTokenRejected : AuthException.FromXErr(err.XErr);
            throw new AuthException(failure, err?.Message ?? $"Xbox Live rejected the sign-in (XErr {err?.XErr}).");
        }
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.XboxAuthResponse, ct).ConfigureAwait(false);
        return result?.Token is not null ? result : throw new AuthException(AuthFailure.Unknown, "Xbox Live returned no token.");
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage resp, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync(info, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>The Minecraft access token is a JWT whose payload carries the <c>xuid</c> claim.</summary>
    internal static string? ReadXuidFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("xuid", out var x) ? x.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
