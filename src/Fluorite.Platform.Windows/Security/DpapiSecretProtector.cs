using System.ComponentModel;
using System.Runtime.InteropServices;
using Fluorite.Core.Security;

namespace Fluorite.Platform.Windows.Security;

/// <summary>DPAPI (CurrentUser scope) encryption for refresh tokens stored on disk.</summary>
public sealed partial class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] s_entropy = "FluoriteLauncher.Accounts.v1"u8.ToArray();

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    private static unsafe byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        fixed (byte* pIn = input)
        fixed (byte* pEntropy = s_entropy)
        {
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = (IntPtr)pIn };
            var entropy = new DATA_BLOB { cbData = s_entropy.Length, pbData = (IntPtr)pEntropy };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok)
                throw new Win32Exception();
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
