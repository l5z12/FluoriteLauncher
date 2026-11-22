namespace Fluorite.Auth.Microsoft;

/// <summary>
/// Microsoft sign-in through the operating system's account broker. On Windows that's WAM, whose picker offers the
/// Microsoft accounts already signed in to Windows (or another one). The broker keeps the refresh token, so accounts
/// signed in this way are refreshed through it (<see cref="Accounts.Account.BrokerAccountId"/>).
/// </summary>
public interface IMicrosoftAccountBroker
{
    /// <summary>Shows the system's account picker.</summary>
    /// <exception cref="AuthException">
    /// <see cref="AuthFailure.Cancelled"/>, or <see cref="AuthFailure.BrokerUnavailable"/> when the broker can't sign
    /// in for this app (the caller can fall back to the browser).
    /// </exception>
    Task<BrokerToken> SignInAsync(CancellationToken ct = default);

    /// <summary>A fresh token for an account signed in through the broker. It shows UI only if it must (a password, consent).</summary>
    Task<BrokerToken> RefreshAsync(string accountId, CancellationToken ct = default);

    /// <summary>Forgets the account for this app, best effort. It stays signed in to the operating system.</summary>
    Task ForgetAsync(string accountId, CancellationToken ct = default);
}

/// <summary>A Microsoft (MSA) access token from the broker, and the broker's ID for the account it belongs to.</summary>
public sealed record BrokerToken(string AccessToken, string AccountId);
