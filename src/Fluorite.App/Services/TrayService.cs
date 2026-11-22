using CommunityToolkit.Mvvm.Input;
using Fluorite.Core.Instances;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.Services;

/// <summary>Notification-area icon used for close-to-tray and "hide while playing".</summary>
public sealed class TrayService(InstanceRegistry layout, GameRunner runner) : IDisposable
{
    private TaskbarIcon? _icon;

    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;

    public bool IsCreated => _icon is not null;

    public async Task EnsureAsync()
    {
        if (_icon is null)
        {
            _icon = new TaskbarIcon
            {
                ToolTipText = Loc.Get("AppTitle/Text"),
                IconSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"))),
                ContextMenuMode = ContextMenuMode.PopupMenu,
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(() => ShowRequested?.Invoke(this, EventArgs.Empty)),
            };
            _icon.ForceCreate(false);
        }
        _icon.ContextFlyout = await BuildMenuAsync();
    }

    private async Task<MenuFlyout> BuildMenuAsync()
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = Loc.Get("Tray_Open") };
        open.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);

        var recent = (await layout.GetInstancesAsync())
            .Where(i => !i.Manifest.Hidden)
            .OrderByDescending(i => i.Manifest.Stats.LastPlayed ?? i.Manifest.Created)
            .Take(5)
            .ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var instance in recent)
            {
                var item = new MenuFlyoutItem { Text = Loc.Format("JumpList_Play", instance.Name) };
                item.Click += (_, _) => runner.Launch(instance);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = Loc.Get("Tray_Exit") };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);
        return menu;
    }

    public void Remove()
    {
        _icon?.Dispose();
        _icon = null;
    }

    public void Dispose() => Remove();
}
