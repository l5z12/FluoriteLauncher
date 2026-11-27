using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fluorite.Auth.Microsoft;
using Fluorite.Auth.Yggdrasil;
using Fluorite.Core;

namespace Fluorite.Auth.Accounts;

/// <summary>A cape a Microsoft account owns.</summary>
public sealed record CapeChoice(string Id, string Name, string Url, bool Active);

/// <summary>What an account wears now and what can be changed from the launcher.</summary>
/// <param name="SkinUrl">Texture URL, or a local file for offline accounts; null for the default skin.</param>
/// <param name="CanChangeSkin">Microsoft and offline accounts; Yggdrasil skins are changed on the skin site (<paramref name="ManageUrl"/>).</param>
public sealed record SkinState(string? SkinUrl, bool Slim, string? CapeUrl, IReadOnlyList<CapeChoice> Capes, bool CanChangeSkin, string? ManageUrl);

/// <summary>A skin kept in the launcher's library (<c>&lt;root&gt;/skins</c>), reusable on any account.</summary>
public sealed record LibrarySkin(string Path, bool Slim, DateTime Added);

/// <summary>Skins, capes and names: reading what an account wears, changing it, and the local skin library.</summary>
public sealed class AccountSkins(
    AccountService accounts, MinecraftAuthChain chain, MinecraftProfileClient profiles, YggdrasilClient yggdrasil,
    HttpClient http, string libraryDirectory, string cacheDirectory)
{
    private const string MojangSessionServer = "https://sessionserver.mojang.com";

    public async Task<SkinState> GetAsync(Account account, CancellationToken ct = default)
    {
        switch (account.Kind)
        {
            case AccountKind.Microsoft:
            {
                var token = await accounts.GetMinecraftTokenAsync(account, ct).ConfigureAwait(false);
                var profile = await chain.GetProfileAsync(token, ct).ConfigureAwait(false)
                    ?? throw new AuthException(AuthFailure.NoMinecraftProfile, "This account has no Minecraft profile.");
                await accounts.UpdateProfileAsync(account, profile, ct).ConfigureAwait(false);
                return FromProfile(profile);
            }
            case AccountKind.Yggdrasil:
            {
                var textures = account.YggdrasilServer is { } api
                    ? await yggdrasil.GetTexturesAsync(api + "/sessionserver", account.Uuid, ct).ConfigureAwait(false)
                    : null;
                return new SkinState(textures?.SkinUrl, textures?.Slim ?? false, textures?.CapeUrl, [], false, Homepage(account));
            }
            default:
                var file = account.SkinFile is { } f && File.Exists(f) ? f : null;
                return new SkinState(file, file is not null && account.SkinSlim, null, [], true, null);
        }
    }

    private static SkinState FromProfile(MinecraftProfile profile)
    {
        var skin = profile.Skins?.FirstOrDefault(s => s.State == "ACTIVE");
        var capes = (profile.Capes ?? [])
            .Where(c => c.Id is not null && c.Url is not null)
            .Select(c => new CapeChoice(c.Id!, c.Alias ?? "Cape", c.Url!, c.State == "ACTIVE"))
            .ToList();
        return new SkinState(skin?.Url, string.Equals(skin?.Variant, "SLIM", StringComparison.OrdinalIgnoreCase),
            capes.FirstOrDefault(c => c.Active)?.Url, capes, true, null);
    }

    /// <summary>The skin site's homepage, from the metadata saved at sign-in.</summary>
    private static string? Homepage(Account account)
    {
        if (account.YggdrasilMetadata is not { } b64)
            return null;
        try
        {
            var meta = JsonSerializer.Deserialize(Convert.FromBase64String(b64), YggdrasilJsonContext.Default.YggdrasilMetadata);
            return meta?.Meta?.Links?.Homepage;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Wears <paramref name="pngPath"/> (and keeps a copy in the library).</summary>
    public async Task<SkinState> SetSkinAsync(Account account, string pngPath, bool slim, CancellationToken ct = default)
    {
        var png = await File.ReadAllBytesAsync(pngPath, ct).ConfigureAwait(false);
        var saved = AddToLibrary(png, slim);
        switch (account.Kind)
        {
            case AccountKind.Microsoft:
                var token = await accounts.GetMinecraftTokenAsync(account, ct).ConfigureAwait(false);
                var profile = await profiles.UploadSkinAsync(token, png, slim, ct).ConfigureAwait(false);
                await accounts.UpdateProfileAsync(account, profile, ct).ConfigureAwait(false);
                return FromProfile(profile);
            case AccountKind.Offline:
                account.SkinFile = saved.Path;
                account.SkinSlim = slim;
                await accounts.SaveAsync(ct).ConfigureAwait(false);
                return await GetAsync(account, ct).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("Skins of this account are changed on its skin site.");
        }
    }

    /// <summary>Back to the default skin.</summary>
    public async Task<SkinState> ResetSkinAsync(Account account, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Microsoft)
        {
            var token = await accounts.GetMinecraftTokenAsync(account, ct).ConfigureAwait(false);
            var profile = await profiles.ResetSkinAsync(token, ct).ConfigureAwait(false);
            await accounts.UpdateProfileAsync(account, profile, ct).ConfigureAwait(false);
            return FromProfile(profile);
        }
        account.SkinFile = null;
        account.SkinSlim = false;
        await accounts.SaveAsync(ct).ConfigureAwait(false);
        return await GetAsync(account, ct).ConfigureAwait(false);
    }

    /// <summary>Shows one of the account's capes, or hides the cape when <paramref name="capeId"/> is null.</summary>
    public async Task<SkinState> SetCapeAsync(Account account, string? capeId, CancellationToken ct = default)
    {
        var token = await accounts.GetMinecraftTokenAsync(account, ct).ConfigureAwait(false);
        var profile = capeId is null
            ? await profiles.HideCapeAsync(token, ct).ConfigureAwait(false)
            : await profiles.ShowCapeAsync(token, capeId, ct).ConfigureAwait(false);
        return FromProfile(profile);
    }

    public async Task<NameAvailability> CheckNameAsync(Account account, string name, CancellationToken ct = default) =>
        await profiles.CheckNameAsync(await accounts.GetMinecraftTokenAsync(account, ct).ConfigureAwait(false), name, ct).ConfigureAwait(false);

    public async Task<DateTimeOffset?> NextNameChangeAsync(Account account, CancellationToken ct = default) =>
        await profiles.NextNameChangeAsync(await accounts.GetMinecraftTokenAsync(account, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task ChangeNameAsync(Account account, string name, CancellationToken ct = default)
    {
        var token = await accounts.GetMinecraftTokenAsync(account, ct).ConfigureAwait(false);
        var profile = await profiles.ChangeNameAsync(token, name, ct).ConfigureAwait(false);
        await accounts.UpdateProfileAsync(account, profile, ct).ConfigureAwait(false);
    }

    // ── Textures ─────────────────────────────────────────────────────────

    /// <summary>
    /// PNG bytes of a texture URL (cached in <c>cache/skins</c>, so avatars show offline) or a local file.
    /// Null when it can't be had.
    /// </summary>
    public async Task<byte[]?> LoadTextureAsync(string urlOrPath, CancellationToken ct = default)
    {
        if (!urlOrPath.Contains("://", StringComparison.Ordinal))
            return File.Exists(urlOrPath) ? await File.ReadAllBytesAsync(urlOrPath, ct).ConfigureAwait(false) : null;

        // Mojang still hands out http:// texture URLs; the same host serves HTTPS.
        var url = urlOrPath.StartsWith("http://textures.minecraft.net/", StringComparison.OrdinalIgnoreCase)
            ? "https://" + urlOrPath["http://".Length..]
            : urlOrPath;
        var cached = Path.Combine(cacheDirectory, CacheKey(url) + ".png");
        if (File.Exists(cached))
            return await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var bytes = await http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
            Directory.CreateDirectory(cacheDirectory);
            await AtomicFile.WriteAllBytesAsync(cached, bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>Texture URLs end in their hash; anything else is keyed by a hash of the URL.</summary>
    private static string CacheKey(string url)
    {
        var last = url.TrimEnd('/').Split('/')[^1];
        if (last.Length is >= 32 and <= 128 && last.All(char.IsAsciiHexDigit))
            return last.ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    }

    // ── Library ──────────────────────────────────────────────────────────

    public IReadOnlyList<LibrarySkin> Library()
    {
        if (!Directory.Exists(libraryDirectory))
            return [];
        return Directory.EnumerateFiles(libraryDirectory, "*.png")
            .Select(p => new LibrarySkin(p, Path.GetFileNameWithoutExtension(p).EndsWith("-slim", StringComparison.OrdinalIgnoreCase), File.GetCreationTimeUtc(p)))
            .OrderByDescending(s => s.Added)
            .ToList();
    }

    /// <summary>Keeps a copy of a skin; the same image and model is stored once.</summary>
    public LibrarySkin AddToLibrary(byte[] png, bool slim)
    {
        Directory.CreateDirectory(libraryDirectory);
        var name = Convert.ToHexStringLower(SHA256.HashData(png))[..16] + (slim ? "-slim" : "-classic") + ".png";
        var path = Path.Combine(libraryDirectory, name);
        if (!File.Exists(path))
            AtomicFile.WriteAllBytes(path, png);
        return new LibrarySkin(path, slim, File.GetCreationTimeUtc(path));
    }

    public static void RemoveFromLibrary(LibrarySkin skin)
    {
        if (File.Exists(skin.Path))
            File.Delete(skin.Path);
    }
}
