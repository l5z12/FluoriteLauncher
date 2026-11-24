using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;

namespace Fluorite.Core.Text;

/// <summary>
/// Opens zips so that names stored without the UTF-8 flag read right. Zip tools on Chinese Windows (WinRAR, Bandizip,
/// Explorer…) write those in GBK, which .NET reads as UTF-8: every Chinese name becomes "���", and names of the same
/// length become the same name. Names that are valid UTF-8 still read as UTF-8.
/// </summary>
/// <remarks>Every reader of one archive must open it through here: entry names are looked up by what they decode to.</remarks>
public static class ZipNames
{
    public static Encoding Encoding { get; } = new UnflaggedNameEncoding();

    public static ZipArchive OpenRead(string path) => ZipFile.Open(path, ZipArchiveMode.Read, Encoding);

    /// <summary>UTF-8 when the bytes are valid UTF-8, else the legacy code page CJK zip tools write.</summary>
    private sealed class UnflaggedNameEncoding : Encoding
    {
        private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

        // A CJK culture's own OEM code page (what its zip tools write); anywhere else GBK, by far the likeliest in packs.
        private static readonly Encoding s_legacy = CodePagesEncodingProvider.Instance.GetEncoding(
            CultureInfo.CurrentCulture.TextInfo.OEMCodePage is var oem and (932 or 936 or 949 or 950) ? oem : 54936) ?? s_utf8;

        private static Encoding For(ReadOnlySpan<byte> bytes) => Utf8.IsValid(bytes) ? s_utf8 : s_legacy;

        public override int GetCharCount(byte[] bytes, int index, int count) => For(bytes.AsSpan(index, count)).GetCharCount(bytes, index, count);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            For(bytes.AsSpan(byteIndex, byteCount)).GetChars(bytes, byteIndex, byteCount, chars, charIndex);

        public override string GetString(byte[] bytes, int index, int count) => For(bytes.AsSpan(index, count)).GetString(bytes, index, count);

        public override int GetMaxCharCount(int byteCount) => Math.Max(s_utf8.GetMaxCharCount(byteCount), s_legacy.GetMaxCharCount(byteCount));

        // Writing (never done through this) is UTF-8.
        public override int GetByteCount(char[] chars, int index, int count) => s_utf8.GetByteCount(chars, index, count);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            s_utf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

        public override int GetMaxByteCount(int charCount) => s_utf8.GetMaxByteCount(charCount);
    }
}
