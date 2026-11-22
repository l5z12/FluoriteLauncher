using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fluorite.Auth.Microsoft;
using Fluorite.Auth.Yggdrasil;

namespace Fluorite.Auth.Tests;

public class YggdrasilTests
{
    [Theory]
    [InlineData("littleskin.cn", "https://littleskin.cn/")]
    [InlineData("https://ely.by/", "https://ely.by/")]
    [InlineData("http://127.0.0.1:25585/api", "http://127.0.0.1:25585/api")]
    public void Normalize_AssumesHttps(string input, string expected) => Assert.Equal(expected, YggdrasilClient.Normalize(input));

    [Fact]
    public void Normalize_RejectsRemotePlainHttp()
    {
        var ex = Assert.Throws<AuthException>(() => YggdrasilClient.Normalize("http://example.com/api"));
        Assert.Equal(AuthFailure.InsecureServer, ex.Failure);
    }

    [Fact]
    public void JvmArguments_AgentAndPrefetch()
    {
        var args = AuthlibInjector.JvmArguments(@"C:\ali\authlib-injector-1.2.8.jar", "https://littleskin.cn/api/yggdrasil", "e30=");
        Assert.Equal(@"-javaagent:C:\ali\authlib-injector-1.2.8.jar=https://littleskin.cn/api/yggdrasil", args[0]);
        Assert.Equal("-Dauthlibinjector.yggdrasil.prefetched=e30=", args[1]);
    }

    [Fact]
    public async Task LocalSkinServer_ServesSignedSlimSkin()
    {
        using var server = new LocalSkinServer();
        server.Start();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        server.Register("b50ad385829d3141a2167e7d7539ba7f", "Notch", png, slim: true);
        using var http = new HttpClient();
        var root = $"http://127.0.0.1:{server.Port}";

        // Discovery reads the metadata the game gets prefetched.
        var discovered = await new YggdrasilClient(http).DiscoverAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal("Fluorite", discovered.Name);
        using var meta = JsonDocument.Parse(Convert.FromBase64String(discovered.MetadataBase64));
        var pem = meta.RootElement.GetProperty("signaturePublickey").GetString()!;

        var json = await http.GetStringAsync($"{root}/sessionserver/session/minecraft/profile/b50ad385829d3141a2167e7d7539ba7f?unsigned=false", TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var prop = doc.RootElement.GetProperty("properties")[0];
        var value = prop.GetProperty("value").GetString()!;
        var signature = Convert.FromBase64String(prop.GetProperty("signature").GetString()!);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
#pragma warning disable CA5350 // authlib's algorithm.
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(value), signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
#pragma warning restore CA5350

        var textures = YggdrasilClient.ReadTextures(new YggdrasilProfile { Properties = [new YggdrasilProperty { Name = "textures", Value = value }] });
        Assert.True(textures.Slim);
        Assert.NotNull(textures.SkinUrl);
        Assert.Equal(png, await http.GetByteArrayAsync(textures.SkinUrl, TestContext.Current.CancellationToken));

        // Unknown profiles: 204, as the spec says.
        using var missing = await http.GetAsync($"{root}/sessionserver/session/minecraft/profile/00000000000000000000000000000000", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, missing.StatusCode);
    }
}
