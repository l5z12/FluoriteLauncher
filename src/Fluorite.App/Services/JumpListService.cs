using Fluorite.Core.Instances;
using Fluorite.Core.Launch;
using Fluorite.Core.Worlds;
using Fluorite.Platform.Windows;
using Microsoft.Extensions.Logging;
using Windows.UI.StartScreen;

namespace Fluorite.App.Services;

/// <summary>
/// Taskbar/Start jump list (requires package identity): recently played instances, and their recent worlds when the
/// game can open a world directly (Quick Play).
/// </summary>
public sealed class JumpListService(ILogger<JumpListService> logger, QuickPlaySupport quickPlay, SettingsService settings)
{
    public static bool IsSupported => PackageInfo.HasPackageIdentity && JumpList.IsSupported();

    public async Task UpdateAsync(IEnumerable<GameInstance> instances)
    {
        if (!IsSupported)
            return;
        try
        {
            var recent = instances
                .Where(i => !settings.Current.IsHidden(i.Manifest))
                .OrderByDescending(i => i.Manifest.Stats.LastPlayed ?? i.Manifest.Created)
                .Take(6)
                .ToList();

            var worlds = new List<(GameInstance Instance, WorldInfo World)>();
            foreach (var i in recent.Where(i => i.Manifest.Stats.LastPlayed is not null).Take(3))
            {
                if (await quickPlay.CanOpenWorldsAsync(i))
                    worlds.AddRange((await Task.Run(() => new WorldStore(i.GameDirectory).GetWorlds())).Where(w => w.Error is null).Take(2).Select(w => (i, w)));
            }

            var list = await JumpList.LoadCurrentAsync();
            list.SystemGroupKind = JumpListSystemGroupKind.None;
            list.Items.Clear();
            var logo = new Uri("ms-appx:///Assets/Square44x44Logo.png");

            var worldGroup = Loc.Get("JumpList_RecentWorlds");
            foreach (var (i, w) in worlds.OrderByDescending(x => x.World.LastPlayed).Take(4))
            {
                var item = JumpListItem.CreateWithArguments(ActivationRequest.LaunchArguments(i.Id, new QuickPlay(QuickPlayKind.Singleplayer, w.FolderName)), w.Name);
                item.GroupName = worldGroup;
                item.Description = Loc.Format("JumpList_PlayWorld", w.Name, i.Name);
                item.Logo = logo;
                list.Items.Add(item);
            }

            var group = Loc.Get("JumpList_Recent");
            foreach (var i in recent)
            {
                var item = JumpListItem.CreateWithArguments(ActivationRequest.LaunchArguments(i.Id), i.Name);
                item.GroupName = group;
                item.Description = Loc.Format("JumpList_Play", i.Name);
                item.Logo = logo;
                list.Items.Add(item);
            }
            await list.SaveAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Jump list update failed");
        }
    }
}
