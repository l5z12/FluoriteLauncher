using Fluorite.Auth.Microsoft;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Fluorite.Platform.Windows.Security;

/// <summary>
/// Microsoft sign-in through WAM, the Windows account broker (via MSAL). Its picker lists the Microsoft accounts
/// signed in to Windows, plus "Use another account"; WAM keeps the refresh tokens.
/// The Azure app needs the redirect URI <c>ms-appx-web://microsoft.aad.brokerplugin/&lt;client ID&gt;</c>
/// (Mobile and desktop applications).
/// </summary>
public sealed class WamAccountBroker : IMicrosoftAccountBroker
{
    // MSAL adds offline_access, openid and profile itself.
    private static readonly string[] Scopes = ["XboxLive.signin"];

    private readonly Lazy<IPublicClientApplication> app;

    /// <param name="parentWindow">The window WAM's dialogs belong to (read when a dialog opens).</param>
    public WamAccountBroker(string clientId, Func<nint> parentWindow)
    {
        app = new(() => PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.PersonalMicrosoftAccount)
            .WithParentActivityOrWindow(() => parentWindow())
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Fluorite Launcher" })
            .Build());
    }

    public async Task<BrokerToken> SignInAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await app.Value.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(ct).ConfigureAwait(false);
            return ToToken(result);
        }
        catch (Exception ex) when (ex is MsalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            throw Map(ex);
        }
    }

    public async Task<BrokerToken> RefreshAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            var account = await app.Value.GetAccountAsync(accountId).ConfigureAwait(false)
                ?? throw new AuthException(AuthFailure.MicrosoftTokenRejected, "Windows no longer has this account. Please sign in again.");
            try
            {
                return ToToken(await app.Value.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct).ConfigureAwait(false));
            }
            catch (MsalUiRequiredException)
            {
                // Windows asks only for what it needs (a password, consent), for this same account.
                return ToToken(await app.Value.AcquireTokenInteractive(Scopes).WithAccount(account).ExecuteAsync(ct).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is MsalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            throw Map(ex);
        }
    }

    public async Task ForgetAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            if (await app.Value.GetAccountAsync(accountId).ConfigureAwait(false) is { } account)
                await app.Value.RemoveAsync(account).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MsalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            // Best effort: the account is gone from Fluorite either way, and stays signed in to Windows.
        }
    }

    private static BrokerToken ToToken(AuthenticationResult result) => new(result.AccessToken, result.Account.HomeAccountId.Identifier);

    private static AuthException Map(Exception ex) => ex is MsalClientException { ErrorCode: MsalError.AuthenticationCanceledError }
        ? new AuthException(AuthFailure.Cancelled, "Sign-in was cancelled.", ex)
        : new AuthException(AuthFailure.BrokerUnavailable, ex.Message, ex);
}
