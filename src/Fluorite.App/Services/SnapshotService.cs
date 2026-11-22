using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Platform.Windows.Shell;
using Microsoft.Extensions.Logging;

namespace Fluorite.App.Services;

/// <summary>
/// Snapshots of instances (mods, configs, packs, loader setup) in the data folder: taken by hand, before updates
/// (unless turned off), and before every restore so a restore can be undone too.
/// </summary>
public sealed class SnapshotService(InstanceRegistry registry, ContentService content, SettingsService settings, ILogger<SnapshotService> logger)
{
    /// <summary>Automatic snapshots kept per instance; the user's own are kept until deleted.</summary>
    public const int AutomaticToKeep = 5;

    private readonly InstanceSnapshots _store = new(Path.Combine(AppPaths.DataRoot, "snapshots"), HardLink.TryCreate);

    /// <summary>An instance's snapshots changed (its id).</summary>
    public event EventHandler<string>? Changed;

    public string DirectoryFor(GameInstance instance) => _store.DirectoryFor(instance);

    public IReadOnlyList<InstanceSnapshot> List(GameInstance instance) => _store.List(instance);

    public async Task<InstanceSnapshot> TakeAsync(GameInstance instance, string? label, bool automatic)
    {
        // What Fluorite knows about the mods (source, pins) goes with them.
        var snapshot = await _store.CreateAsync(instance, label, automatic, [content.For(instance).IndexPath]);
        if (automatic)
            await Task.Run(() => _store.Prune(instance, AutomaticToKeep));
        Changed?.Invoke(this, instance.Id);
        return snapshot;
    }

    /// <summary>Before an update, when that's turned on. Never stops the update: a failed snapshot is only logged.</summary>
    public async Task TakeBeforeUpdateAsync(GameInstance instance, string label)
    {
        if (!settings.Current.SnapshotBeforeUpdates)
            return;
        try
        {
            await TakeAsync(instance, label, automatic: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Snapshot before update of {Instance} failed", instance.Id);
        }
    }

    /// <summary>
    /// Back to the snapshot: first a snapshot of how things are now (so this can be undone), then its files and
    /// loader setup. The game must not be running (the caller checks).
    /// </summary>
    public async Task RestoreAsync(GameInstance instance, InstanceSnapshot snapshot)
    {
        await TakeAsync(instance, Loc.Get("Snapshot_BeforeRestore"), automatic: true);
        var components = await _store.RestoreAsync(instance, snapshot);
        await content.For(instance).ReloadIndexAsync();
        if (instance.CanEditComponents && components.Count > 0)
        {
            instance.Manifest.Components = components;
            await registry.SaveAsync(instance);
        }
        Changed?.Invoke(this, instance.Id);
    }
}
