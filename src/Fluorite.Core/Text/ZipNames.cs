using System.IO.Compression;
using System.Text;
using System.Text.Unicode;

namespace Fluorite.Core.Text;

/// <summary>
/// Opens zips so that names stored without the UTF-8 flag read right. Zip tools on Chinese Windows (WinRAR, Bandizip,
/// Explorer…) write those in GBK, which .NET reads as UTF-8: every Chinese name becomes "���", and names of the same
/// length become the same name.
/// </summary>
/// <remarks>
/// One encoding is chosen for the whole archive, so that a folder reads the same in every name under it: a short GBK
/// name can be valid UTF-8 on its own ("英" reads as "Ӣ"). It's UTF-8 when every unflagged name is valid UTF-8; else
/// the system's CJK code page, then GB18030 (by far the likeliest in packs), whichever reads every name; else UTF-8 as
/// .NET reads it, with "�" for the bytes it can't read (never "?", which Windows file names can't hold).
/// Every reader of one archive must open it through here: entry names are looked up by what they decode to.
/// </remarks>
public static class ZipNames
{
    /// <summary>The system locale's code page if it's a CJK one: what its zip tools write.</summary>
    private static readonly int? s_systemCjkPage =
        CodePagesEncodingProvider.Instance.GetEncoding(0)?.CodePage is var page and (932 or 936 or 949 or 950) ? page : null;

    public static ZipArchive OpenRead(string path) => OpenRead(path, s_systemCjkPage);

    internal static ZipArchive OpenRead(string path, int? systemCjkPage)
    {
        var probe = new NameProbe();
        var zip = ZipFile.Open(path, ZipArchiveMode.Read, probe);
        try
        {
            foreach (var entry in zip.Entries)
                _ = entry.FullName;
        }
        catch
        {
            zip.Dispose();
            throw;
        }
        if (probe.SawInvalidUtf8 && LegacyEncodingFor(probe.Names, systemCjkPage) is { } legacy)
        {
            zip.Dispose();
            return ZipFile.Open(path, ZipArchiveMode.Read, legacy);
        }
        // Every name is UTF-8, or no other code page reads them all either: as the probe read them.
        return zip;
    }

    private static Encoding? LegacyEncodingFor(List<byte[]> names, int? systemCjkPage)
    {
        int[] pages = systemCjkPage is { } own ? [own, 54936] : [54936];
        foreach (var page in pages)
        {
            var strict = CodePagesEncodingProvider.Instance.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (strict is not null && names.TrueForAll(name => Reads(strict, name)))
                return CodePagesEncodingProvider.Instance.GetEncoding(page); // Lenient for comments, read only on demand.
        }
        return null;
    }

    private static bool Reads(Encoding strict, byte[] name)
    {
        try
        {
            strict.GetCharCount(name);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Reads names as UTF-8 and keeps those that aren't ASCII, noting whether any wasn't valid UTF-8.</summary>
    private sealed class NameProbe : Encoding
    {
        private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

        public List<byte[]> Names { get; } = [];
        public bool SawInvalidUtf8 { get; private set; }

        private void Note(ReadOnlySpan<byte> name)
        {
            if (Ascii.IsValid(name))
                return;
            Names.Add(name.ToArray());
            SawInvalidUtf8 |= !Utf8.IsValid(name);
        }

        public override string GetString(byte[] bytes, int index, int count)
        {
            Note(bytes.AsSpan(index, count));
            return s_utf8.GetString(bytes, index, count);
        }

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            Note(bytes.AsSpan(byteIndex, byteCount));
            return s_utf8.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => s_utf8.GetCharCount(bytes, index, count);

        public override int GetMaxCharCount(int byteCount) => s_utf8.GetMaxCharCount(byteCount);

        public override int GetByteCount(char[] chars, int index, int count) => s_utf8.GetByteCount(chars, index, count);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            s_utf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

        public override int GetMaxByteCount(int charCount) => s_utf8.GetMaxByteCount(charCount);
    }
}
