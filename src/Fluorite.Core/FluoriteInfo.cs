using System.Reflection;

namespace Fluorite.Core;

/// <summary>Static identity of the launcher, used for <c>${launcher_name}</c>, User-Agent headers, etc.</summary>
public static class FluoriteInfo
{
    public const string LauncherName = "FluoriteLauncher";

    /// <summary>Public project URL used in the User-Agent (Modrinth requires a contactable UA). Never a personal email.</summary>
    public const string ProjectUrl = "https://github.com/l5z12/FluoriteLauncher";

    public static string Version { get; } =
        typeof(FluoriteInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string UserAgent => $"{LauncherName}/{Version} (+{ProjectUrl})";
}
