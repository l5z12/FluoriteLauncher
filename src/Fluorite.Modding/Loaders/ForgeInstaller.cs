using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fluorite.Core.Java;
using Fluorite.Core.Json;
using Fluorite.Core.Libraries;
using Fluorite.Core.Net;
using Fluorite.Core.Platform;
using Fluorite.Core.Versions;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Loaders;

public sealed class ForgeProcessor
{
    public string Jar { get; set; } = "";
    public List<string>? Classpath { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Outputs { get; set; }
    public List<string>? Sides { get; set; }
}

public sealed class ForgeDataEntry
{
    public string? Client { get; set; }
    public string? Server { get; set; }
}

/// <summary>Modern (spec 0/1) <c>install_profile.json</c>.</summary>
public sealed class ForgeInstallProfile
{
    public int? Spec { get; set; }
    public string? Version { get; set; }
    public string? Json { get; set; }
    public string? Minecraft { get; set; }
    public Dictionary<string, ForgeDataEntry>? Data { get; set; }
    public List<ForgeProcessor>? Processors { get; set; }
    public List<Library>? Libraries { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ForgeInstallProfile))]
internal sealed partial class ForgeJsonContext : JsonSerializerContext;

/// <summary>
/// Installs Forge/NeoForge from their official installer jar without running the installer GUI:
/// <list type="bullet">
/// <item><b>Modern</b> (1.12.2-14.23.5.2851+, 1.13+, all NeoForge): extract the version JSON and embedded maven
/// artifacts, download processor libraries, resolve the <c>data</c> map and run every client-side processor with
/// output SHA-1 verification (skipping processors whose outputs are already valid).</item>
/// <item><b>Legacy</b> (1.5.2–1.12.2): write <c>versionInfo</c> as the version JSON and extract the universal jar.</item>
/// </list>
/// </summary>
public static partial class ForgeInstaller
{
    [GeneratedRegex(@"\{(?<key>[A-Z0-9_]+)\}")]
    private static partial Regex DataKeyRegex();

    public static async Task<string> InstallAsync(string installerUrl, string fileName, string minecraftVersion, LoaderInstallContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(ctx.CacheDir);
        var installerPath = Path.Combine(ctx.CacheDir, fileName);
        ctx.Status?.Report($"Downloading {fileName}");
        await ctx.Downloader.DownloadAllAsync([new DownloadItem(ctx.Mirrors.Candidates(installerUrl), installerPath) { DisplayName = fileName }], ctx.Download, ct).ConfigureAwait(false);

        string profileText;
        using (var zip = ZipFile.OpenRead(installerPath))
            profileText = JarTools.ReadText(zip, "install_profile.json") ?? throw new InvalidDataException($"{fileName} has no install_profile.json.");

        using var doc = JsonDocument.Parse(profileText);
        return doc.RootElement.TryGetProperty("install", out var install) && doc.RootElement.TryGetProperty("versionInfo", out var versionInfo)
            ? await InstallLegacyAsync(installerPath, install, versionInfo, minecraftVersion, ctx, ct).ConfigureAwait(false)
            : await InstallModernAsync(installerPath, profileText, minecraftVersion, ctx, ct).ConfigureAwait(false);
    }

    // ── Legacy (1.5.2 – 1.12.2) ─────────────────────────────────────────────

    private static async Task<string> InstallLegacyAsync(string installerPath, JsonElement install, JsonElement versionInfo, string minecraftVersion, LoaderInstallContext ctx, CancellationToken ct)
    {
        var path = install.GetProperty("path").GetString()!;
        var filePath = install.GetProperty("filePath").GetString()!;
        var target = install.TryGetProperty("target", out var t) ? t.GetString() : null;
        var minecraft = install.TryGetProperty("minecraft", out var m) ? m.GetString() ?? minecraftVersion : minecraftVersion;

        ctx.Status?.Report("Extracting Forge");
        using (var zip = ZipFile.OpenRead(installerPath))
            JarTools.Extract(zip, filePath, ctx.Dirs.LibraryPath(MavenCoordinate.Parse(path).RelativePath));

        await ctx.Versions.GetOrFetchAsync(minecraft, ct).ConfigureAwait(false);
        return await VersionJsonWriter.WriteRawAsync(ctx.Dirs, versionInfo.GetRawText(), node =>
        {
            // Some old installers wrote a full JSON without inheritsFrom; make it inherit vanilla like the rest.
            node["inheritsFrom"] ??= minecraft;
            node["jar"] ??= minecraft;
            if (target is not null)
                node["id"] = target;
        }, ct).ConfigureAwait(false);
    }

    // ── Modern (processors) ────────────────────────────────────────────────

    private static async Task<string> InstallModernAsync(string installerPath, string profileText, string minecraftVersion, LoaderInstallContext ctx, CancellationToken ct)
    {
        var profile = JsonSerializer.Deserialize(profileText, ForgeJsonContext.Default.ForgeInstallProfile)
            ?? throw new InvalidDataException("Invalid install_profile.json.");
        var minecraft = profile.Minecraft ?? minecraftVersion;
        var dirs = ctx.Dirs;

        string versionText;
        using (var zip = ZipFile.OpenRead(installerPath))
        {
            versionText = JarTools.ReadText(zip, profile.Json ?? "/version.json") ?? throw new InvalidDataException("Installer has no version JSON.");

            // Embedded artifacts (forge universal/client jars, etc.) live under maven/ in the installer.
            ctx.Status?.Report("Extracting embedded libraries");
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("maven/", StringComparison.Ordinal) && e.Name.Length > 0))
                JarTools.Extract(zip, entry.FullName, dirs.LibraryPath(entry.FullName["maven/".Length..]));
        }
        var version = JsonSerializer.Deserialize(versionText, FluoriteJsonContext.Default.VersionJson)
            ?? throw new InvalidDataException("Invalid version JSON in installer.");

        // Vanilla JSON + client jar are inputs to the processors ({MINECRAFT_JAR}).
        ctx.Status?.Report($"Downloading Minecraft {minecraft}");
        var vanilla = await ctx.Versions.GetOrFetchAsync(minecraft, ct).ConfigureAwait(false);
        var vanillaJar = dirs.VersionJar(minecraft);
        var downloads = new List<DownloadItem>();
        if (vanilla.Downloads?.TryGetValue("client", out var client) == true && client?.Url is { Length: > 0 } clientUrl)
            downloads.Add(new DownloadItem(ctx.Mirrors.Candidates(clientUrl), vanillaJar, client.Sha1, client.Size) { DisplayName = $"{minecraft}.jar" });

        var ruleCtx = RuleContext.ForCurrentOs(RuntimePlatform.Current.OsArch == CpuArch.Arm64 ? CpuArch.Arm64 : CpuArch.X64);
        var allLibraries = new List<Library>(profile.Libraries ?? []);
        allLibraries.AddRange(version.Libraries ?? []);
        // No group:artifact de-duplication: processors may each need a different version of the same library.
        downloads.AddRange(LibraryPlanner.Plan(allLibraries, dirs, ruleCtx, ctx.Mirrors, deduplicate: false).Downloads);

        ctx.Status?.Report("Downloading loader libraries");
        await ctx.Downloader.DownloadAllAsync(downloads, ctx.Download, ct).ConfigureAwait(false);

        var processors = (profile.Processors ?? []).Where(p => p.Sides is null || p.Sides.Contains("client", StringComparer.OrdinalIgnoreCase)).ToList();
        if (processors.Count > 0)
        {
            var java = await ctx.Java.EnsureAsync(
                JavaRequirement.For(vanilla.JavaVersion?.MajorVersion, vanilla.JavaVersion?.Component, versionHasArm64Natives: true, RuntimePlatform.Current.OsArch),
                ctx.Download, ct: ct).ConfigureAwait(false);

            var scratch = Path.Combine(ctx.CacheDir, "forge-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var data = BuildData(profile, installerPath, minecraft, vanillaJar, dirs, scratch);
                for (var i = 0; i < processors.Count; i++)
                {
                    ctx.Status?.Report($"Running installer step {i + 1}/{processors.Count}");
                    await RunProcessorAsync(processors[i], data, java, dirs, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                TryDeleteDirectory(scratch);
            }
        }

        return await VersionJsonWriter.WriteRawAsync(dirs, versionText, node => node["inheritsFrom"] ??= minecraft, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> BuildData(ForgeInstallProfile profile, string installerPath, string minecraft, string vanillaJar, Core.GameDirectories dirs, string scratch)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SIDE"] = "client",
            ["MINECRAFT_JAR"] = vanillaJar,
            ["MINECRAFT_VERSION"] = minecraft,
            ["ROOT"] = dirs.Root,
            ["INSTALLER"] = installerPath,
            ["LIBRARY_DIR"] = dirs.Libraries,
        };

        using var zip = ZipFile.OpenRead(installerPath);
        foreach (var (key, entry) in profile.Data ?? [])
        {
            var value = entry.Client;
            if (value is null)
                continue;
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                data[key] = LibraryPath(dirs, value[1..^1]);
            }
            else if (value.StartsWith('\'') && value.EndsWith('\''))
            {
                data[key] = value[1..^1];
            }
            else if (value.StartsWith('/'))
            {
                // A file inside the installer (e.g. /data/client.lzma binary patches).
                var target = Path.Combine(scratch, value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                JarTools.Extract(zip, value, target);
                data[key] = target;
            }
            else
            {
                data[key] = value;
            }
        }
        return data;
    }

    private static string LibraryPath(Core.GameDirectories dirs, string coordinate) =>
        dirs.LibraryPath(MavenCoordinate.Parse(coordinate).RelativePath);

    /// <summary>Resolves a processor argument: <c>[maven]</c> → path, <c>{KEY}</c> → data value (also inside strings).</summary>
    internal static string ResolveArgument(string arg, IReadOnlyDictionary<string, string> data, Core.GameDirectories dirs)
    {
        if (arg.Length > 2 && arg.StartsWith('[') && arg.EndsWith(']'))
            return LibraryPath(dirs, arg[1..^1]);
        if (arg.Length > 2 && arg.StartsWith('\'') && arg.EndsWith('\''))
            return arg[1..^1];
        return DataKeyRegex().Replace(arg, m => data.TryGetValue(m.Groups["key"].Value, out var v) ? v : m.Value);
    }

    private static async Task RunProcessorAsync(ForgeProcessor processor, Dictionary<string, string> data, JavaInstallation java, Core.GameDirectories dirs, CancellationToken ct)
    {
        // Skip when every declared output already exists with the expected hash (re-install / repair).
        var outputs = (processor.Outputs ?? [])
            .Select(kv => (File: ResolveArgument(kv.Key, data, dirs), Sha1: ResolveArgument(kv.Value, data, dirs)))
            .ToList();
        if (outputs.Count > 0 && await OutputsValidAsync(outputs, ct).ConfigureAwait(false))
            return;

        var jar = LibraryPath(dirs, processor.Jar);
        var mainClass = JarTools.ReadMainClass(jar) ?? throw new InvalidDataException($"Processor {processor.Jar} has no Main-Class.");
        var classpath = new List<string> { jar };
        classpath.AddRange((processor.Classpath ?? []).Select(c => LibraryPath(dirs, c)));

        var psi = new ProcessStartInfo(java.JavaExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dirs.Root,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(';', classpath));
        psi.ArgumentList.Add(mainClass);
        foreach (var a in processor.Args ?? [])
            psi.ArgumentList.Add(ResolveArgument(a, data, dirs));

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Java for a Forge processor.");
        var output = new StringBuilder();
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, output);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var text = output.ToString();
            throw new LoaderInstallException($"Forge processor {processor.Jar} failed with exit code {process.ExitCode}:{Environment.NewLine}{Tail(text, 2000)}");
        }

        foreach (var (file, sha1) in outputs)
        {
            if (!File.Exists(file))
                throw new LoaderInstallException($"Forge processor {processor.Jar} did not produce {file}.");
            var actual = await HashUtil.Sha1FileAsync(file, ct).ConfigureAwait(false);
            if (!HashUtil.HashEquals(actual, sha1))
            {
                File.Delete(file);
                throw new LoaderInstallException($"Forge processor output {Path.GetFileName(file)} has SHA-1 {actual}, expected {sha1}.");
            }
        }
    }

    private static async Task<bool> OutputsValidAsync(List<(string File, string Sha1)> outputs, CancellationToken ct)
    {
        foreach (var (file, sha1) in outputs)
        {
            if (!File.Exists(file) || !HashUtil.HashEquals(await HashUtil.Sha1FileAsync(file, ct).ConfigureAwait(false), sha1))
                return false;
        }
        return true;
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lock (sink)
                sink.AppendLine(line);
        }
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class LoaderInstallException(string message, Exception? inner = null) : Exception(message, inner);
