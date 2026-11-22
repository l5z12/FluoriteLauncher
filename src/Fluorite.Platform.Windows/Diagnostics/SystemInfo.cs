using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Fluorite.Platform.Windows.Diagnostics;

public sealed record GraphicsAdapter(string Name, string? DriverVersion, string? DriverDate);

/// <summary>Hardware and Windows facts for diagnostics (read from the registry: fast, and no WMI).</summary>
public static class SystemInfo
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>"Windows 11 Pro 24H2 (26100.2605)".</summary>
    public static string WindowsVersion
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
                var ubr = key?.GetValue("UBR") is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : "";
                var product = key?.GetValue("ProductName") as string ?? "Windows";
                // ProductName still says "Windows 10" on Windows 11.
                if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b >= 22000)
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
                var display = key?.GetValue("DisplayVersion") as string;
                return $"{product}{(display is null ? "" : " " + display)} ({build}{ubr}), {RuntimeInformation.OSArchitecture}";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return $"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}";
            }
        }
    }

    public static string? ProcessorName
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim();
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Installed display adapters with their driver versions (old drivers cause many OpenGL crashes).</summary>
    public static IReadOnlyList<GraphicsAdapter> GraphicsAdapters()
    {
        var list = new List<GraphicsAdapter>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null)
                return list;
            foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                try
                {
                    using var adapter = cls.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is not string desc || desc.Length == 0)
                        continue;
                    // Remote/virtual display drivers aren't what the game renders with.
                    if (desc.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) || desc.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (list.Any(a => a.Name == desc))
                        continue;
                    list.Add(new GraphicsAdapter(desc, adapter.GetValue("DriverVersion") as string, adapter.GetValue("DriverDate") as string));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }
        return list;
    }
}
