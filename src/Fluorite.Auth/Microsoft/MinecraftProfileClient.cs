using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fluorite.Auth.Microsoft;

public enum NameAvailability
{
    Available,
    Taken,
    NotAllowed,
}

/// <summary>Skin, cape and name changes for a Microsoft account (<c>api.minecraftservices.com/minecraft/profile</c>).</summary>
public sealed class MinecraftProfileClient(HttpClient http)
{
    private const string Base = "https://api.minecraftservices.com/minecraft/profile";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    /// <summary>Uploads a skin PNG; <paramref name="slim"/> picks the thin-armed (Alex) model.</summary>
    public async Task<MinecraftProfile> UploadSkinAsync(string token, byte[] png, bool slim, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(slim ? "slim" : "classic"), "variant" },
        };
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");
        return await SendAsync(HttpMethod.Post, Base + "/skins", token, form, ct).ConfigureAwait(false);
    }

    /// <summary>Back to the default skin (Steve/Alex, depending on the profile).</summary>
    public Task<MinecraftProfile> ResetSkinAsync(string token, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, Base + "/skins/active", token, null, ct);

    public Task<MinecraftProfile> ShowCapeAsync(string token, string capeId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, Base + "/capes/active", token,
            JsonContent.Create(new CapeRequest { CapeId = capeId }, AuthJsonContext.Default.CapeRequest), ct);

    public Task<MinecraftProfile> HideCapeAsync(string token, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, Base + "/capes/active", token, null, ct);

    public async Task<NameAvailability> CheckNameAsync(string token, string name, CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var req = Request(HttpMethod.Get, $"{Base}/name/{Uri.EscapeDataString(name)}/available", token, null);
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        await ThrowIfFailedAsync(resp, cts.Token).ConfigureAwait(false);
        var status = await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.NameAvailabilityResponse, cts.Token).ConfigureAwait(false);
        return status?.Status switch
        {
            "AVAILABLE" => NameAvailability.Available,
            "DUPLICATE" => NameAvailability.Taken,
            _ => NameAvailability.NotAllowed,
        };
    }

    /// <summary>When the name may next be changed (names can change once every 30 days); null when it may now.</summary>
    public async Task<DateTimeOffset?> NextNameChangeAsync(string token, CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var req = Request(HttpMethod.Get, Base + "/namechange", token, null);
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        await ThrowIfFailedAsync(resp, cts.Token).ConfigureAwait(false);
        var info = await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.NameChangeInfo, cts.Token).ConfigureAwait(false);
        if (info is null || info.NameChangeAllowed)
            return null;
        return info.ChangedAt is { } changed ? changed.AddDays(30) : DateTimeOffset.UtcNow.AddDays(30);
    }

    public Task<MinecraftProfile> ChangeNameAsync(string token, string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"{Base}/name/{Uri.EscapeDataString(name)}", token, null, ct);

    private async Task<MinecraftProfile> SendAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct)
    {
        using var cts = Timeout(ct);
        using var req = Request(method, url, token, content);
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        await ThrowIfFailedAsync(resp, cts.Token).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync(AuthJsonContext.Default.MinecraftProfile, cts.Token).ConfigureAwait(false)
            ?? throw new AuthException(AuthFailure.Unknown, "Minecraft services returned no profile.");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, HttpContent? content)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (body.Length > 0)
            {
                var err = JsonSerializer.Deserialize(body, AuthJsonContext.Default.MinecraftServicesError);
                message = err?.ErrorMessage ?? err?.Details?.Status ?? err?.Error;
            }
        }
        catch (JsonException)
        {
        }
        var failure = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AuthFailure.SignInAgain,
            HttpStatusCode.TooManyRequests => AuthFailure.RateLimited,
            _ => AuthFailure.Unknown,
        };
        throw new AuthException(failure, message ?? $"Minecraft services answered HTTP {(int)resp.StatusCode}.");
    }

    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(s_timeout);
        return cts;
    }
}
