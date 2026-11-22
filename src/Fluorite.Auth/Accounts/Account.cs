using System.Text.Json.Serialization;

namespace Fluorite.Auth.Accounts;

public enum AccountKind
{
    Microsoft,
    Yggdrasil,
    Offline,
}

/// <summary>
/// A stored account. Secrets (<see cref="RefreshToken"/>, <see cref="AccessToken"/>) are serialized only inside the
/// encrypted store payload, never in plain JSON on disk.
/// </summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountKind Kind { get; set; }
    public string PlayerName { get; set; } = "";

    /// <summary>Profile UUID without dashes.</summary>
    public string Uuid { get; set; } = "";

    public string? Xuid { get; set; }

    /// <summary>Microsoft refresh token or Yggdrasil client token.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Minecraft/Yggdrasil access token (cached).</summary>
    public string? AccessToken { get; set; }

    public DateTimeOffset? AccessTokenExpires { get; set; }

    /// <summary>
    /// For Microsoft accounts: the Azure app (client ID) that signed in. Its refresh token only works with that app,
    /// so accounts signed in through another app keep refreshing through it. Null for accounts from before this.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// For Microsoft accounts signed in through the Windows account broker: the broker's ID for the account. The
    /// broker keeps the refresh token, so <see cref="RefreshToken"/> is null and refreshes go through the broker.
    /// </summary>
    public string? BrokerAccountId { get; set; }

    /// <summary>For Yggdrasil accounts: the authlib-injector API root.</summary>
    public string? YggdrasilServer { get; set; }

    /// <summary>For Yggdrasil accounts: the server's display name (LittleSkin, Ely.by…).</summary>
    public string? YggdrasilServerName { get; set; }

    /// <summary>For Yggdrasil accounts: the last API metadata seen (Base64), prefetched to the game so it starts even if that request would fail.</summary>
    public string? YggdrasilMetadata { get; set; }

    /// <summary>For Yggdrasil accounts: the email or name used to sign in, offered again when the password is needed.</summary>
    public string? LoginName { get; set; }

    /// <summary>For offline accounts: a skin PNG served to the game by the launcher's local skin server.</summary>
    public string? SkinFile { get; set; }

    /// <summary>For offline accounts: whether <see cref="SkinFile"/> uses the slim (Alex) arm model.</summary>
    public bool SkinSlim { get; set; }

    /// <summary>Whether this Microsoft account was verified to own Java Edition (unlocks offline accounts).</summary>
    public bool OwnsGame { get; set; }

    public string? SkinUrl { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsed { get; set; }

    [JsonIgnore]
    public bool HasValidAccessToken => AccessToken is not null && AccessTokenExpires is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(5);
}
