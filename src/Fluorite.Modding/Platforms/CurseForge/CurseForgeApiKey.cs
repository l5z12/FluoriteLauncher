namespace Fluorite.Modding.Platforms.CurseForge;

/// <summary>
/// Where the CurseForge API key comes from. No key is ever committed: release builds get Fluorite's own key from
/// CI (<c>-p:FluoriteCurseForgeApiKey</c>), and local builds read one from the git-ignored
/// <c>Directory.Build.local.props</c> or the environment. Without a key, CurseForge is turned off.
/// </summary>
public static class CurseForgeApiKey
{
    /// <summary>Environment override, then the build-time key; <see langword="null"/> when neither is set.</summary>
    public static string? Resolve(string? buildKey) =>
        Environment.GetEnvironmentVariable("FLUORITE_CURSEFORGE_API_KEY") is { Length: > 0 } env ? env
        : !string.IsNullOrWhiteSpace(buildKey) ? buildKey
        : null;
}
