using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Microsoft.Extensions.Logging;

namespace Fluorite.App.Services;

/// <summary>
/// How many mod updates each instance has, for the badge on its Library card. Checked quietly in the background,
/// one instance at a time and at most every few hours each; the Mods tab reports what it finds too.
/// </summary>
public sealed class UpdateBadgeService(ContentService content, ILogger<UpdateBadgeService> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private readonly Dictionary<string, (int Count, DateTime CheckedUtc)> _counts = new(StringComparer.OrdinalIgnoreCase);
    private Task? _running;

    /// <summary>An instance's count changed (its id); raised on the UI thread.</summary>
    public event EventHandler<string>? Changed;

    public int CountFor(string instanceId) => _counts.TryGetValue(instanceId, out var c) ? c.Count : 0;

    public void Report(GameInstance instance, int count)
    {
        var changed = CountFor(instance.Id) != count;
        _counts[instance.Id] = (count, DateTime.UtcNow);
        if (changed)
            Changed?.Invoke(this, instance.Id);
    }

    /// <summary>Checks the instances not checked lately, unless a check is already going.</summary>
    public void CheckInBackground(IReadOnlyList<GameInstance> instances)
    {
        if (_running is { IsCompleted: false })
            return;
        var now = DateTime.UtcNow;
        var due = instances
            .Where(i => ContentService.ModLoaderNames(i.Manifest).Count > 0)
            .Where(i => !_counts.TryGetValue(i.Id, out var c) || now - c.CheckedUtc > MaxAge)
            .ToList();
        if (due.Count > 0)
            _running = RunAsync(due);
    }

    private async Task RunAsync(List<GameInstance> due)
    {
        await Task.Delay(TimeSpan.FromSeconds(8)); // After the app (and the Library) settled.
        foreach (var instance in due)
        {
            try
            {
                var manager = content.For(instance);
                var location = new ContentLocation(ContentKind.Mod);
                var files = await Task.Run(() => manager.ScanAsync(location));
                if (files.Count == 0)
                {
                    Report(instance, 0);
                    continue;
                }
                // Mods never opened in the Mods tab may not be known yet (which project, which platform).
                if (await Task.Run(() => content.IdentifyAsync(manager, files)) > 0)
                    files = await Task.Run(() => manager.ScanAsync(location));
                var target = ContentService.TargetFor(instance.Manifest, ContentKind.Mod);
                var updates = await Task.Run(() => content.CheckUpdatesAsync(files, target));
                Report(instance, updates.Count);
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex, "Update check stopped (offline?)");
                return; // Offline: the next visit tries again.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
            {
                logger.LogDebug(ex, "Update check of {Instance} failed", instance.Id);
            }
        }
    }
}
