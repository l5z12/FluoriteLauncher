using Fluorite.Core.Instances;
using Fluorite.Core.Launch;
using Fluorite.Platform.Windows.Shell;

namespace Fluorite.App.Services;

/// <summary>Desktop shortcuts that launch an instance via <c>fluorite://launch/&lt;id&gt;</c>, optionally into a world or server.</summary>
public sealed class ShortcutService
{
    /// <param name="label">Shortcut name; the instance name when null.</param>
    public string CreateDesktopShortcut(GameInstance instance, QuickPlay? quickPlay = null, string? label = null)
    {
        // Packaged: the protocol comes from the manifest. Unpackaged: register it per-user on first use.
        if (Environment.ProcessPath is { } exe)
            ShellIntegration.EnsureProtocolRegistered(exe);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return ShellIntegration.CreateUrlShortcut(desktop, label ?? instance.Name, ActivationRequest.LaunchUri(instance.Id, quickPlay), StableIconPath());
    }

    /// <summary>The MSIX install folder changes on every update, so shortcuts point at a copy in the data root.</summary>
    private static string StableIconPath()
    {
        var target = Path.Combine(AppPaths.DataRoot, "icons", "fluorite.ico");
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        return target;
    }
}
