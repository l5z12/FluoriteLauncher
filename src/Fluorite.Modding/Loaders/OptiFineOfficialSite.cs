using System.Net;
using System.Text.RegularExpressions;
using Fluorite.Core;

namespace Fluorite.Modding.Loaders;

/// <summary>An OptiFine build listed on optifine.net.</summary>
/// <param name="MinecraftVersion">Normalised to Mojang's id (<c>1.8.0</c> → <c>1.8</c>).</param>
/// <param name="Version">e.g. <c>HD_U_I6</c>, <c>HD_U_K2_pre5</c>.</param>
public sealed record OptiFineBuild(string MinecraftVersion, string Version, string FileName, bool IsPreview)
{
    /// <summary>Forge build the release notes say it's compatible with ("Forge 47.1.0", "Forge #2000"), if any.</summary>
    public string? Forge { get; init; }
}

/// <summary>
/// optifine.net has no API. The downloads page lists every build; each file's mirror page (<c>adloadx?f=…</c>)
/// carries a one-time <c>downloadx?f=…&amp;x=…</c> link that only works in the same cookie session, so both requests
/// go through one short-lived client with its own cookie container.
/// </summary>
internal static partial class OptiFineOfficialSite
{
    private const string Base = "https://optifine.net";
    private static readonly TimeSpan s_listTtl = TimeSpan.FromMinutes(30);
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static (DateTimeOffset At, IReadOnlyList<OptiFineBuild> Builds)? s_cache;

    public static async Task<IReadOnlyList<OptiFineBuild>> GetBuildsAsync(HttpClient http, CancellationToken ct)
    {
        await s_gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (s_cache is { } c && DateTimeOffset.UtcNow - c.At < s_listTtl)
                return c.Builds;
            var html = await http.GetStringAsync($"{Base}/downloads", ct).ConfigureAwait(false);
            var builds = ParseDownloadsPage(html);
            if (builds.Count == 0)
                throw new HttpRequestException("optifine.net returned no downloads (page layout changed?).");
            s_cache = (DateTimeOffset.UtcNow, builds);
            return builds;
        }
        finally
        {
            s_gate.Release();
        }
    }

    internal static List<OptiFineBuild> ParseDownloadsPage(string html)
    {
        var result = new List<OptiFineBuild>();
        foreach (Match row in DownloadRow().Matches(html))
        {
            var body = row.Groups["body"].Value;
            var file = MirrorLink().Match(body);
            if (!file.Success)
                continue;
            var name = WebUtility.UrlDecode(file.Groups["file"].Value);
            var parts = FileName().Match(name);
            if (!parts.Success)
                continue;
            var forge = ForgeCell().Match(body) is { Success: true } f && !f.Groups["forge"].Value.Contains("N/A", StringComparison.OrdinalIgnoreCase)
                ? WebUtility.HtmlDecode(f.Groups["forge"].Value.Trim())
                : null;
            result.Add(new OptiFineBuild(NormaliseMinecraft(parts.Groups["mc"].Value), parts.Groups["ver"].Value, name, parts.Groups["preview"].Success) { Forge = forge });
        }
        return result;
    }

    /// <summary>optifine.net writes 1.8 as 1.8.0 (and a few others); Mojang ids drop the trailing .0.</summary>
    internal static string NormaliseMinecraft(string mc) =>
        mc.Count(c => c == '.') == 2 && mc.EndsWith(".0", StringComparison.Ordinal) ? mc[..^2] : mc;

    public static string FileNameFor(string minecraftVersion, string version, IReadOnlyList<OptiFineBuild>? known)
    {
        if (known?.FirstOrDefault(b => b.MinecraftVersion == minecraftVersion && b.Version == version) is { } b)
            return b.FileName;
        var preview = version.Contains("_pre", StringComparison.OrdinalIgnoreCase);
        return $"{(preview ? "preview_" : "")}OptiFine_{minecraftVersion}_{version}.jar";
    }

    /// <summary>Downloads <paramref name="fileName"/> from optifine.net to <paramref name="target"/>.</summary>
    public static async Task DownloadAsync(string fileName, string target, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FluoriteInfo.UserAgent);

        var mirrorPage = $"{Base}/adloadx?f={Uri.EscapeDataString(fileName)}";
        var html = await http.GetStringAsync(mirrorPage, ct).ConfigureAwait(false);
        var link = DownloadLink().Match(html);
        if (!link.Success)
            throw new HttpRequestException($"optifine.net has no download link for {fileName}.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/{WebUtility.HtmlDecode(link.Groups["href"].Value)}");
        request.Headers.Referrer = new Uri(mirrorPage);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".part";
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);

        // An expired/invalid key returns a tiny text page ("Request not found.") with 200 OK.
        if (!IsZip(tmp))
        {
            File.Delete(tmp);
            throw new HttpRequestException($"optifine.net did not return a jar for {fileName}.");
        }
        File.Move(tmp, target, overwrite: true);
    }

    private static bool IsZip(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return fs.Length > 1024 && fs.Read(magic) == 4 && magic[0] == 'P' && magic[1] == 'K';
    }

    [GeneratedRegex(@"<tr class=['""]downloadLine[^>]*>(?<body>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DownloadRow();

    [GeneratedRegex(@"adloadx\?f=(?<file>[^""'&<>\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MirrorLink();

    [GeneratedRegex(@"^(?<preview>preview_)?OptiFine_(?<mc>[0-9][^_]*)_(?<ver>HD_.+)\.jar$", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    [GeneratedRegex(@"class=['""]colForge['""]>(?<forge>[^<]*)<", RegexOptions.IgnoreCase)]
    private static partial Regex ForgeCell();

    [GeneratedRegex(@"href=['""](?<href>downloadx\?f=[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadLink();
}
