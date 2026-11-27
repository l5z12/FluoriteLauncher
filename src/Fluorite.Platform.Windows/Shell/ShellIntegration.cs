using System.Runtime.Versioning;
using System.Text;
using Fluorite.Core;
using Microsoft.Win32;

namespace Fluorite.Platform.Windows.Shell;

[SupportedOSPlatform("windows")]
public static class ShellIntegration
{
    public const string Scheme = "fluorite";

    /// <summary>
    /// Unpackaged builds register <c>fluorite://</c> per user (HKCU, no elevation). Packaged builds declare the protocol
    /// in the MSIX manifest instead, so this is a no-op there.
    /// </summary>
    public static void EnsureProtocolRegistered(string exePath)
    {
        if (PackageInfo.HasPackageIdentity)
            return;
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        key.SetValue("", "URL:Fluorite Launcher");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon"))
            icon.SetValue("", $"\"{exePath}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exePath}\" \"%1\"");
    }

    /// <summary>Writes an Internet Shortcut (.url) — works for packaged apps, unlike .lnk files pointing into WindowsApps.</summary>
    /// <returns>The path of the created file.</returns>
    public static string CreateUrlShortcut(string folder, string displayName, Uri target, string? iconPath)
    {
        var safe = new StringBuilder();
        foreach (var c in displayName)
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        var path = Path.Combine(folder, safe.ToString().Trim() + ".url");

        var sb = new StringBuilder();
        sb.AppendLine("[InternetShortcut]");
        sb.Append("URL=").AppendLine(target.AbsoluteUri);
        if (iconPath is not null)
        {
            sb.Append("IconFile=").AppendLine(iconPath);
            sb.AppendLine("IconIndex=0");
        }
        AtomicFile.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
