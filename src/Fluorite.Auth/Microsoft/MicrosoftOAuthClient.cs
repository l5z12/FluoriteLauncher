using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fluorite.Auth.Microsoft;

/// <summary>
/// Microsoft identity platform OAuth for consumer accounts without MSAL: device code, auth-code + PKCE via a
/// loopback redirect, and refresh. The Windows platform layer adds a WAM (Windows account broker) provider on top.
/// </summary>
public sealed class MicrosoftOAuthClient(HttpClient http, MicrosoftAuthOptions options)
{
    // ── Device code ──────────────────────────────────────────────────────

    public async Task<DeviceCodeResponse> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["scope"] = options.Scope,
        });
        using var resp = await http.PostAsync(new Uri(options.Authority + "/devicecode"), content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.DeviceCodeResponse, ct).ConfigureAwait(false)
            ?? throw new AuthException(AuthFailure.Unknown, "Empty device code response.");
    }

    public async Task<MsaTokenResponse> PollDeviceCodeAsync(DeviceCodeResponse code, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(code.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            var token = await TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = options.ClientId,
                ["device_code"] = code.DeviceCode,
            }, ct).ConfigureAwait(false);

            switch (token.Error)
            {
                case null when token.AccessToken is not null:
                    return token;
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "authorization_declined":
                    throw new AuthException(AuthFailure.Cancelled, "Sign-in was declined.");
                default:
                    throw new AuthException(AuthFailure.MicrosoftTokenRejected, token.ErrorDescription ?? token.Error ?? "Device code sign-in failed.");
            }
        }
        throw new AuthException(AuthFailure.Cancelled, "The sign-in code expired.");
    }

    // ── Authorization code + PKCE via loopback ───────────────────────────

    /// <param name="openBrowser">Callback that opens the system browser at the given URL.</param>
    public async Task<MsaTokenResponse> SignInWithBrowserAsync(Func<Uri, Task> openBrowser, CancellationToken ct = default)
    {
        var port = GetFreePort();
        var redirect = $"http://localhost:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        var authorize = new Uri(options.Authority + "/authorize"
            + $"?client_id={Uri.EscapeDataString(options.ClientId)}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(redirect)}"
            + $"&scope={Uri.EscapeDataString(options.Scope)}"
            + $"&code_challenge={challenge}&code_challenge_method=S256"
            + $"&state={state}&prompt=select_account");
        await openBrowser(authorize).ConfigureAwait(false);

        HttpListenerContext ctx;
        using (ct.Register(listener.Stop))
        {
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }

        var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
        var ok = query["code"] is not null && query["state"] == state;
        var page = ok
            ? "<html><body style=\"font-family:Segoe UI;text-align:center;margin-top:20vh\"><h2>Signed in</h2><p>You can close this tab and return to Fluorite Launcher.</p></body></html>"
            : "<html><body style=\"font-family:Segoe UI;text-align:center;margin-top:20vh\"><h2>Sign-in failed</h2><p>Return to Fluorite Launcher and try again.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(page);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.Close();

        if (!ok)
        {
            var error = query["error"];
            throw error == "access_denied"
                ? new AuthException(AuthFailure.Cancelled, "Sign-in was cancelled.")
                : new AuthException(AuthFailure.MicrosoftTokenRejected, query["error_description"] ?? error ?? "Invalid sign-in response.");
        }

        var token = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = options.ClientId,
            ["code"] = query["code"]!,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
            ["scope"] = options.Scope,
        }, ct).ConfigureAwait(false);
        return token.AccessToken is not null
            ? token
            : throw new AuthException(AuthFailure.MicrosoftTokenRejected, token.ErrorDescription ?? "Token exchange failed.");
    }

    // ── Refresh ───────────────────────────────────────────────────────────

    /// <param name="clientId">
    /// The app that issued <paramref name="refreshToken"/> (refresh tokens are bound to it); null for the current one.
    /// </param>
    public async Task<MsaTokenResponse> RefreshAsync(string refreshToken, string? clientId = null, CancellationToken ct = default)
    {
        var token = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId ?? options.ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = options.Scope,
        }, ct).ConfigureAwait(false);
        return token.AccessToken is not null
            ? token
            : throw new AuthException(AuthFailure.MicrosoftTokenRejected, token.ErrorDescription ?? "Your Microsoft sign-in expired. Please sign in again.");
    }

    private async Task<MsaTokenResponse> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync(new Uri(options.Authority + "/token"), content, ct).ConfigureAwait(false);
        // 400 responses carry an OAuth error body that callers interpret.
        return await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.MsaTokenResponse, ct).ConfigureAwait(false)
            ?? new MsaTokenResponse { Error = $"http_{(int)resp.StatusCode}" };
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
