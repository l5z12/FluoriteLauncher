namespace Fluorite.Core.Security;

/// <summary>Encrypts secrets (refresh tokens) at rest, bound to the current Windows user.</summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}
