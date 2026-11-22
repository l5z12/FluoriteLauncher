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

    /// <summary>The stored token can no longer be refreshed; the password is needed again.</summary>
    SignInAgain,

    /// <summary>The address doesn't serve authlib-injector API metadata.</summary>
    NotYggdrasilServer,

    /// <summary>A plain-HTTP authentication server that isn't on this machine.</summary>
    InsecureServer,

    /// <summary>Yggdrasil: the account has no game profile (character) yet.</summary>
    NoYggdrasilProfiles,

    /// <summary>The system account broker (Windows: WAM) couldn't sign in for this app.</summary>
    BrokerUnavailable,
}

/// <summary>An authentication error with a machine-readable reason the UI maps to localized guidance.</summary>
public sealed class AuthException(AuthFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public AuthFailure Failure { get; } = failure;

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
