using System.Text.Json.Serialization;

namespace Fluorite.Auth.Yggdrasil;

/// <summary>A resolved authlib-injector server: its API root and the metadata the game gets prefetched.</summary>
/// <param name="ApiRoot">API root without a trailing slash, e.g. <c>https://littleskin.cn/api/yggdrasil</c>.</param>
/// <param name="MetadataBase64">The raw <c>GET {ApiRoot}</c> response, Base64-encoded for <c>authlibinjector.yggdrasil.prefetched</c>.</param>
public sealed record YggdrasilServer(string ApiRoot, string Name, string? Homepage, string? Register, bool NonEmailLogin, string MetadataBase64);

/// <summary>Well-known servers offered in the sign-in form; anything else is typed in.</summary>
public static class YggdrasilPresets
{
    /// <summary>Addresses as users know them; ALI discovery resolves the real API root.</summary>
    public static IReadOnlyList<(string Name, string Address)> All { get; } =
    [
        ("LittleSkin", "littleskin.cn"),
        ("Ely.by", "ely.by"),
    ];
}

public sealed class YggdrasilAgent
{
    public string Name { get; init; } = "Minecraft";
    public int Version { get; init; } = 1;
}

public sealed class YggdrasilAuthenticateRequest
{
    public YggdrasilAgent Agent { get; init; } = new();
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? ClientToken { get; init; }
    public bool RequestUser { get; init; } = true;
}

public sealed class YggdrasilRefreshRequest
{
    public required string AccessToken { get; init; }
    public string? ClientToken { get; init; }
    public bool RequestUser { get; init; } = true;
    public YggdrasilProfile? SelectedProfile { get; init; }
}

public sealed class YggdrasilTokenRequest
{
    public required string AccessToken { get; init; }
    public string? ClientToken { get; init; }
}

public sealed class YggdrasilAuthResponse
{
    public string? AccessToken { get; set; }
    public string? ClientToken { get; set; }
    public List<YggdrasilProfile>? AvailableProfiles { get; set; }
    public YggdrasilProfile? SelectedProfile { get; set; }
}

public sealed class YggdrasilProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<YggdrasilProperty>? Properties { get; set; }
}

public sealed class YggdrasilProperty
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Signature { get; set; }
}

public sealed class YggdrasilError
{
    public string? Error { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Cause { get; set; }
}

public sealed class YggdrasilMetadata
{
    public YggdrasilMeta? Meta { get; set; }
    public List<string>? SkinDomains { get; set; }
    public string? SignaturePublickey { get; set; }
}

public sealed class YggdrasilMeta
{
    public string? ServerName { get; set; }
    public YggdrasilLinks? Links { get; set; }
    [JsonPropertyName("feature.non_email_login")] public bool NonEmailLogin { get; set; }
}

public sealed class YggdrasilLinks
{
    public string? Homepage { get; set; }
    public string? Register { get; set; }
}

/// <summary>The decoded value of a profile's <c>textures</c> property.</summary>
public sealed class TexturesPayload
{
    public long Timestamp { get; set; }
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public bool? SignatureRequired { get; set; }
    public Dictionary<string, TextureInfo> Textures { get; set; } = [];
}

public sealed class TextureInfo
{
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary><c>artifact/latest.json</c> of the authlib-injector download API.</summary>
public sealed class AuthlibInjectorArtifact
{
    [JsonPropertyName("build_number")] public int BuildNumber { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("checksums")] public AuthlibInjectorChecksums? Checksums { get; set; }
}

public sealed class AuthlibInjectorChecksums
{
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(YggdrasilAuthenticateRequest))]
[JsonSerializable(typeof(YggdrasilRefreshRequest))]
[JsonSerializable(typeof(YggdrasilTokenRequest))]
[JsonSerializable(typeof(YggdrasilAuthResponse))]
[JsonSerializable(typeof(YggdrasilProfile))]
[JsonSerializable(typeof(YggdrasilError))]
[JsonSerializable(typeof(YggdrasilMetadata))]
[JsonSerializable(typeof(TexturesPayload))]
[JsonSerializable(typeof(AuthlibInjectorArtifact))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<YggdrasilProfile>))]
internal sealed partial class YggdrasilJsonContext : JsonSerializerContext;
