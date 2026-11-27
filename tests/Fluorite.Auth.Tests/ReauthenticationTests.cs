using System.Net;
using System.Text;
using Fluorite.Auth.Accounts;
using Fluorite.Auth.Microsoft;
using Fluorite.Auth.Yggdrasil;
using Fluorite.Core.Net;
using Fluorite.Core.Security;

namespace Fluorite.Auth.Tests;

/// <summary>Signing an account in again: when it's needed, that it stays the same entry, and that it stays the same player.</summary>
public sealed class ReauthenticationTests : IDisposable
{
    private const string SteveId = "0123456789abcdef0123456789abcdef";
    private const string AlexId = "fedcba9876543210fedcba9876543210";
    private const string SkinSite = "https://skins.example/api/yggdrasil";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fluorite-reauth-{Guid.NewGuid():N}");
    private readonly FakeServices _services = new();
    private readonly HttpClient _http;
    private readonly AccountStore _store;
    private readonly AuthlibInjector _authlib;
    private readonly AccountService _accounts;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReauthenticationTests()
    {
        _http = new HttpClient(_services);
        _store = new AccountStore(StorePath, new PlainProtector());
        _authlib = new AuthlibInjector(_http, new MirrorTable(DownloadSourceKind.Official), Path.Combine(_dir, "authlib"));
        var options = new MicrosoftAuthOptions();
        _accounts = new AccountService(_store, new MicrosoftOAuthClient(_http, options), new MinecraftAuthChain(_http), options,
            new YggdrasilClient(_http), _authlib);
    }

    private string StorePath => Path.Combine(_dir, "accounts.dat");

    public void Dispose()
    {
        _authlib.Dispose();
        _store.Dispose();
        _http.Dispose();
        _services.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(SteveId, true)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData(AlexId, false)]
    [InlineData("", false)]
    public void SamePlayer_IgnoresDashesAndCase(string profileId, bool same) =>
        Assert.Equal(same, AccountService.IsSamePlayer(new Account { Uuid = SteveId }, profileId));

    [Fact]
    public void SamePlayer_NeverForAnAccountWithoutAnId() => Assert.False(AccountService.IsSamePlayer(new Account(), ""));

    [Theory]
    [InlineData("invalid_grant", AuthFailure.SignInAgain)]
    [InlineData("interaction_required", AuthFailure.SignInAgain)]
    [InlineData("temporarily_unavailable", AuthFailure.MicrosoftTokenRejected)]
    public async Task Refresh_OnlyARevokedGrantNeedsSigningInAgain(string error, AuthFailure expected)
    {
        _services.RefreshError = error;
        var oauth = new MicrosoftOAuthClient(_http, new MicrosoftAuthOptions());
        var ex = await Assert.ThrowsAsync<AuthException>(() => oauth.RefreshAsync("rt", null, Ct));
        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task FailedRefresh_MarksTheAccount_AndThatsSaved()
    {
        _services.RefreshError = "invalid_grant";
        var account = await AddAsync(Steve(refreshToken: "rt"));

        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.GetSessionAsync(account, Ct));

        Assert.Equal(AuthFailure.SignInAgain, ex.Failure);
        Assert.True(account.NeedsSignIn);
        using var reopened = new AccountStore(StorePath, new PlainProtector()); // The Accounts page says so after a restart too.
        Assert.True(Assert.Single((await reopened.LoadAsync(Ct)).Accounts).NeedsSignIn);
    }

    [Fact]
    public async Task AccountWindowsForgot_NeedsSigningInAgain()
    {
        _accounts.Broker = new ForgetfulBroker();
        var account = await AddAsync(Steve(brokerAccountId: "home-1"));

        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.GetMinecraftTokenAsync(account, Ct));

        Assert.Equal(AuthFailure.SignInAgain, ex.Failure);
        Assert.True(account.NeedsSignIn);
    }

    [Fact]
    public async Task OtherFailures_DontMarkTheAccount()
    {
        _services.RefreshError = "temporarily_unavailable";
        var account = await AddAsync(Steve(refreshToken: "rt"));

        await Assert.ThrowsAsync<AuthException>(() => _accounts.GetSessionAsync(account, Ct));

        Assert.False(account.NeedsSignIn);
    }

    [Fact]
    public async Task SigningInAgain_KeepsTheSameEntry()
    {
        _accounts.Broker = new ForgetfulBroker();
        var account = await AddAsync(Steve(brokerAccountId: "home-1"));
        account.NeedsSignIn = true;
        var id = account.Id;

        var signIn = await _accounts.SignInMicrosoftAsync(new MsaTokenResponse { AccessToken = "msa" }, "home-2", Ct);
        await _accounts.ReauthenticateMicrosoftAsync(account, signIn, Ct);

        var stored = Assert.Single(await _accounts.GetAccountsAsync(Ct));
        Assert.Same(account, stored);
        Assert.Equal(id, stored.Id);
        Assert.Equal(id, (await _accounts.GetSelectedAsync(Ct))?.Id);
        Assert.False(stored.NeedsSignIn);
        Assert.Equal("home-2", stored.BrokerAccountId);
        Assert.Null(stored.RefreshToken); // Windows keeps it.
        // And it launches without asking again.
        Assert.Equal("mc-token", (await _accounts.GetSessionAsync(stored, Ct)).AccessToken);
    }

    [Fact]
    public async Task SigningInAgainInTheBrowser_MovesTheAccountOffWindows()
    {
        var account = await AddAsync(Steve(brokerAccountId: "home-1"));

        var signIn = await _accounts.SignInMicrosoftAsync(new MsaTokenResponse { AccessToken = "msa", RefreshToken = "rt-new" }, null, Ct);
        await _accounts.ReauthenticateMicrosoftAsync(account, signIn, Ct);

        Assert.Null(account.BrokerAccountId);
        Assert.Equal("rt-new", account.RefreshToken);
        Assert.Equal(MicrosoftAuthOptions.FluoriteClientId, account.ClientId);
    }

    [Fact]
    public async Task SigningInAgainAnotherWay_ForgetsTheOldWindowsSignIn()
    {
        var broker = new ForgetfulBroker();
        _accounts.Broker = broker;
        var account = await AddAsync(Steve(brokerAccountId: "home-1"));

        var browser = await _accounts.SignInMicrosoftAsync(new MsaTokenResponse { AccessToken = "msa", RefreshToken = "rt-new" }, null, Ct);
        await _accounts.ReauthenticateMicrosoftAsync(account, browser, Ct);

        Assert.Equal(["home-1"], broker.Forgotten); // As removing the account would; it stays signed in to Windows.
    }

    [Fact]
    public async Task SigningInAgainAsTheSameWindowsAccount_KeepsIt()
    {
        var broker = new ForgetfulBroker();
        _accounts.Broker = broker;
        var account = await AddAsync(Steve(brokerAccountId: "home-2"));

        var windows = await _accounts.SignInMicrosoftAsync(new MsaTokenResponse { AccessToken = "msa" }, "home-2", Ct);
        await _accounts.ReauthenticateMicrosoftAsync(account, windows, Ct);

        Assert.Empty(broker.Forgotten);
        Assert.Equal("home-2", account.BrokerAccountId);
    }

    [Fact]
    public async Task NeedingASignIn_AndNoLonger_IsAnnounced()
    {
        _accounts.Broker = new ForgetfulBroker();
        var account = await AddAsync(Steve(brokerAccountId: "home-1"));
        var seen = new List<bool>();
        _accounts.SignInStateChanged += (_, a) => seen.Add(a.NeedsSignIn);

        await Assert.ThrowsAsync<AuthException>(() => _accounts.GetMinecraftTokenAsync(account, Ct));
        await Assert.ThrowsAsync<AuthException>(() => _accounts.GetMinecraftTokenAsync(account, Ct)); // Still: no news.
        var signIn = await _accounts.SignInMicrosoftAsync(new MsaTokenResponse { AccessToken = "msa" }, "home-2", Ct);
        await _accounts.ReauthenticateMicrosoftAsync(account, signIn, Ct);

        Assert.Equal([true, false], seen);
    }

    [Fact]
    public async Task SigningInAsAnotherPlayer_ChangesNothing()
    {
        var account = await AddAsync(Steve(refreshToken: "rt-steve"));
        account.NeedsSignIn = true;
        _services.ProfileId = AlexId;
        _services.ProfileName = "Alex";

        var signIn = await _accounts.SignInMicrosoftAsync(new MsaTokenResponse { AccessToken = "msa", RefreshToken = "rt-alex" }, null, Ct);
        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.ReauthenticateMicrosoftAsync(account, signIn, Ct));

        Assert.Equal(AuthFailure.DifferentPlayer, ex.Failure);
        Assert.Equal("Alex", ex.Player);
        Assert.Equal(("Steve", SteveId, "rt-steve", "old-token"), (account.PlayerName, account.Uuid, account.RefreshToken, account.AccessToken));
        Assert.True(account.NeedsSignIn);

        // Kept as an account of its own instead, when asked to.
        var alex = await _accounts.AddMicrosoftAsync(signIn, Ct);
        Assert.NotEqual(account.Id, alex.Id);
        Assert.Equal(2, (await _accounts.GetAccountsAsync(Ct)).Count);
    }

    [Fact]
    public async Task SkinSite_ExpiredToken_MarksTheAccount_AndThePasswordFixesIt()
    {
        var account = await AddAsync(SkinSiteSteve());

        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.GetSessionAsync(account, Ct));
        Assert.Equal(AuthFailure.SignInAgain, ex.Failure);
        Assert.True(account.NeedsSignIn);

        _services.YggdrasilProfile = (SteveId, "Steve");
        await _accounts.ReauthenticateYggdrasilAsync(account, "password", Ct);

        Assert.False(account.NeedsSignIn);
        Assert.Equal("ygg-new", account.AccessToken);
        Assert.Single(await _accounts.GetAccountsAsync(Ct));
    }

    [Fact]
    public async Task SkinSite_AnotherCharacter_ChangesNothing()
    {
        var account = await AddAsync(SkinSiteSteve());
        _services.YggdrasilProfile = (AlexId, "Alex");

        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.ReauthenticateYggdrasilAsync(account, "password", Ct));

        Assert.Equal(AuthFailure.DifferentPlayer, ex.Failure);
        Assert.Equal("Alex", ex.Player);
        Assert.Equal(("Steve", "ygg-old"), (account.PlayerName, account.AccessToken));
    }

    private static Account Steve(string? refreshToken = null, string? brokerAccountId = null) => new()
    {
        Kind = AccountKind.Microsoft,
        PlayerName = "Steve",
        Uuid = SteveId,
        RefreshToken = refreshToken,
        BrokerAccountId = brokerAccountId,
        AccessToken = "old-token",
        AccessTokenExpires = DateTimeOffset.UtcNow.AddHours(-1), // Expired: the next use refreshes it.
        OwnsGame = true,
    };

    private static Account SkinSiteSteve() => new()
    {
        Kind = AccountKind.Yggdrasil,
        PlayerName = "Steve",
        Uuid = SteveId,
        YggdrasilServer = SkinSite,
        LoginName = "steve@example.com",
        AccessToken = "ygg-old",
        RefreshToken = "client",
    };

    private async Task<Account> AddAsync(Account account)
    {
        var data = await _store.LoadAsync(Ct);
        data.Accounts.Add(account);
        data.SelectedAccountId = account.Id;
        await _store.SaveAsync(Ct);
        return account;
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plain) => plain.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> cipher) => cipher.ToArray();
    }

    /// <summary>What WAM does once the account was removed from Windows: refreshing fails, picking it again works.</summary>
    private sealed class ForgetfulBroker : IMicrosoftAccountBroker
    {
        public Task<BrokerToken> SignInAsync(CancellationToken ct = default) => Task.FromResult(new BrokerToken("msa", "home-2"));

        public Task<BrokerToken> RefreshAsync(string accountId, CancellationToken ct = default) =>
            Task.FromException<BrokerToken>(new AuthException(AuthFailure.SignInAgain, "Windows no longer has this account. Please sign in again."));

        /// <summary>The accounts Fluorite asked it to forget.</summary>
        public List<string> Forgotten { get; } = [];

        public Task ForgetAsync(string accountId, CancellationToken ct = default)
        {
            Forgotten.Add(accountId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Microsoft's token endpoint, Xbox Live, Minecraft services and a skin site, all answering as told.</summary>
    private sealed class FakeServices : HttpMessageHandler
    {
        public string? RefreshError { get; set; }
        public string ProfileId { get; set; } = SteveId;
        public string ProfileName { get; set; } = "Steve";
        public (string Id, string Name) YggdrasilProfile { get; set; } = (SteveId, "Steve");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(url switch
            {
                _ when url.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal) => RefreshError is null
                    ? Json(HttpStatusCode.OK, """{"access_token":"msa","refresh_token":"rt-new","expires_in":3600}""")
                    : Json(HttpStatusCode.BadRequest, $$"""{"error":"{{RefreshError}}","error_description":"AADSTS70000: nope"}"""),
                _ when url.StartsWith("https://user.auth.xboxlive.com/", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, """{"Token":"xbl","DisplayClaims":{"xui":[{"uhs":"uhs"}]}}"""),
                _ when url.StartsWith("https://xsts.auth.xboxlive.com/", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, """{"Token":"xsts","DisplayClaims":{"xui":[{"uhs":"uhs"}]}}"""),
                _ when url.EndsWith("/authentication/login_with_xbox", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, """{"access_token":"mc-token","expires_in":86400}"""),
                _ when url.EndsWith("/minecraft/profile", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, $$"""{"id":"{{ProfileId}}","name":"{{ProfileName}}"}"""),
                // The stored skin-site token is no good any more; the password gets a new one.
                _ when url == SkinSite + "/authserver/validate" => Json(HttpStatusCode.Forbidden, "{}"),
                _ when url == SkinSite + "/authserver/refresh" =>
                    Json(HttpStatusCode.Forbidden, """{"error":"ForbiddenOperationException","errorMessage":"Invalid token."}"""),
                _ when url == SkinSite + "/authserver/authenticate" => Json(HttpStatusCode.OK,
                    $$$"""{"accessToken":"ygg-new","clientToken":"client","selectedProfile":{"id":"{{{YggdrasilProfile.Id}}}","name":"{{{YggdrasilProfile.Name}}}"}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
