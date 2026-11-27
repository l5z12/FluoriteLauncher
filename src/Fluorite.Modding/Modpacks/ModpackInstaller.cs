using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fluorite.Core;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Core.Text;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Modpacks;

/// <summary>
/// <c>&lt;metadata&gt;/pack.json</c>: which modpack an instance was installed from and the SHA-1 of every file the pack
/// put there, so updates can tell untouched pack files (safe to replace or remove) from files the user changed.
/// </summary>
public sealed class PackState
{
    public int FormatVersion { get; set; } = 1;
    public PackFormat Format { get; set; }
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? ProjectId { get; set; }
    public string? VersionId { get; set; }
    public string? Url { get; set; }
    public string MinecraftVersion { get; set; } = "";
    public string? LoaderUid { get; set; }
    public string? LoaderVersion { get; set; }
    public DateTimeOffset InstalledAt { get; set; }

#pragma warning disable CA2227 // Serialised.
    /// <summary>Relative path → SHA-1 as the pack installed it.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore CA2227

    public const string FileName = "pack.json";

    public static PackState? Load(string metadataDirectory)
    {
        var path = Path.Combine(metadataDirectory, FileName);
        try
        {
            if (!File.Exists(path))
                return null;
            var state = JsonSerializer.Deserialize(File.ReadAllBytes(path), PackStateJsonContext.Default.PackState);
            if (state is not null)
                state.Files = new Dictionary<string, string>(state.Files, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string metadataDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(metadataDirectory);
        var path = Path.Combine(metadataDirectory, FileName);
        await AtomicFile.WriteAsync(path, async tmp =>
        {
            await using var fs = File.Create(tmp);
            await JsonSerializer.SerializeAsync(fs, this, PackStateJsonContext.Default.PackState, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public PackSource ToSource() => new(Format, ProjectId, VersionId, Version) { Url = Url };
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PackState))]
internal sealed partial class PackStateJsonContext : JsonSerializerContext;

/// <summary>One file a pack wants at a path: downloaded, or copied from the archive. <see cref="Hash"/> is what it must hash to.</summary>
public sealed record PackEntry(string Path, PackFile? File, PackOverride? Override, string Algorithm, string? Hash)
{
    public string? Sha1 => Algorithm == "sha1" ? Hash : null;
}

public enum PackChangeKind
{
    /// <summary>New in this version (or missing from disk).</summary>
    Add,

    /// <summary>The pack changed it and the user didn't touch it.</summary>
    Replace,

    /// <summary>The pack dropped it and the user didn't touch it.</summary>
    Remove,

    /// <summary>The pack changed or dropped it, but the user changed it too.</summary>
    Modified,

    /// <summary>The pack now ships a file the user already has their own copy of.</summary>
    Conflict,
}

/// <summary>A planned change; <see cref="Apply"/> defaults to the safe choice (user changes are kept).</summary>
public sealed class PackChange(string path, PackChangeKind kind, PackEntry? entry)
{
    public string Path { get; } = path;
    public PackChangeKind Kind { get; } = kind;
    public PackEntry? Entry { get; } = entry;
    public bool Apply { get; set; } = kind is PackChangeKind.Add or PackChangeKind.Replace or PackChangeKind.Remove;
}

public sealed class PackUpdatePlan
{
    public required PackManifest Manifest { get; init; }
    public required IReadOnlyDictionary<string, PackEntry> Entries { get; init; }
    public List<PackChange> Changes { get; } = [];
    public PackState? Previous { get; init; }

    public string? NewMinecraft { get; init; }
    public (string? Uid, string? Version)? NewLoader { get; init; }

    public int Unchanged { get; set; }
    public bool HasComponentChanges => NewMinecraft is not null || NewLoader is not null;
}

public sealed record PackInstallResult(IReadOnlyList<PackFile> Blocked, IReadOnlyList<string> Warnings);

/// <summary>Installs a modpack into an instance, and plans/applies updates that keep the user's changes.</summary>
public sealed class ModpackInstaller(DownloadEngine downloader, MirrorTable mirrors)
{
    /// <summary>Creates a Fluorite instance for the pack (components, settings, icon); files are installed by <see cref="InstallAsync"/>.</summary>
    /// <param name="group">The Library group to put it in; null or blank for none.</param>
    public static async Task<GameInstance> CreateInstanceAsync(FluoriteLayout layout, PackManifest manifest, string? name = null, string? group = null, CancellationToken ct = default)
    {
        var m = new InstanceManifest
        {
            Name = string.IsNullOrWhiteSpace(name) ? manifest.Name : name.Trim(),
            Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
            Notes = manifest.Summary,
            IconKey = manifest.IconKey,
            Components = manifest.Components(),
        };
        InstanceSettingsDiff.Overlay(m.Settings, manifest.Settings);
        var instance = await layout.CreateAsync(m, ct).ConfigureAwait(false);
        // Keep the icon's own type: Prism icons may be SVG, JPEG, WebP…
        var icon = Path.Combine(instance.Directory, "icon" + (manifest.IconEntry is { } named && Path.GetExtension(named) is { Length: > 0 } ext ? ext.ToLowerInvariant() : ".png"));
        try
        {
            if (manifest.IconEntry is { } entry && manifest.ArchivePath is { } archive)
            {
                using var zip = ZipNames.OpenRead(archive);
                zip.GetEntry(entry)?.ExtractToFile(icon, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
        }
        if (File.Exists(icon))
        {
            instance.Manifest.Icon = icon;
            await layout.SaveAsync(instance, ct).ConfigureAwait(false);
        }
        return instance;
    }

    /// <summary>Every file the pack wants, with the hash it must have. Overrides are hashed from the archive.</summary>
    public static Dictionary<string, PackEntry> Entries(PackManifest manifest, bool includeOptional = true)
    {
        var entries = new Dictionary<string, PackEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
        {
            if (f.Client == PackSide.Unsupported || (f.Client == PackSide.Optional && !includeOptional))
                continue;
            var (algorithm, hash) = f.Sha1 is not null ? ("sha1", f.Sha1) : f.Sha512 is not null ? ("sha512", f.Sha512) : f.OtherHash is { } o ? (o.Algorithm, o.Value) : ("sha1", (string?)null);
            entries[f.Path] = new PackEntry(f.Path, f, null, algorithm, hash?.ToLowerInvariant());
        }
        if (manifest.Overrides.Count > 0 && manifest.ArchivePath is { } archive)
        {
            using var zip = ZipNames.OpenRead(archive);
            foreach (var o in manifest.Overrides)
            {
                if (zip.GetEntry(o.Entry) is not { } e)
                    continue;
                using var s = e.Open();
                entries[o.Path] = new PackEntry(o.Path, null, o, "sha1", Convert.ToHexStringLower(SHA1.HashData(s)));
            }
        }
        return entries;
    }

    /// <summary>Fresh install: writes every entry and records the pack state.</summary>
    public async Task<PackInstallResult> InstallAsync(PackManifest manifest, GameInstance instance, ContentManager? content,
        bool includeOptional = true, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var entries = Entries(manifest, includeOptional);
        var state = NewState(manifest);
        var result = await WriteAsync(manifest, instance, entries.Values.ToList(), [], null, content, state, progress, ct).ConfigureAwait(false);
        await state.SaveAsync(instance.MetadataDirectory, ct).ConfigureAwait(false);
        return result;
    }

    private static PackState NewState(PackManifest m) => new()
    {
        Format = m.Format,
        Name = m.Name,
        Version = m.Version,
        ProjectId = m.Source?.ProjectId,
        VersionId = m.Source?.VersionId,
        Url = m.Source?.Url,
        MinecraftVersion = m.MinecraftVersion,
        LoaderUid = m.LoaderUid,
        LoaderVersion = m.LoaderVersion,
        InstalledAt = DateTimeOffset.UtcNow,
    };

    // ── Updates ───────────────────────────────────────────────────────────

    /// <summary>Compares the installed pack (state + disk) with a new version of it.</summary>
    public static async Task<PackUpdatePlan> PlanUpdateAsync(GameInstance instance, PackManifest manifest, bool includeOptional = true, CancellationToken ct = default)
    {
        var previous = PackState.Load(instance.MetadataDirectory);
        var entries = Entries(manifest, includeOptional);
        var components = instance.Manifest.Components;
        var mc = components.FirstOrDefault(c => c.Uid == ComponentUids.Minecraft);
        var loader = ComponentResolver.PrimaryLoader(instance.Manifest);
        var plan = new PackUpdatePlan
        {
            Manifest = manifest,
            Entries = entries,
            Previous = previous,
            NewMinecraft = mc?.Version != manifest.MinecraftVersion && mc?.Pinned != true ? manifest.MinecraftVersion : null,
            NewLoader = (loader?.Uid != manifest.LoaderUid || loader?.Version != manifest.LoaderVersion) && loader?.Pinned != true
                ? (manifest.LoaderUid, manifest.LoaderVersion)
                : null,
        };

        var old = previous?.Files ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in old.Keys.Union(entries.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var disk = OnDisk(instance.GameDirectory, path);
            entries.TryGetValue(path, out var entry);
            old.TryGetValue(path, out var oldSha1);
            var diskSha1 = disk is null ? null : await HashAsync(disk, "sha1", ct).ConfigureAwait(false);

            PackChangeKind? kind;
            if (entry is not null && oldSha1 is not null)
            {
                if (disk is null)
                    kind = PackChangeKind.Add;
                else if (await MatchesAsync(disk, entry, diskSha1, ct).ConfigureAwait(false))
                    kind = null; // Already what the new version wants.
                else if (HashUtil.HashEquals(diskSha1, oldSha1))
                    kind = PackChangeKind.Replace;
                else
                    kind = PackChangeKind.Modified;
            }
            else if (oldSha1 is not null)
            {
                kind = disk is null ? null : HashUtil.HashEquals(diskSha1, oldSha1) ? PackChangeKind.Remove : PackChangeKind.Modified;
            }
            else
            {
                kind = disk is null ? PackChangeKind.Add : await MatchesAsync(disk, entry!, diskSha1, ct).ConfigureAwait(false) ? null : PackChangeKind.Conflict;
            }

            if (kind is { } k)
                plan.Changes.Add(new PackChange(path, k, entry));
            else
                plan.Unchanged++;
        }
        return plan;
    }

    /// <summary>Applies the chosen changes, updates the instance's components and records the new state.</summary>
    public async Task<PackInstallResult> ApplyUpdateAsync(PackUpdatePlan plan, GameInstance instance, ContentManager? content, Func<GameInstance, Task> saveInstance,
        Action<string>? delete = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var writes = plan.Changes.Where(c => c.Apply && c.Entry is not null && c.Kind != PackChangeKind.Remove).Select(c => c.Entry!).ToList();
        var removes = plan.Changes.Where(c => c.Apply && (c.Kind == PackChangeKind.Remove || (c.Kind == PackChangeKind.Modified && c.Entry is null))).Select(c => c.Path).ToList();

        var state = NewState(plan.Manifest);
        foreach (var (path, entry) in plan.Entries)
        {
            // Unwritten entries keep the hash the pack meant them to have, so a kept user change stays "modified".
            if (entry.Sha1 is { } sha1)
                state.Files[path] = sha1;
            else if (plan.Previous?.Files.TryGetValue(path, out var previous) == true)
                state.Files[path] = previous;
        }

        var result = await WriteAsync(plan.Manifest, instance, writes, removes, delete, content, state, progress, ct).ConfigureAwait(false);

        var m = instance.Manifest;
        if (plan.NewMinecraft is { } mc && m.Components.FirstOrDefault(c => c.Uid == ComponentUids.Minecraft) is { } mcRef)
            mcRef.Version = mc;
        if (plan.NewLoader is { } loader)
        {
            m.Components.RemoveAll(c => c.Uid != ComponentUids.Minecraft && ComponentResolver.PrimaryLoaders.Contains(c.Uid) && c.Uid != ComponentUids.OptiFine);
            if (loader.Uid is not null && loader.Version is not null)
                m.Components.Add(new ComponentRef { Uid = loader.Uid, Version = loader.Version });
        }
        if (plan.HasComponentChanges)
        {
            foreach (var c in m.Components)
                c.ProfileId = null;
        }
        InstanceSettingsDiff.Overlay(m.Settings, new InstanceSettings { MaxMemoryMb = m.Settings.MaxMemoryMb is null ? plan.Manifest.Settings.MaxMemoryMb : null });
        await saveInstance(instance).ConfigureAwait(false);
        await state.SaveAsync(instance.MetadataDirectory, ct).ConfigureAwait(false);
        return result;
    }

    // ── Writing files ─────────────────────────────────────────────────────

    private async Task<PackInstallResult> WriteAsync(PackManifest manifest, GameInstance instance, List<PackEntry> writes, List<string> removes,
        Action<string>? delete, ContentManager? content, PackState state, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var gameDir = instance.GameDirectory;
        Directory.CreateDirectory(gameDir);
        var warnings = new List<string>(manifest.Warnings);
        var blocked = new List<PackFile>();
        var staging = Path.Combine(instance.MetadataDirectory, "pack-download");
        Directory.CreateDirectory(staging);

        // Mods the user disabled that this update replaces with a new file: the new version stays disabled.
        var disabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in removes.Where(p => p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)))
        {
            if (OnDisk(gameDir, path) is { } file && file.EndsWith(ContentLocation.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                disabledIds.UnionWith(ModIds(file));
        }

        // 1. Downloads (into staging, so a failure never leaves half-written files in the game folder).
        var downloads = writes.Where(w => w.File is { IsBlocked: false } f && f.Urls.Count > 0).ToList();
        var items = downloads.Select((w, i) => new DownloadItem(
            w.File!.Urls.SelectMany(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && !uri.IsFile ? mirrors.Candidates(uri.AbsoluteUri) : []).Distinct().ToList(),
            Path.Combine(staging, $"{i:D5}.part"), w.File.Sha1, w.File.Size)
        { DisplayName = Path.GetFileName(w.Path) }).ToList();

        // A local packwiz folder references its own files; only a pack read from disk may do that.
        var localPack = manifest.Source?.Url is { } src && Uri.TryCreate(src, UriKind.Absolute, out var srcUri) && srcUri.IsFile;
        var remote = new List<DownloadItem>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Sources.Count > 0)
            {
                remote.Add(items[i]);
                continue;
            }
            var local = downloads[i].File!.Urls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null).FirstOrDefault(p => p is not null);
            if (!localPack || local is null || !File.Exists(local))
                throw new InvalidDataException($"{downloads[i].Path} has no usable download.");
            File.Copy(local, items[i].Path, overwrite: true);
            if (items[i].Sha1 is { } sha1 && !HashUtil.HashEquals(await HashUtil.Sha1FileAsync(items[i].Path, ct).ConfigureAwait(false), sha1))
                throw new InvalidDataException($"{downloads[i].Path} doesn't match the pack's hash.");
        }
        if (remote.Count > 0)
            await downloader.DownloadAllAsync(remote, progress, ct).ConfigureAwait(false);

        for (var i = 0; i < downloads.Count; i++)
        {
            var w = downloads[i];
            var tmp = items[i].Path;
            if (w.Hash is not null && w.Algorithm != "sha1" && !HashUtil.HashEquals(await HashAsync(tmp, w.Algorithm, ct).ConfigureAwait(false), w.Hash))
                throw new InvalidDataException($"{w.Path}: the downloaded file doesn't match the pack's {w.Algorithm} hash.");
            if (w.File!.ExtractToSaves)
            {
                ExtractWorld(tmp, gameDir);
                File.Delete(tmp);
                continue;
            }
            var target = KeepDisabled(await PlaceAsync(gameDir, w.Path, tmp, ct).ConfigureAwait(false), w.Path, disabledIds);
            state.Files[w.Path] = await HashUtil.Sha1FileAsync(target, ct).ConfigureAwait(false);
            await RecordAsync(content, w, target, ct).ConfigureAwait(false);
        }

        // 2. Overrides from the archive (after downloads: they win on the same path, as the formats specify).
        var overrides = writes.Where(w => w.Override is not null).ToList();
        if (overrides.Count > 0 && manifest.ArchivePath is { } archive)
        {
            using var zip = ZipNames.OpenRead(archive);
            foreach (var w in overrides)
            {
                ct.ThrowIfCancellationRequested();
                if (zip.GetEntry(w.Override!.Entry) is not { } e)
                    continue;
                var tmp = Path.Combine(staging, "override.part");
                e.ExtractToFile(tmp, overwrite: true);
                var target = KeepDisabled(await PlaceAsync(gameDir, w.Path, tmp, ct).ConfigureAwait(false), w.Path, disabledIds);
                state.Files[w.Path] = w.Hash ?? await HashUtil.Sha1FileAsync(target, ct).ConfigureAwait(false);
            }
        }

        // 3. Files the author doesn't allow launchers to download.
        foreach (var w in writes.Where(w => w.File is { IsBlocked: true }))
        {
            var existing = OnDisk(gameDir, w.Path);
            if (existing is null || !HashUtil.HashEquals(await HashAsync(existing, "sha1", ct).ConfigureAwait(false), w.File!.Sha1))
                blocked.Add(w.File!);
            if (w.File!.Sha1 is { } sha1)
                state.Files[w.Path] = sha1;
        }

        // 4. Removals.
        foreach (var path in removes)
        {
            if (OnDisk(gameDir, path) is { } file)
            {
                if (delete is not null)
                    delete(file);
                else
                    File.Delete(file);
            }
            state.Files.Remove(path);
        }

        try
        {
            Directory.Delete(staging, recursive: true);
        }
        catch (IOException)
        {
        }
        return new PackInstallResult(blocked, warnings);
    }

    /// <summary>Moves into place, keeping a user-disabled file disabled (<c>.disabled</c> suffix).</summary>
    private static async Task<string> PlaceAsync(string gameDir, string relative, string source, CancellationToken ct)
    {
        var target = PackPaths.Resolve(gameDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var disabled = target + ContentLocation.DisabledSuffix;
        if (!File.Exists(target) && File.Exists(disabled))
            target = disabled;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(target))
                    File.SetAttributes(target, FileAttributes.Normal);
                File.Move(source, target, overwrite: true);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 6)
            {
                await Task.Delay(150 * attempt, ct).ConfigureAwait(false); // Antivirus/indexer holding a fresh file.
            }
        }
    }

    /// <summary>A new file for a mod the user had disabled (same mod id) is placed disabled too.</summary>
    private static string KeepDisabled(string target, string relative, HashSet<string> disabledIds)
    {
        if (disabledIds.Count == 0 || target.EndsWith(ContentLocation.DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            || !relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) || !ModIds(target).Any(disabledIds.Contains))
            return target;
        File.Move(target, target + ContentLocation.DisabledSuffix, overwrite: true);
        return target + ContentLocation.DisabledSuffix;
    }

    private static List<string> ModIds(string jar)
    {
        try
        {
            return Metadata.ContentMetadataReader.Read(jar).Mods.Select(m => m.Id).Where(id => id.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void ExtractWorld(string zipPath, string gameDir)
    {
        var saves = Path.Combine(gameDir, "saves");
        using var zip = ZipNames.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || PackPaths.Sanitize(e.FullName) is not { } rel)
                continue;
            var target = PackPaths.Resolve(saves, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }

    private static async Task RecordAsync(ContentManager? content, PackEntry w, string target, CancellationToken ct)
    {
        if (content is null || PackPaths.LocationOf(w.Path) is not { } location)
            return;
        var hashes = await ContentHashes.ComputeAsync(target, ct).ConfigureAwait(false);
        var info = new FileInfo(target);
        var key = ContentManager.KeyFor(location, Path.GetFileName(w.Path));
        await content.UpdateAsync(index =>
        {
            index.Files.TryGetValue(key, out var previous);
            index.Files[key] = new ContentRecord
            {
                Size = info.Length,
                ModifiedUtc = info.LastWriteTimeUtc,
                Sha1 = hashes.Sha1,
                Sha512 = hashes.Sha512,
                Murmur2 = hashes.Murmur2,
                Source = w.File?.Source ?? previous?.Source,
                Title = w.File?.Title ?? previous?.Title,
                Pinned = previous?.Pinned ?? false,
                IdentifiedAt = w.File?.Source is null ? null : DateTimeOffset.UtcNow,
            };
            return true;
        }, ct).ConfigureAwait(false);
    }

    private static string? OnDisk(string gameDir, string relative)
    {
        var path = PackPaths.Resolve(gameDir, relative);
        return File.Exists(path) ? path : File.Exists(path + ContentLocation.DisabledSuffix) ? path + ContentLocation.DisabledSuffix : null;
    }

    private static async Task<bool> MatchesAsync(string disk, PackEntry entry, string? diskSha1, CancellationToken ct)
    {
        if (entry.Hash is null)
            return false;
        return entry.Algorithm == "sha1"
            ? HashUtil.HashEquals(diskSha1, entry.Hash)
            : HashUtil.HashEquals(await HashAsync(disk, entry.Algorithm, ct).ConfigureAwait(false), entry.Hash);
    }

    /// <summary>sha1, sha256, sha512, md5, or murmur2 (CurseForge fingerprint, decimal).</summary>
    public static async Task<string> HashAsync(string path, string algorithm, CancellationToken ct = default)
    {
        if (algorithm == "murmur2")
            return (await ContentHashes.ComputeAsync(path, ct).ConfigureAwait(false)).Murmur2.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        byte[] hash = algorithm switch
        {
            "sha1" => await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false),
            "sha256" => await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false),
            "sha512" => await SHA512.HashDataAsync(fs, ct).ConfigureAwait(false),
#pragma warning disable CA5351 // packwiz allows md5; it only checks integrity here.
            "md5" => await MD5.HashDataAsync(fs, ct).ConfigureAwait(false),
#pragma warning restore CA5351
            _ => throw new NotSupportedException($"Unknown hash format '{algorithm}'."),
        };
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>A planned file for the manual-download flow (<see cref="ContentService.CompleteManualAsync"/>).</summary>
    public static PlannedFile? ToPlannedFile(PackFile file)
    {
        if (PackPaths.LocationOf(file.Path) is not { } location || file.Source is not { } source)
            return null;
        var version = new RemoteVersion(source.Platform, source.ProjectId, source.VersionId, source.VersionNumber ?? Path.GetFileName(file.Path), source.VersionNumber ?? "")
        {
            Files = [new RemoteFile(Path.GetFileName(file.Path), file.Size ?? 0) { Sha1 = file.Sha1, ManualDownloadUrl = file.ManualDownloadUrl, Primary = true }],
        };
        return new PlannedFile(version, version.Files[0], location, IsDependency: false)
        {
            Project = new RemoteProject(source.Platform, source.ProjectId, source.ProjectId, file.Title ?? Path.GetFileName(file.Path)),
        };
    }
}
