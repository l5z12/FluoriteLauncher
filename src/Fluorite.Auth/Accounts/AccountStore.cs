using System.Text.Json;
using Fluorite.Auth.Microsoft;
using Fluorite.Core.Security;

namespace Fluorite.Auth.Accounts;

public sealed class AccountStoreData
{
    public int FormatVersion { get; set; } = 1;
    public string? SelectedAccountId { get; set; }
    public List<Account> Accounts { get; set; } = [];
}

/// <summary>
/// Persists accounts to <c>accounts.dat</c>: the whole JSON document is encrypted with <see cref="ISecretProtector"/>
/// (DPAPI on Windows), so tokens never touch disk in plaintext.
/// </summary>
public sealed class AccountStore(string path, ISecretProtector protector) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccountStoreData? _data;

    public void Dispose() => _lock.Dispose();

    public async Task<AccountStoreData> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_data is not null)
                return _data;
            if (!File.Exists(path))
                return _data = new AccountStoreData();

            var cipher = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            try
            {
                var plain = protector.Unprotect(cipher);
                _data = JsonSerializer.Deserialize(plain, AuthJsonContext.Default.AccountStoreData) ?? new AccountStoreData();
            }
            catch (Exception ex) when (ex is JsonException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
            {
                // Different Windows user / corrupted file: keep a copy and start fresh rather than failing the launcher.
                File.Copy(path, path + ".unreadable", overwrite: true);
                _data = new AccountStoreData();
            }
            return _data;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_data is null)
                return;
            var plain = JsonSerializer.SerializeToUtf8Bytes(_data, AuthJsonContext.Default.AccountStoreData);
            var cipher = protector.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, cipher, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
