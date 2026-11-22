namespace Fluorite.Core.Instances;

/// <summary>Where an external root keeps Fluorite's sidecars, and how instances of it are named.</summary>
public sealed record ExternalLayoutContext(string RootId, OtherLauncher Launcher, string SidecarRoot, GameDirectories FluoriteStore, bool AllowWriteBack);

/// <summary>
/// Base for another launcher's folder used in place. Subclasses read that launcher's metadata into a native
/// <see cref="InstanceManifest"/>; Fluorite's changes are overlaid from (and saved to) a sidecar, unless the root
/// allows write-back and the subclass supports it.
/// </summary>
public abstract class ExternalLayout(string root, ExternalLayoutContext context) : IGameLayout
{
    public abstract LayoutKind Kind { get; }
    public string Root { get; } = Path.GetFullPath(root);
    public ExternalLayoutContext Context { get; } = context;

    /// <summary>One instance's native view: the manifest built from the other launcher's files, before the sidecar.</summary>
    protected sealed record NativeInstance(
        string LocalId, string Directory, string GameDirectory, GameDirectories Store, InstanceManifest Manifest, IReadOnlyList<string> Warnings);

    protected abstract IEnumerable<NativeInstance> ReadAll();

    protected abstract NativeInstance? Read(string localId);

    /// <summary>Whether this layout can write its own format (and the root allows it).</summary>
    protected virtual bool SupportsWriteBack => false;

    /// <summary>Writes the instance's settings/name/components/stats in the launcher's own format.</summary>
    protected virtual void WriteBack(GameInstance instance, NativeInstance native) => throw new NotSupportedException();

    public bool WritesBack => SupportsWriteBack && Context.AllowWriteBack;

    public string SidecarDirectory(string localId) => Path.Combine(Context.SidecarRoot, InstanceSidecar.FolderName(localId));

    public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<GameInstance>>(() => ReadAll().Select(ToInstance).ToList(), ct);

    public Task<GameInstance?> GetAsync(string localId, CancellationToken ct = default) =>
        Task.Run(() => Read(localId) is { } n ? ToInstance(n) : null, ct);

    private GameInstance ToInstance(NativeInstance n)
    {
        var sidecarDir = SidecarDirectory(n.LocalId);
        InstanceSidecar.Load(sidecarDir).ApplyTo(n.Manifest);
        string launchId;
        try
        {
            launchId = ComponentResolver.LaunchVersionId(n.Manifest);
        }
        catch (InvalidDataException)
        {
            launchId = n.LocalId;
        }
        return new GameInstance
        {
            Id = $"{Context.RootId}:{n.LocalId}",
            LocalId = n.LocalId,
            RootId = Context.RootId,
            Launcher = Context.Launcher,
            Layout = Kind,
            Directory = n.Directory,
            GameDirectory = n.GameDirectory,
            Store = n.Store,
            Manifest = n.Manifest,
            LaunchVersionId = launchId,
            MetadataDirectory = sidecarDir,
            ContentDirectory = Path.Combine(Context.SidecarRoot, "content", InstanceSidecar.FolderName(Path.GetFullPath(n.GameDirectory).ToUpperInvariant())),
            CanEditComponents = WritesBack,
            Warnings = n.Warnings,
        };
    }

    public async Task SaveAsync(GameInstance instance, CancellationToken ct = default)
    {
        var native = await Task.Run(() => Read(instance.LocalId), ct).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException($"'{instance.Name}' no longer exists in {Root}.");
        var sidecarDir = SidecarDirectory(instance.LocalId);
        var previous = InstanceSidecar.Load(sidecarDir);
        if (WritesBack)
        {
            WriteBack(instance, native);
            native = Read(instance.LocalId) ?? native;
        }
        var sidecar = InstanceSidecar.Diff(instance.Manifest, native.Manifest, previous);
        await sidecar.SaveAsync(sidecarDir, ct).ConfigureAwait(false);
    }

    protected static DateTimeOffset? FromUnixMs(long? ms) => ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value) : null;

    protected static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
