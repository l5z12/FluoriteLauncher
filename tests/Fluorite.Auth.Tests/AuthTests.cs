using System.Text;
using Fluorite.Auth.Accounts;
using Fluorite.Auth.Microsoft;
using Fluorite.Core.Security;

namespace Fluorite.Auth.Tests;

public class OfflineAccountTests
{
    [Theory]
    // Values verified against Java's UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(UTF_8)).
    [InlineData("Notch", "b50ad385829d3141a2167e7d7539ba7f")]
    [InlineData("jeb_", "a762f5604fce3236812ab80efff0b62b")]
    [InlineData("FluoriteDev", "92acdd37175a3a63ba8fac7c1ebbb8fb")]
    public void OfflineUuid_MatchesVanilla(string name, string expected) =>
        Assert.Equal(expected, OfflineAccounts.OfflineUuid(name));

    [Theory]
    [InlineData("Steve", true)]
    [InlineData("ab", false)]
    [InlineData("has space", false)]
    [InlineData("SeventeenCharName", false)]
    [InlineData("名字", false)]
    public void NameValidation(string name, bool valid) => Assert.Equal(valid, OfflineAccounts.IsValidName(name));

    [Fact]
    public void Unlock_RequiresOwnedMicrosoftAccount()
    {
        Assert.False(OfflineAccounts.AreUnlocked([]));
        Assert.False(OfflineAccounts.AreUnlocked([new Account { Kind = AccountKind.Microsoft, OwnsGame = false }]));
        Assert.True(OfflineAccounts.AreUnlocked([new Account { Kind = AccountKind.Microsoft, OwnsGame = true }]));
    }
}

public class XboxErrorTests
{
    [Theory]
    [InlineData(2148916233, AuthFailure.NoXboxAccount)]
    [InlineData(2148916235, AuthFailure.XboxUnavailableInRegion)]
    [InlineData(2148916236, AuthFailure.AdultVerificationRequired)]
    [InlineData(2148916238, AuthFailure.ChildAccountNeedsFamily)]
    [InlineData(1, AuthFailure.Unknown)]
    public void MapsXErr(long xerr, AuthFailure expected) => Assert.Equal(expected, AuthException.FromXErr(xerr));

    [Fact]
    public void ReadsXuidFromJwt()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"{B64("{\"alg\":\"none\"}")}.{B64("{\"xuid\":\"2535400000000000\",\"sub\":\"x\"}")}.sig";
        Assert.Equal("2535400000000000", MinecraftAuthChain.ReadXuidFromJwt(jwt));
        Assert.Null(MinecraftAuthChain.ReadXuidFromJwt("not-a-jwt"));
    }
}

public class AccountStoreTests
{
    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> p) => p.ToArray().Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> c) => Protect(c);
    }

    [Fact]
    public async Task RoundTrips_AndNeverWritesPlaintextTokens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fluorite-accounts-{Guid.NewGuid():N}.dat");
        try
        {
            using (var store = new AccountStore(path, new XorProtector()))
            {
                var data = await store.LoadAsync(TestContext.Current.CancellationToken);
                data.Accounts.Add(new Account { Kind = AccountKind.Microsoft, PlayerName = "Steve", RefreshToken = "SUPER-SECRET-REFRESH" });
                await store.SaveAsync(TestContext.Current.CancellationToken);
            }

            var raw = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("SUPER-SECRET-REFRESH", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

            using var reopened = new AccountStore(path, new XorProtector());
            var loaded = await reopened.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("SUPER-SECRET-REFRESH", Assert.Single(loaded.Accounts).RefreshToken);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class OAuthRefreshTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"a","refresh_token":"r","expires_in":3600}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task Refresh_UsesTheAppThatIssuedTheToken()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var oauth = new MicrosoftOAuthClient(http, new MicrosoftAuthOptions());
        var ct = TestContext.Current.CancellationToken;

        await oauth.RefreshAsync("rt", null, ct); // Signed in through Fluorite's app.
        await oauth.RefreshAsync("rt", "22222222-2222-2222-2222-222222222222", ct); // An account from another app.

        Assert.Contains($"client_id={MicrosoftAuthOptions.FluoriteClientId}", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("client_id=22222222-2222-2222-2222-222222222222", handler.Bodies[1], StringComparison.Ordinal);
        Assert.All(handler.Bodies, b => Assert.DoesNotContain("client_secret", b, StringComparison.Ordinal));
    }
}