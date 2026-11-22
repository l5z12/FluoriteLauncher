using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Fluorite.Auth.Microsoft;

namespace Fluorite.Auth.Yggdrasil;

/// <summary>The skin and cape a profile wears, from its signed <c>textures</c> property.</summary>
public sealed record ProfileTextures(string? SkinUrl, bool Slim, string? CapeUrl);

/// <summary>
/// Client for authlib-injector compatible Yggdrasil servers (LittleSkin, Ely.by, Blessing Skin…), following
/// the authlib-injector launcher and server specifications.
/// </summary>
public sealed class YggdrasilClient(HttpClient http)
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resolves what the user typed (<c>littleskin.cn</c>, a full URL…) to the API root via API Location Indication,
    /// and reads the server metadata.
    /// </summary>
    public async Task<YggdrasilServer> DiscoverAsync(string address, CancellationToken ct = default)
    {
        var url = Normalize(address);
        var (finalUrl, body, location) = await GetMetadataAsync(url, ct).ConfigureAwait(false);
        if (location is not null && !SameUrl(location, finalUrl))
        {
            url = location;
            (finalUrl, body, _) = await GetMetadataAsync(location, ct).ConfigureAwait(false);
        }

        YggdrasilMetadata? meta = null;
        try
        {
            meta = JsonSerializer.Deserialize(body, YggdrasilJsonContext.Default.YggdrasilMetadata);
        }
        catch (JsonException)
        {
        }
        if (meta?.SignaturePublickey is null && meta?.Meta is null)
            throw new AuthException(AuthFailure.NotYggdrasilServer, $"{url} is not an authlib-injector authentication server.");

        var root = finalUrl.TrimEnd('/');
        return new YggdrasilServer(
            root,
            meta.Meta?.ServerName is { Length: > 0 } name ? name : new Uri(root).Host,
            meta.Meta?.Links?.Homepage,
            meta.Meta?.Links?.Register,
            meta.Meta?.NonEmailLogin ?? false,
            Convert.ToBase64String(body));
    }

    /// <summary>Normalizes user input to an absolute URL: <c>https://</c> is assumed; plain HTTP is allowed only on this machine.</summary>
    public static string Normalize(string address)
    {
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new AuthException(AuthFailure.NotYggdrasilServer, $"'{address}' is not a valid server address.");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new AuthException(AuthFailure.InsecureServer, $"{uri.Host} uses unencrypted HTTP; passwords would be sent in the clear.");
        return uri.AbsoluteUri;
    }

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private async Task<(string FinalUrl, byte[] Body, string? Location)> GetMetadataAsync(string url, CancellationToken ct)
    {
        using var cts = Timeout(ct);
        using var resp = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
        var final = resp.RequestMessage?.RequestUri ?? new Uri(url);
        string? location = null;
        if (resp.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var values) && values.FirstOrDefault() is { Length: > 0 } ali
            && Uri.TryCreate(final, ali, out var resolved))
        {
            // Never let the indication downgrade an HTTPS address to HTTP.
            if (resolved.Scheme == Uri.UriSchemeHttps || final.Scheme == Uri.UriSchemeHttp)
                location = resolved.AbsoluteUri;
        }
        if (!resp.IsSuccessStatusCode && location is null)
            throw new AuthException(AuthFailure.NotYggdrasilServer, $"{url} answered HTTP {(int)resp.StatusCode}.");
        var body = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        return (final.AbsoluteUri, body, location);
    }

    /// <summary>Signs in with an email (or a profile name, where the server allows it) and password.</summary>
    public Task<YggdrasilAuthResponse> AuthenticateAsync(string apiRoot, string username, string password, string? clientToken, CancellationToken ct = default) =>
        PostAsync(apiRoot + "/authserver/authenticate",
            new YggdrasilAuthenticateRequest { Username = username, Password = password, ClientToken = clientToken },
            YggdrasilJsonContext.Default.YggdrasilAuthenticateRequest, ct);

    /// <summary>Exchanges a token for a new one, optionally binding it to <paramref name="profile"/>.</summary>
    public Task<YggdrasilAuthResponse> RefreshAsync(string apiRoot, string accessToken, string? clientToken, YggdrasilProfile? profile, CancellationToken ct = default) =>
        PostAsync(apiRoot + "/authserver/refresh",
            new YggdrasilRefreshRequest
            {
                AccessToken = accessToken,
                ClientToken = clientToken,
                SelectedProfile = profile is null ? null : new YggdrasilProfile { Id = profile.Id, Name = profile.Name },
            },
            YggdrasilJsonContext.Default.YggdrasilRefreshRequest, ct);

    /// <summary>Whether the token still works (204); any error response means it doesn't.</summary>
    public async Task<bool> ValidateAsync(string apiRoot, string accessToken, string? clientToken, CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var resp = await http.PostAsJsonAsync(apiRoot + "/authserver/validate",
            new YggdrasilTokenRequest { AccessToken = accessToken, ClientToken = clientToken },
            YggdrasilJsonContext.Default.YggdrasilTokenRequest, cts.Token).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Revokes the token (sign-out of this launcher). Errors are ignored: the account is removed either way.</summary>
    public async Task InvalidateAsync(string apiRoot, string accessToken, string? clientToken, CancellationToken ct = default)
    {
        try
        {
            using var cts = Timeout(ct);
            using var resp = await http.PostAsJsonAsync(apiRoot + "/authserver/invalidate",
                new YggdrasilTokenRequest { AccessToken = accessToken, ClientToken = clientToken },
                YggdrasilJsonContext.Default.YggdrasilTokenRequest, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The skin and cape of a profile from a session server: <c>{apiRoot}/sessionserver</c> for Yggdrasil servers,
    /// <c>https://sessionserver.mojang.com</c> for Microsoft accounts. Null when the profile doesn't exist.
    /// </summary>
    public async Task<ProfileTextures?> GetTexturesAsync(string sessionServer, string uuid, CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var resp = await http.GetAsync($"{sessionServer.TrimEnd('/')}/session/minecraft/profile/{uuid}", cts.Token).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        var profile = await resp.Content.ReadFromJsonAsync(YggdrasilJsonContext.Default.YggdrasilProfile, cts.Token).ConfigureAwait(false);
        return profile is null ? null : ReadTextures(profile);
    }

    public static ProfileTextures ReadTextures(YggdrasilProfile profile)
    {
        var value = profile.Properties?.FirstOrDefault(p => p.Name == "textures")?.Value;
        if (value is null)
            return new ProfileTextures(null, false, null);
        try
        {
            var payload = JsonSerializer.Deserialize(Convert.FromBase64String(value), YggdrasilJsonContext.Default.TexturesPayload);
            var skin = payload?.Textures.GetValueOrDefault("SKIN");
            var cape = payload?.Textures.GetValueOrDefault("CAPE");
            var slim = skin?.Metadata?.GetValueOrDefault("model") == "slim";
            return new ProfileTextures(skin?.Url, slim, cape?.Url);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new ProfileTextures(null, false, null);
        }
    }

    private async Task<YggdrasilAuthResponse> PostAsync<T>(string url, T body, JsonTypeInfo<T> info, CancellationToken ct)
    {
        using var cts = Timeout(ct);
        using var resp = await http.PostAsJsonAsync(url, body, info, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw await ErrorAsync(resp, cts.Token).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync(YggdrasilJsonContext.Default.YggdrasilAuthResponse, cts.Token).ConfigureAwait(false)
            ?? throw new AuthException(AuthFailure.Unknown, "The server returned an empty response.");
    }

    private static async Task<AuthException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        YggdrasilError? err = null;
        try
        {
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 0)
                err = JsonSerializer.Deserialize(bytes, YggdrasilJsonContext.Default.YggdrasilError);
        }
        catch (JsonException)
        {
        }
        var message = err?.ErrorMessage is { Length: > 0 } m ? m : $"HTTP {(int)resp.StatusCode} {err?.Error}".Trim();
        var failure = resp.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => AuthFailure.InvalidCredentials,
            HttpStatusCode.TooManyRequests => AuthFailure.RateLimited,
            _ => AuthFailure.Unknown,
        };
        return new AuthException(failure, message);
    }

    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(s_timeout);
        return cts;
    }
}
