using Fluorite.Core.Launch;
using Microsoft.Win32;

namespace Fluorite.Platform.Windows.Diagnostics;

/// <summary>
/// Windows' per-program GPU choice (Settings › System › Display › Graphics), stored under
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c> as <c>GpuPreference=N;</c> next to other per-program
/// DirectX settings that are kept as they are.
/// </summary>
public static class GpuPreferences
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>Sets <paramref name="executable"/>'s preference; <see cref="GpuPreference.Default"/> changes nothing.</summary>
    public static void Apply(string executable, GpuPreference preference)
    {
        if (preference == GpuPreference.Default || !File.Exists(executable))
            return;
        var wanted = preference == GpuPreference.HighPerformance ? "2" : "1";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            var current = key.GetValue(executable) as string ?? "";
            var settings = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var index = settings.FindIndex(s => s.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && settings[index].EndsWith("=" + wanted, StringComparison.Ordinal))
                return;
            if (index >= 0)
                settings[index] = "GpuPreference=" + wanted;
            else
                settings.Add("GpuPreference=" + wanted);
            key.SetValue(executable, string.Join(";", settings) + ";", RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // A policy-locked registry only loses the preference.
        }
    }
}
