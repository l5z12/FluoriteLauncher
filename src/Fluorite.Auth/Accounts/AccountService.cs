using Fluorite.Auth.Microsoft;
using Fluorite.Auth.Yggdrasil;
using Fluorite.Core.Launch;

namespace Fluorite.Auth.Accounts;

/// <summary>A Yggdrasil sign-in that succeeded; <see cref="Profiles"/> lists the characters to choose from when there are several.</summary>
public sealed record YggdrasilSignIn(YggdrasilServer Server, string LoginName, YggdrasilAuthResponse Response)
{
    public IReadOnlyList<YggdrasilProfile> Profiles =>
        Response.SelectedProfile is { } p ? [p] : Response.AvailableProfiles ?? [];
}

/// <summary>
/// A Microsoft sign-in that reached Minecraft: the player it's for, and the tokens to keep. Nothing is stored until
/// it's added (<see cref="AccountService.AddMicrosoftAsync(MicrosoftSignIn, CancellationToken)"/>) or used to sign an
/// account in again (<see cref="AccountService.ReauthenticateMicrosoftAsync"/>).
/// </summary>
/// <param name="BrokerAccountId">The broker's account ID when the token came from <see cref="AccountService.Broker"/>.</param>
public sealed record MicrosoftSignIn(MsaTokenResponse Token, string? BrokerAccountId, MinecraftLogin Login, MinecraftProfile Profile);

/// <summary>
/// Account lifecycle: add (MSA/Yggdrasil/offline), refresh, sign in again, remove, select, and produce a
/// <see cref="GameSession"/>.
/// </summary>
/// <param name="developerOfflineOverride">
/// Debug builds only: lets developers add offline accounts before an approved Azure client ID exists.
/// Release builds must always pass <c>false</c> (offline accounts stay gated behind game ownership).
/// </param>
public sealed class AccountService(
    AccountStore store, MicrosoftOAuthClient oauth, MinecraftAuthChain chain, MicrosoftAuthOptions options,
    YggdrasilClient yggdrasil, AuthlibInjector authlib, bool developerOfflineOverride = false)
{
    /// <summary>
    /// Serves offline accounts' skins to the game (a local Yggdrasil server). Set by the app; without it offline
    /// accounts launch with the default skins.
    /// </summary>
    public ILocalSkinHost? LocalSkins { get; set; }

    /// <summary>
    /// The system account broker (Windows: WAM). Set by the app; accounts signed in through it can't be refreshed
    /// without it.
    /// </summary>
    public IMicrosoftAccountBroker? Broker { get; set; }

    /// <summary>
    /// An account started or stopped needing a sign-in (<see cref="Account.NeedsSignIn"/>): a refresh failed for good,
    /// or a sign-in or refresh worked. Raised on whichever thread did it; pages showing accounts follow it.
    /// </summary>
    public event EventHandler<Account>? SignInStateChanged;

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default) =>
        (await store.LoadAsync(ct).ConfigureAwait(false)).Accounts;

    public async Task<Account?> GetSelectedAsync(CancellationToken ct = default)
    {
        var data = await store.LoadAsync(ct).ConfigureAwait(false);
        return data.Accounts.FirstOrDefault(a => a.Id == data.SelectedAccountId) ?? data.Accounts.FirstOrDefault();
    }

    public async Task SelectAsync(string accountId, CancellationToken ct = default)
    {
        var data = await store.LoadAsync(ct).ConfigureAwait(false);
        data.SelectedAccountId = accountId;
        await store.SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Puts the accounts in the given order (ids not listed keep their place after the listed ones).</summary>
    public async Task ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        var data = await store.LoadAsync(ct).ConfigureAwait(false);
        var position = new Dictionary<string, int>();
        for (var i = 0; i < orderedIds.Count; i++)
            position.TryAdd(orderedIds[i], i);
        var sorted = data.Accounts.OrderBy(a => position.GetValueOrDefault(a.Id, int.MaxValue)).ToList(); // Stable.
        data.Accounts.Clear();
        data.Accounts.AddRange(sorted);
        await store.SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> AreOfflineAccountsUnlockedAsync(CancellationToken ct = default) =>
        developerOfflineOverride || OfflineAccounts.AreUnlocked(await GetAccountsAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Takes an MSA token from any provider (device code, browser, WAM) through to Minecraft, and checks the account
    /// can play: it owns the game and has a profile. Stores nothing.
    /// </summary>
    /// <param name="brokerAccountId">The broker's account ID when the token came from <see cref="Broker"/>.</param>
    public async Task<MicrosoftSignIn> SignInMicrosoftAsync(MsaTokenResponse msa, string? brokerAccountId = null, CancellationToken ct = default)
    {
        var login = await chain.LoginAsync(msa.AccessToken!, ct).ConfigureAwait(false);
        if (!login.OwnsGame)
            throw new AuthException(AuthFailure.DoesNotOwnGame, "This Microsoft account doesn't own Minecraft: Java Edition.");
        var profile = login.Profile
            ?? throw new AuthException(AuthFailure.NoMinecraftProfile, "This account owns the game but hasn't created a Minecraft profile yet.");
        return new MicrosoftSignIn(msa, brokerAccountId, login, profile);
    }

    /// <summary>Completes a Microsoft sign-in given an MSA token from any provider (device code, browser, WAM).</summary>
    /// <param name="brokerAccountId">The broker's account ID when the token came from <see cref="Broker"/>.</param>
    public async Task<Account> AddMicrosoftAsync(MsaTokenResponse msa, string? brokerAccountId = null, CancellationToken ct = default) =>
        await AddMicrosoftAsync(await SignInMicrosoftAsync(msa, brokerAccountId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>Adds the account a sign-in is for; one already listed for that player is updated instead.</summary>
    public async Task<Account> AddMicrosoftAsync(MicrosoftSignIn signIn, CancellationToken ct = default)
    {
        var data = await store.LoadAsync(ct).ConfigureAwait(false);
        var account = data.Accounts.FirstOrDefault(a => a.Kind == AccountKind.Microsoft && IsSamePlayer(a, signIn.Profile.Id))
            ?? new Account { Kind = AccountKind.Microsoft };
        var replaced = account.BrokerAccountId;
        Apply(account, signIn);
        if (!data.Accounts.Contains(account))
            data.Accounts.Add(account);
        data.SelectedAccountId ??= account.Id;
        await store.SaveAsync(ct).ConfigureAwait(false);
        await ForgetReplacedBrokerAccountAsync(replaced).ConfigureAwait(false);
        return account;
    }

    /// <summary>
    /// Signs a Microsoft account in again, in place: the same entry and ID, so what points at it (an instance's
    /// default account, the selection) keeps working. A sign-in for another player changes nothing: it throws
    /// <see cref="AuthFailure.DifferentPlayer"/>, naming that player.
    /// </summary>
    public async Task ReauthenticateMicrosoftAsync(Account account, MicrosoftSignIn signIn, CancellationToken ct = default)
    {
        if (account.Kind != AccountKind.Microsoft)
            throw new InvalidOperationException("Not a Microsoft account.");
        if (!IsSamePlayer(account, signIn.Profile.Id))
        {
            throw new AuthException(AuthFailure.DifferentPlayer, $"That sign-in is for {signIn.Profile.Name}, not {account.PlayerName}.")
            {
                Player = signIn.Profile.Name,
            };
        }
        var replaced = account.BrokerAccountId;
        Apply(account, signIn);
        await store.SaveAsync(ct).ConfigureAwait(false);
        await ForgetReplacedBrokerAccountAsync(replaced).ConfigureAwait(false);
    }

    /// <summary>
    /// The Windows sign-in an account had before a sign-in replaced it (one in the browser, with a code, or as another
    /// Windows account) is forgotten for Fluorite, as removing the account would; it stays signed in to Windows. Not
    /// while another account still uses it. Best effort, and not cancellable: the sign-in has already been saved.
    /// </summary>
    private async Task ForgetReplacedBrokerAccountAsync(string? replaced)
    {
        if (replaced is null || Broker is not { } broker)
            return;
        var data = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        if (data.Accounts.Any(a => a.BrokerAccountId == replaced))
            return;
        await broker.ForgetAsync(replaced, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Whether a profile ID is this account's player (with or without dashes, in any case).</summary>
    public static bool IsSamePlayer(Account account, string profileId) =>
        account.Uuid.Length > 0
        && string.Equals(account.Uuid.Replace("-", "", StringComparison.Ordinal), profileId.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    private void Apply(Account account, MicrosoftSignIn signIn)
    {
        Apply(account, signIn.Token, signIn.Login);
        account.ClientId = options.ClientId; // The app that issued this refresh token.
        account.BrokerAccountId = signIn.BrokerAccountId;
        if (signIn.BrokerAccountId is not null)
            account.RefreshToken = null; // The broker keeps it.
    }

    public async Task<Account> AddOfflineAsync(string playerName, CancellationToken ct = default)
    {
        var data = await store.LoadAsync(ct).ConfigureAwait(false);
        if (!developerOfflineOverride && !OfflineAccounts.AreUnlocked(data.Accounts))
            throw new AuthException(AuthFailure.DoesNotOwnGame, "Add a Microsoft account that owns Minecraft: Java Edition before using offline accounts.");
        var account = OfflineAccounts.Create(playerName);
        data.Accounts.Add(account);
        data.SelectedAccountId ??= account.Id;
        await store.SaveAsync(ct).ConfigureAwait(false);
        return account;
    }

    /// <summary>Signs in to a Yggdrasil server. The password is used once and never stored.</summary>
    public async Task<YggdrasilSignIn> SignInYggdrasilAsync(YggdrasilServer server, string loginName, string password, CancellationToken ct = default)
    {
        var response = await yggdrasil.AuthenticateAsync(server.ApiRoot, loginName, password, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        var signIn = new YggdrasilSignIn(server, loginName, response);
        if (response.AccessToken is null)
            throw new AuthException(AuthFailure.Unknown, "The server returned no access token.");
        if (signIn.Profiles.Count == 0)
            throw new AuthException(AuthFailure.NoYggdrasilProfiles, $"This {server.Name} account has no character yet.");
        return signIn;
    }

    /// <summary>Adds (or updates) the account for one profile of a sign-in, binding the token to that profile.</summary>
    public async Task<Account> AddYggdrasilAsync(YggdrasilSignIn signIn, YggdrasilProfile profile, CancellationToken ct = default)
    {
        var response = signIn.Response;
        if (response.SelectedProfile?.Id != profile.Id)
            response = await yggdrasil.RefreshAsync(signIn.Server.ApiRoot, response.AccessToken!, response.ClientToken, profile, ct).ConfigureAwait(false);

        var data = await store.LoadAsync(ct).ConfigureAwait(false);
        var selected = response.SelectedProfile ?? profile;
        var account = data.Accounts.FirstOrDefault(a => a.Kind == AccountKind.Yggdrasil && a.Uuid == selected.Id
                && string.Equals(a.YggdrasilServer, signIn.Server.ApiRoot, StringComparison.OrdinalIgnoreCase))
            ?? new Account { Kind = AccountKind.Yggdrasil };
        account.YggdrasilServer = signIn.Server.ApiRoot;
        account.YggdrasilServerName = signIn.Server.Name;
        account.YggdrasilMetadata = signIn.Server.MetadataBase64;
        account.LoginName = signIn.LoginName;
        Apply(account, response, selected);
        if (!data.Accounts.Contains(account))
            data.Accounts.Add(account);
        data.SelectedAccountId ??= account.Id;
        await store.SaveAsync(ct).ConfigureAwait(false);
        return account;
    }

    /// <summary>
    /// Signs a Yggdrasil account in again after its token expired (the password is asked for again), in place. A
    /// sign-in that lands on another character changes nothing: it throws <see cref="AuthFailure.DifferentPlayer"/>.
    /// </summary>
    public async Task ReauthenticateYggdrasilAsync(Account account, string password, CancellationToken ct = default)
    {
        var api = account.YggdrasilServer ?? throw new InvalidOperationException("Not a Yggdrasil account.");
        var response = await yggdrasil.AuthenticateAsync(api, account.LoginName ?? account.PlayerName, password, account.RefreshToken, ct).ConfigureAwait(false);
        if (response.SelectedProfile is { } p && !IsSamePlayer(account, p.Id))
            throw new AuthException(AuthFailure.DifferentPlayer, $"That sign-in belongs to {p.Name}, not {account.PlayerName}.") { Player = p.Name };
        if (response.SelectedProfile is null)
        {
            var mine = response.AvailableProfiles?.FirstOrDefault(x => IsSamePlayer(account, x.Id))
                ?? throw new AuthException(AuthFailure.DifferentPlayer, $"{account.PlayerName} is no longer on this account.");
            response = await yggdrasil.RefreshAsync(api, response.AccessToken!, response.ClientToken, mine, ct).ConfigureAwait(false);
        }
        Apply(account, response, response.SelectedProfile);
        await store.SaveAsync(ct).ConfigureAwait(false);
    }

    private void Apply(Account account, YggdrasilAuthResponse response, YggdrasilProfile? profile)
    {
        account.AccessToken = response.AccessToken ?? account.AccessToken;
        account.RefreshToken = response.ClientToken ?? account.RefreshToken; // Yggdrasil: the client token.
        account.AccessTokenExpires = null; // Unknown; checked with /validate before each launch.
        SetNeedsSignIn(account, false);
        if (profile is not null)
        {
            account.Uuid = profile.Id;
            account.PlayerName = profile.Name;
        }
    }

    public async Task RemoveAsync(string accountId, CancellationToken ct = default)
    {
        var data = await store.LoadAsync(ct).ConfigureAwait(false);
        var account = data.Accounts.FirstOrDefault(a => a.Id == accountId);
        data.Accounts.RemoveAll(a => a.Id == accountId);
        if (data.SelectedAccountId == accountId)
            data.SelectedAccountId = data.Accounts.FirstOrDefault()?.Id;
        // Remove it from disk first: if Fluorite closes before the sign-outs below finish, the account is gone rather
        // than left listed with tokens that no longer work. Both sign-outs are best effort.
        await store.SaveAsync(ct).ConfigureAwait(false);
        if (account is { Kind: AccountKind.Yggdrasil, YggdrasilServer: { } api, AccessToken: { } token })
            await yggdrasil.InvalidateAsync(api, token, account.RefreshToken, ct).ConfigureAwait(false);
        if (account is { BrokerAccountId: { } brokerId } && Broker is { } broker)
            await broker.ForgetAsync(brokerId, ct).ConfigureAwait(false);
    }

    /// <summary>Ensures a fresh token (refreshing silently when needed) and builds the launch session.</summary>
    public async Task<GameSession> GetSessionAsync(Account account, CancellationToken ct = default)
    {
        switch (account.Kind)
        {
            case AccountKind.Offline:
                if (LocalSkins is not null && account.SkinFile is not null && File.Exists(account.SkinFile))
                {
                    // A custom skin: authlib-injector points the game at the local skin server.
                    var (root, metadata) = await LocalSkins.ServeAsync(account, ct).ConfigureAwait(false);
                    var agent = await authlib.EnsureAsync(ct).ConfigureAwait(false);
                    return new GameSession(account.PlayerName, account.Uuid, Guid.NewGuid().ToString("N"), "mojang")
                    {
                        ExtraJvmArguments = AuthlibInjector.JvmArguments(agent, root, metadata),
                    };
                }
                return new GameSession(account.PlayerName, account.Uuid, "0", "legacy");

            case AccountKind.Yggdrasil:
                try
                {
                    return await GetYggdrasilSessionAsync(account, ct).ConfigureAwait(false);
                }
                catch (AuthException ex) when (ex.Failure == AuthFailure.SignInAgain)
                {
                    await MarkNeedsSignInAsync(account).ConfigureAwait(false);
                    throw;
                }

            case AccountKind.Microsoft:
                var token = await GetMinecraftTokenAsync(account, ct).ConfigureAwait(false);
                account.LastUsed = DateTimeOffset.Now;
                await store.SaveAsync(ct).ConfigureAwait(false);
                return new GameSession(account.PlayerName, account.Uuid, token, "msa", account.Xuid, account.ClientId ?? options.ClientId);

            default:
                throw new NotSupportedException($"{account.Kind} accounts are not supported.");
        }
    }

    /// <summary>A Minecraft services access token for a Microsoft account, refreshed silently when it's about to expire.</summary>
    public async Task<string> GetMinecraftTokenAsync(Account account, CancellationToken ct = default)
    {
        if (account.Kind != AccountKind.Microsoft)
            throw new InvalidOperationException("Only Microsoft accounts have Minecraft services tokens.");
        if (!account.HasValidAccessToken)
        {
            MsaTokenResponse msa;
            try
            {
                msa = await RefreshMicrosoftAsync(account, ct).ConfigureAwait(false);
            }
            catch (AuthException ex) when (ex.Failure == AuthFailure.SignInAgain)
            {
                await MarkNeedsSignInAsync(account).ConfigureAwait(false);
                throw;
            }
            var login = await chain.LoginAsync(msa.AccessToken!, ct).ConfigureAwait(false);
            Apply(account, msa, login);
            account.ClientId ??= options.ClientId;
            await store.SaveAsync(ct).ConfigureAwait(false);
        }
        return account.AccessToken!;
    }

    /// <summary>A new MSA token: through the broker for accounts signed in with it, otherwise with the refresh token.</summary>
    private async Task<MsaTokenResponse> RefreshMicrosoftAsync(Account account, CancellationToken ct)
    {
        if (account.BrokerAccountId is { } brokerId)
        {
            if (Broker is null)
                throw new AuthException(AuthFailure.SignInAgain, "Windows sign-in isn't available here. Please sign in again.");
            var token = await Broker.RefreshAsync(brokerId, ct).ConfigureAwait(false);
            return new MsaTokenResponse { AccessToken = token.AccessToken };
        }
        if (account.RefreshToken is null)
            throw new AuthException(AuthFailure.SignInAgain, "Please sign in again.");
        return await oauth.RefreshAsync(account.RefreshToken, account.ClientId, ct).ConfigureAwait(false);
    }

    /// <summary>Remembers that only signing in again fixes the account, for the Accounts page to say so.</summary>
    private async Task MarkNeedsSignInAsync(Account account)
    {
        if (account.NeedsSignIn)
            return;
        SetNeedsSignIn(account, true);
        try
        {
            // Not cancellable, and best effort: the failure is being reported either way.
            await store.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still shown until Fluorite closes.
        }
    }

    private void SetNeedsSignIn(Account account, bool value)
    {
        if (account.NeedsSignIn == value)
            return;
        account.NeedsSignIn = value;
        SignInStateChanged?.Invoke(this, account);
    }

    /// <summary>Persists changes made to an account object (skin, name…).</summary>
    public Task SaveAsync(CancellationToken ct = default) => store.SaveAsync(ct);

    /// <summary>Takes a fresh Microsoft profile (after a skin, cape or name change) into the stored account.</summary>
    public Task UpdateProfileAsync(Account account, MinecraftProfile profile, CancellationToken ct = default)
    {
        account.PlayerName = profile.Name;
        account.SkinUrl = profile.Skins?.FirstOrDefault(s => s.State == "ACTIVE")?.Url;
        return store.SaveAsync(ct);
    }

    /// <summary>
    /// Validates the token, refreshing it when needed (a refresh that fails asks for the password again,
    /// <see cref="AuthFailure.SignInAgain"/>). When the server can't be reached the stored token is used as is:
    /// single player still works and the game reports the rest.
    /// </summary>
    private async Task<GameSession> GetYggdrasilSessionAsync(Account account, CancellationToken ct)
    {
        // No server: a password can't fix that, so not SignInAgain.
        var api = account.YggdrasilServer ?? throw new AuthException(AuthFailure.Unknown, "This account has no server; add it again.");
        if (account.AccessToken is null)
            throw new AuthException(AuthFailure.SignInAgain, "Please sign in again.");

        var reachable = true;
        try
        {
            if (!await yggdrasil.ValidateAsync(api, account.AccessToken, account.RefreshToken, ct).ConfigureAwait(false))
            {
                try
                {
                    var refreshed = await yggdrasil.RefreshAsync(api, account.AccessToken, account.RefreshToken, null, ct).ConfigureAwait(false);
                    Apply(account, refreshed, refreshed.SelectedProfile);
                }
                catch (AuthException ex) when (ex.Failure == AuthFailure.InvalidCredentials)
                {
                    throw new AuthException(AuthFailure.SignInAgain, ex.Message, ex);
                }
            }
            SetNeedsSignIn(account, false); // The token works (again).
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            reachable = false;
        }

        if (reachable)
        {
            try
            {
                account.YggdrasilMetadata = (await yggdrasil.DiscoverAsync(api, ct).ConfigureAwait(false)).MetadataBase64;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or AuthException)
            {
                // The metadata from sign-in still works.
            }
        }

        var agent = await authlib.EnsureAsync(ct).ConfigureAwait(false);
        account.LastUsed = DateTimeOffset.Now;
        await store.SaveAsync(ct).ConfigureAwait(false);
        return new GameSession(account.PlayerName, account.Uuid, account.AccessToken!, "mojang")
        {
            ExtraJvmArguments = AuthlibInjector.JvmArguments(agent, api, account.YggdrasilMetadata),
        };
    }

    private void Apply(Account account, MsaTokenResponse msa, MinecraftLogin login)
    {
        account.RefreshToken = msa.RefreshToken ?? account.RefreshToken;
        account.AccessToken = login.AccessToken;
        account.AccessTokenExpires = login.Expires;
        account.Xuid = login.Xuid;
        account.OwnsGame = login.OwnsGame;
        SetNeedsSignIn(account, false);
        if (login.Profile is { } p)
        {
            account.Uuid = p.Id;
            account.PlayerName = p.Name;
            account.SkinUrl = p.Skins?.FirstOrDefault(s => s.State == "ACTIVE")?.Url;
        }
    }
}
