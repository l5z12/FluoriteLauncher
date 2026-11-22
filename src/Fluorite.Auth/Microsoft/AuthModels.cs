using System.Text.Json.Serialization;

namespace Fluorite.Auth.Microsoft;

public sealed class MicrosoftAuthOptions
{
    /// <summary>
    /// Fluorite Launcher's Azure app (a public client: no secret). DO NOT use this ID in your own launcher, fork or
    /// tool: register your own Azure app and get it approved for the Minecraft API at https://aka.ms/mce-reviewappid.
    /// </summary>
    public const string FluoriteClientId = "4fe34dcd-b6fe-4757-ad7c-76593988eda7";

    /// <summary>
    /// Azure app (client) ID. Must be approved for the Minecraft API (https://aka.ms/mce-reviewappid);
    /// unapproved IDs get HTTP 403 from <c>login_with_xbox</c>.
    /// </summary>
    public string ClientId { get; set; } = FluoriteClientId;

    public string Scope { get; set; } = "XboxLive.signin offline_access";

    public string Authority { get; set; } = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
}

public sealed class MsaTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

public sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class XboxAuthRequest
{
    public required XboxAuthProperties Properties { get; init; }
    public required string RelyingParty { get; init; }
    public string TokenType { get; init; } = "JWT";
}

public sealed class XboxAuthProperties
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AuthMethod { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SiteName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RpsTicket { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SandboxId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? UserTokens { get; init; }
}

public sealed class XboxAuthResponse
{
    public string? Token { get; set; }
    public XboxDisplayClaims? DisplayClaims { get; set; }
    public DateTimeOffset? NotAfter { get; set; }
}

public sealed class XboxDisplayClaims
{
    public List<XboxUserInfo>? Xui { get; set; }
}

public sealed class XboxUserInfo
{
    public string? Uhs { get; set; }
    public string? Xid { get; set; }
    public string? Gtg { get; set; }
}

public sealed class XboxErrorResponse
{
    public long XErr { get; set; }
    public string? Message { get; set; }
    public string? Redirect { get; set; }
}

public sealed class MinecraftLoginRequest
{
    [JsonPropertyName("identityToken")] public required string IdentityToken { get; init; }
}

public sealed class MinecraftLoginResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
}

public sealed class MinecraftEntitlements
{
    public List<MinecraftEntitlement>? Items { get; set; }
}

public sealed class MinecraftEntitlement
{
    public string? Name { get; set; }
}

public sealed class MinecraftProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<MinecraftSkin>? Skins { get; set; }
    public List<MinecraftCape>? Capes { get; set; }
}

public sealed class MinecraftSkin
{
    public string? Id { get; set; }
    public string? State { get; set; }
    public string? Url { get; set; }
    public string? Variant { get; set; }
}

public sealed class MinecraftCape
{
    public string? Id { get; set; }
    public string? State { get; set; }
    public string? Url { get; set; }
    public string? Alias { get; set; }
}

public sealed class CapeRequest
{
    [JsonPropertyName("capeId")] public required string CapeId { get; init; }
}

public sealed class NameAvailabilityResponse
{
    public string? Status { get; set; }
}

public sealed class NameChangeInfo
{
    public DateTimeOffset? ChangedAt { get; set; }
    public bool NameChangeAllowed { get; set; }
}

public sealed class MinecraftServicesError
{
    public string? Error { get; set; }
    public string? ErrorMessage { get; set; }
    public MinecraftServicesErrorDetails? Details { get; set; }
}

public sealed class MinecraftServicesErrorDetails
{
    public string? Status { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CapeRequest))]
[JsonSerializable(typeof(NameAvailabilityResponse))]
[JsonSerializable(typeof(NameChangeInfo))]
[JsonSerializable(typeof(MinecraftServicesError))]
[JsonSerializable(typeof(MsaTokenResponse))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(XboxAuthRequest))]
[JsonSerializable(typeof(XboxAuthResponse))]
[JsonSerializable(typeof(XboxErrorResponse))]
[JsonSerializable(typeof(MinecraftLoginRequest))]
[JsonSerializable(typeof(MinecraftLoginResponse))]
[JsonSerializable(typeof(MinecraftEntitlements))]
[JsonSerializable(typeof(MinecraftProfile))]
[JsonSerializable(typeof(Accounts.AccountStoreData))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
