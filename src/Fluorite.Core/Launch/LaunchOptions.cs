namespace Fluorite.Core.Launch;

/// <summary>The authenticated identity passed to the game.</summary>
/// <param name="Uuid">Profile UUID without dashes.</param>
/// <param name="UserType"><c>msa</c>, <c>mojang</c> (Yggdrasil/authlib-injector) or <c>legacy</c> (offline).</param>
public sealed record GameSession(string PlayerName, string Uuid, string AccessToken, string UserType, string? Xuid = null, string? ClientId = null)
{
    /// <summary>JVM arguments required by the account type (e.g. <c>-javaagent:authlib-injector.jar=…</c>).</summary>
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];

    /// <summary>Legacy <c>${user_properties}</c> JSON (Twitch etc.).</summary>
    public string UserProperties { get; init; } = "{}";

    public bool IsDemo { get; init; }
}

public enum QuickPlayKind
{
    Singleplayer,
    Multiplayer,
    Realms,
}

/// <param name="Target">World folder name, <c>host[:port]</c>, or realm id.</param>
public sealed record QuickPlay(QuickPlayKind Kind, string Target);

public enum GameProcessPriority
{
    Normal,
    AboveNormal,
    High,
    BelowNormal,
    Idle,
}

public sealed record LaunchOptions
{
    public required string GameDirectory { get; init; }
    public required GameSession Session { get; init; }

    /// <summary>Per-instance natives folder; defaults to <c>&lt;gameDir&gt;/../natives</c>.</summary>
    public string? NativesDirectory { get; init; }

    public int? MaxMemoryMb { get; init; }
    public int? MinMemoryMb { get; init; }
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];
    public IReadOnlyList<string> ExtraGameArguments { get; init; } = [];
    public (int Width, int Height)? Resolution { get; init; }
    public bool Fullscreen { get; init; }
    public QuickPlay? QuickPlay { get; init; }

    /// <summary>Explicit Java home or executable; null for automatic selection.</summary>
    public string? JavaPath { get; init; }

    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Wrapper command line (e.g. a profiler); the java command is appended to it.</summary>
    public string? WrapperCommand { get; init; }

    /// <summary>Replaces <c>${version_type}</c> (shown on the title screen).</summary>
    public string? VersionTypeOverride { get; init; }

    public GameProcessPriority Priority { get; init; } = GameProcessPriority.Normal;

    public JvmPreset JvmPreset { get; init; } = JvmPreset.None;

    public GpuPreference GpuPreference { get; init; } = GpuPreference.Default;

    /// <summary>On an ARM64 PC: run natively where possible, or always x64 Java under emulation.</summary>
    public Libraries.Arm64Mode Arm64Mode { get; init; } = Libraries.Arm64Mode.Automatic;

    /// <summary>Use a Java @argfile when the command line would exceed this many characters.</summary>
    public int ArgFileThreshold { get; init; } = 30_000;
}
