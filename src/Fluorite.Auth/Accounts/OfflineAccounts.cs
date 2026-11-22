using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fluorite.Auth.Accounts;

public static partial class OfflineAccounts
{
    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex ValidNameRegex();

    /// <summary>Vanilla-compatible username rule (servers in offline mode accept others, but many plugins don't).</summary>
    public static bool IsValidName(string name) => ValidNameRegex().IsMatch(name);

    /// <summary>
    /// The UUID vanilla servers assign in offline mode: Java's <c>UUID.nameUUIDFromBytes("OfflinePlayer:" + name)</c>
    /// (MD5, version 3). Returned without dashes.
    /// </summary>
    public static string OfflineUuid(string name)
    {
#pragma warning disable CA5351 // MD5 is what Minecraft uses for offline UUIDs; not a security use.
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
#pragma warning restore CA5351
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // IETF variant
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Offline accounts are allowed only after a Microsoft account that owns Java Edition has been added (Prism model).
    /// </summary>
    public static bool AreUnlocked(IEnumerable<Account> accounts) =>
        accounts.Any(a => a.Kind == AccountKind.Microsoft && a.OwnsGame);

    public static Account Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 16)
            throw new ArgumentException("Offline player names must be 1–16 characters.", nameof(name));
        return new Account
        {
            Kind = AccountKind.Offline,
            PlayerName = name,
            Uuid = OfflineUuid(name),
            AccessToken = "0",
        };
    }
}
