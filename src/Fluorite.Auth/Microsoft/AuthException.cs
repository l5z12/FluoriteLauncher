namespace Fluorite.Auth.Microsoft;

public enum AuthFailure
{
    Unknown,
    Cancelled,
    Network,
    MicrosoftTokenRejected,
    NoXboxAccount,
    XboxUnavailableInRegion,
    AdultVerificationRequired,
    ChildAccountNeedsFamily,
    XboxBanned,
    AppNotApprovedForMinecraft,
    DoesNotOwnGame,
    NoMinecraftProfile,
    RateLimited,

    /// <summary>Yggdrasil: wrong email/name or password.</summary>
    InvalidCredentials,

    /// <summary>
    /// The stored sign-in can no longer be renewed: only signing in again fixes it (a skin site's password, or a new
    /// Microsoft sign-in).
    /// </summary>
    SignInAgain,

    /// <summary>The address doesn't serve authlib-injector API metadata.</summary>
    NotYggdrasilServer,

    /// <summary>A plain-HTTP authentication server that isn't on this machine.</summary>
    InsecureServer,

    /// <summary>Yggdrasil: the account has no game profile (character) yet.</summary>
    NoYggdrasilProfiles,

    /// <summary>The system account broker (Windows: WAM) couldn't sign in for this app.</summary>
    BrokerUnavailable,

    /// <summary>Signing an account in again reached another player than the account's (<see cref="AuthException.Player"/>).</summary>
    DifferentPlayer,
}

/// <summary>An authentication error with a machine-readable reason the UI maps to localized guidance.</summary>
public sealed class AuthException(AuthFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public AuthFailure Failure { get; } = failure;

    /// <summary>For <see cref="AuthFailure.DifferentPlayer"/>: who the sign-in was for; null when the account's player is just gone.</summary>
    public string? Player { get; init; }

    /// <summary>Maps XSTS <c>XErr</c> codes to failures.</summary>
    public static AuthFailure FromXErr(long xerr) => xerr switch
    {
        2148916227 => AuthFailure.XboxBanned,
        2148916233 => AuthFailure.NoXboxAccount,
        2148916235 => AuthFailure.XboxUnavailableInRegion,
        2148916236 or 2148916237 => AuthFailure.AdultVerificationRequired,
        2148916238 => AuthFailure.ChildAccountNeedsFamily,
        _ => AuthFailure.Unknown,
    };
}
