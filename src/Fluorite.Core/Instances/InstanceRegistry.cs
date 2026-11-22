using Fluorite.Core.Settings;

namespace Fluorite.Core.Instances;

/// <summary>
/// Every instance Fluorite can launch: its own (<see cref="FluoriteLayout"/>) plus those of other launchers' folders
/// registered as <see cref="ExternalRoot"/>s, used in place. Routes saves to the right layout.
/// </summary>
public sealed class InstanceRegistry(FluoriteLayout fluorite, Func<IReadOnlyList<ExternalRoot>> roots)
{
    private readonly Dictionary<string, (ExternalRoot Config, ExternalLayout Layout)> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public FluoriteLayout Fluorite { get; } = fluorite;

    /// <summary>Roots that couldn't be read on the last listing, with the reason.</summary>
    public IReadOnlyDictionary<string, string> RootErrors => _errors;
    private Dictionary<string, string> _errors = [];

    public string SidecarRoot(string rootId) => Path.Combine(Fluorite.Root, "external", rootId);

    public ExternalLayout? LayoutFor(string rootId)
    {
        var config = roots().FirstOrDefault(r => string.Equals(r.Id, rootId, StringComparison.OrdinalIgnoreCase));
        if (config is null)
            return null;
        lock (_lock)
        {
            if (_layouts.TryGetValue(rootId, out var cached) && Same(cached.Config, config))
                return cached.Layout;
            var context = new ExternalLayoutContext(config.Id, config.Launcher, SidecarRoot(config.Id), Fluorite.Store, config.AllowWriteBack);
            var layout = CreateLayout(config.Kind, config.Path, context);
            _layouts[rootId] = (Clone(config), layout);
            return layout;
        }
    }

    /// <summary>
    /// A read-only view of a launcher folder that isn't (or needn't be) a root, e.g. to pick instances to copy from.
    /// Its sidecar is a throwaway folder in the cache, so nothing is written next to the real roots' overrides.
    /// </summary>
    public ExternalLayout Browse(LayoutKind kind, OtherLauncher launcher, string path) =>
        CreateLayout(kind, path, new ExternalLayoutContext("browse", launcher, Path.Combine(Fluorite.Root, "cache", "browse-" + kind.ToString().ToLowerInvariant()), Fluorite.Store, AllowWriteBack: false));

    private static ExternalLayout CreateLayout(LayoutKind kind, string path, ExternalLayoutContext context) => kind switch
    {
        LayoutKind.MultiMC => new MultiMCLayout(path, context),
        LayoutKind.Official => new OfficialLayout(path, context),
        LayoutKind.Isolated => new IsolatedLayout(path, context),
        _ => throw new NotSupportedException($"Layout {kind} can't be used as an external root."),
    };

    public async Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken ct = default)
    {
        var all = new List<GameInstance>(await Fluorite.GetInstancesAsync(ct).ConfigureAwait(false));
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots().Where(r => r.Enabled))
        {
            try
            {
                if (!Directory.Exists(root.Path))
                {
                    errors[root.Id] = $"{root.Path} doesn't exist.";
                    continue;
                }
                if (LayoutFor(root.Id) is { } layout)
                    all.AddRange(await layout.GetInstancesAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors[root.Id] = ex.Message;
            }
        }
        _errors = errors;
        return all;
    }

    /// <summary>By global id: <c>&lt;folder&gt;</c> for Fluorite instances, <c>&lt;rootId&gt;:&lt;localId&gt;</c> for external ones.</summary>
    public async Task<GameInstance?> GetAsync(string id, CancellationToken ct = default)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && LayoutFor(id[..colon]) is { } layout)
            return await layout.GetAsync(id[(colon + 1)..], ct).ConfigureAwait(false);
        return await Fluorite.GetAsync(id, ct).ConfigureAwait(false);
    }

    public Task SaveAsync(GameInstance instance, CancellationToken ct = default) =>
        instance.RootId is { } rootId
            ? (LayoutFor(rootId) ?? throw new InvalidOperationException($"'{instance.Name}' belongs to a folder that was removed from Fluorite.")).SaveAsync(instance, ct)
            : Fluorite.SaveAsync(instance, ct);

    /// <summary>Whether a change to <paramref name="instance"/> also reaches its own launcher (always, for Fluorite's).</summary>
    public bool WritesBack(GameInstance instance) => instance.RootId is not { } rootId || LayoutFor(rootId) is { WritesBack: true };

    /// <summary>
    /// Gives the instance an icon: a copy of <paramref name="imagePath"/>, or the built-in <paramref name="builtInKey"/>,
    /// or (both null) none, which shows its letter tile. Another launcher's instance keeps the copy in Fluorite's notes
    /// beside it, and with write-back Prism gets it too. The icon it replaces is deleted if Fluorite made it.
    /// </summary>
    public async Task SetIconAsync(GameInstance instance, string? imagePath, string? builtInKey, CancellationToken ct = default)
    {
        var dir = instance.IsExternal ? instance.MetadataDirectory : instance.Directory;
        var old = instance.Manifest.Icon;
        string? copy = null;
        if (imagePath is not null)
        {
            copy = await Task.Run(() => InstanceIcons.Import(imagePath, dir), ct).ConfigureAwait(false);
            (instance.Manifest.Icon, instance.Manifest.IconKey) = (copy, null);
        }
        else
        {
            (instance.Manifest.Icon, instance.Manifest.IconKey) = (null, builtInKey);
        }
        await SaveAsync(instance, ct).ConfigureAwait(false);
        foreach (var unused in new[] { old, copy }.Where(p => p is not null && !string.Equals(p, instance.Manifest.Icon, StringComparison.OrdinalIgnoreCase)))
            InstanceIcons.DeleteUnused(unused, dir, includeIconFiles: !instance.IsExternal);
    }

    private static bool Same(ExternalRoot a, ExternalRoot b) =>
        a.Path == b.Path && a.Kind == b.Kind && a.AllowWriteBack == b.AllowWriteBack && a.Launcher == b.Launcher;

    private static ExternalRoot Clone(ExternalRoot r) => new() { Id = r.Id, Path = r.Path, Kind = r.Kind, Launcher = r.Launcher, Enabled = r.Enabled, AllowWriteBack = r.AllowWriteBack };

    // ── Adding roots ──────────────────────────────────────────────────────

    /// <summary>
    /// Works out what kind of launcher folder <paramref name="path"/> is: a Prism/MultiMC data folder (or its
    /// <c>instances</c> folder), an official <c>.minecraft</c>, or an HMCL/PCL <c>.minecraft</c> with isolated versions.
    /// Also accepts a folder that contains <c>.minecraft</c>. Returns the normalised path.
    /// </summary>
    public static (LayoutKind Kind, OtherLauncher Launcher, string Path)? Detect(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(path))
            return null;

        if (File.Exists(System.IO.Path.Combine(path, "prismlauncher.cfg")) || File.Exists(System.IO.Path.Combine(path, "polymc.cfg")))
            return (LayoutKind.MultiMC, OtherLauncher.PrismLauncher, path);
        if (File.Exists(System.IO.Path.Combine(path, "multimc.cfg")))
            return (LayoutKind.MultiMC, OtherLauncher.MultiMC, path);
        if (HasInstanceFolders(System.IO.Path.Combine(path, "instances")))
            return (LayoutKind.MultiMC, OtherLauncher.PrismLauncher, path);
        if (HasInstanceFolders(path))
        {
            // The instances folder itself; prefer its parent when that's the data folder.
            var parent = System.IO.Path.GetDirectoryName(path);
            return parent is not null && (File.Exists(System.IO.Path.Combine(parent, "prismlauncher.cfg")) || File.Exists(System.IO.Path.Combine(parent, "multimc.cfg")))
                ? Detect(parent)
                : (LayoutKind.MultiMC, OtherLauncher.PrismLauncher, path);
        }

        var dotMinecraft = System.IO.Path.Combine(path, ".minecraft");
        if (!Directory.Exists(System.IO.Path.Combine(path, "versions")) && Directory.Exists(dotMinecraft))
            return Detect(dotMinecraft) is { } inner ? inner with { Launcher = LooksLikePcl(path) ? OtherLauncher.Pcl : inner.Launcher } : null;

        var versions = System.IO.Path.Combine(path, "versions");
        if (!Directory.Exists(versions) && !File.Exists(System.IO.Path.Combine(path, "launcher_profiles.json")))
            return null;

        if (IsIsolated(path))
            return (LayoutKind.Isolated, LooksLikePcl(path) || LooksLikePcl(System.IO.Path.GetDirectoryName(path)) ? OtherLauncher.Pcl : OtherLauncher.Hmcl, path);
        return File.Exists(System.IO.Path.Combine(path, "launcher_profiles.json")) || File.Exists(System.IO.Path.Combine(path, "launcher_profiles_microsoft_store.json"))
            ? (LayoutKind.Official, OtherLauncher.Official, path)
            : (LayoutKind.Isolated, OtherLauncher.Hmcl, path);
    }

    /// <summary>A <c>.minecraft</c> where some version keeps its own game data (HMCL/PCL "version isolation").</summary>
    public static bool IsIsolated(string dotMinecraft)
    {
        var versions = System.IO.Path.Combine(dotMinecraft, "versions");
        try
        {
            return Directory.Exists(versions) && Directory.EnumerateDirectories(versions).Any(d =>
                File.Exists(System.IO.Path.Combine(d, "hmclversion.cfg"))
                || Directory.Exists(System.IO.Path.Combine(d, ".hmcl"))
                || Directory.Exists(System.IO.Path.Combine(d, "PCL"))
                || Directory.Exists(System.IO.Path.Combine(d, "saves"))
                || Directory.Exists(System.IO.Path.Combine(d, "mods")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksLikePcl(string? dir)
    {
        if (dir is null || !Directory.Exists(dir))
            return false;
        try
        {
            return Directory.Exists(System.IO.Path.Combine(dir, "PCL"))
                || Directory.EnumerateFiles(dir, "Plain Craft Launcher*.exe").Any()
                || (Directory.Exists(System.IO.Path.Combine(dir, "versions"))
                    && Directory.EnumerateDirectories(System.IO.Path.Combine(dir, "versions")).Any(d => Directory.Exists(System.IO.Path.Combine(d, "PCL"))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasInstanceFolders(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any(d => File.Exists(System.IO.Path.Combine(d, "instance.cfg")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A short id for a new root: <c>prism</c>, <c>official</c>, <c>hmcl-2</c>…</summary>
    public static string ProposeId(OtherLauncher launcher, IEnumerable<string> taken)
    {
        var baseId = launcher switch
        {
            OtherLauncher.PrismLauncher => "prism",
            OtherLauncher.MultiMC => "multimc",
            OtherLauncher.Official => "official",
            OtherLauncher.Hmcl => "hmcl",
            OtherLauncher.Pcl => "pcl",
            _ => launcher.ToString().ToLowerInvariant(),
        };
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = baseId;
        for (var i = 2; used.Contains(id); i++)
            id = $"{baseId}-{i}";
        return id;
    }
}
