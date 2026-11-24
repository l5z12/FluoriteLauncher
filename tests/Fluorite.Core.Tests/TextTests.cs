using System.IO.Compression;
using System.Text;
using Fluorite.Core.Text;

namespace Fluorite.Core.Tests;

public class MojibakeTests
{
    [Theory]
    // UTF-8 read as Windows-1252, with its unassigned bytes (0x8D, 0x9D) as C1 controls, as Windows decodes them.
    [InlineData("!!Â§rÂ§fÂ§l ã€‡äº”çº¢æ˜¾ 05RD Â§7- 2025-10-02 Â§0.zip", "!!§r§f§l 〇五红显 05RD §7- 2025-10-02 §0.zip")]
    [InlineData("è\u008D‰æ–¹å\u009D—ä¾§é\u009D¢", "草方块侧面")]
    [InlineData("[å¿«æ\u008D·å®‰è£…]ã€Šé›¶é›¾æž„æƒ³-v5003ã€‹.zip", "[快捷安装]《零雾构想-v5003》.zip")]
    public void GarbledText_IsRepaired(string garbled, string expected) => Assert.Equal(expected, Mojibake.Repair(garbled));

    [Theory]
    [InlineData("Faithful 32x.zip")]
    [InlineData("零雾构想 v5003.zip")] // Already right.
    [InlineData("Pokémon Café.zip")] // Accents that aren't UTF-8 bytes.
    [InlineData("§6Gold.zip")]
    public void OtherText_IsLeftAlone(string text) => Assert.Same(text, Mojibake.Repair(text));
}

public class ZipNamesTests
{
    private static readonly Encoding s_gbk = CodePagesEncodingProvider.Instance.GetEncoding(936)!;

    [Theory]
    [InlineData(null)]
    [InlineData(936)]
    public void UnflaggedGbkNames_ReadAsChinese(int? systemPage)
    {
        string[] names = ["overrides/resourcepacks/零雾构想.zip", "overrides/resourcepacks/完美玻璃.zip"];
        using var temp = new TempZip(s_gbk, names);
        using var read = ZipNames.OpenRead(temp.Path, systemPage);
        Assert.Equal(names, read.Entries.Select(e => e.FullName));
        Assert.NotNull(read.GetEntry(names[0]));
    }

    [Fact]
    public void UnflaggedNames_ReadInTheSystemsOwnCodePageFirst()
    {
        using var temp = new TempZip(CodePagesEncodingProvider.Instance.GetEncoding(932)!, "ワールド/level.dat");
        using var read = ZipNames.OpenRead(temp.Path, 932);
        Assert.Equal("ワールド/level.dat", Assert.Single(read.Entries).FullName);
    }

    [Fact]
    public void OneArchive_ReadsInOneEncoding()
    {
        // "英" alone is valid UTF-8 (D3 A2, "Ӣ"): it must still read as GBK, like the folder under it.
        string[] names = ["英/level.dat", "英/datapacks/中文包/pack.mcmeta"];
        using var temp = new TempZip(s_gbk, names);
        using var read = ZipNames.OpenRead(temp.Path, null);
        Assert.Equal(names, read.Entries.Select(e => e.FullName));
    }

    [Theory]
    [InlineData(null)] // The system has no CJK code page (Western Windows).
    [InlineData(936)] // Chinese.
    [InlineData(932)] // Japanese.
    public void NamesNoCodePageReads_KeepTheirSeparators(int? systemPage)
    {
        // A French Explorer zip (CP850): not UTF-8, and "é/" isn't GBK, GB18030 or Shift-JIS either. No "?", no lost "/".
        using var temp = new TempZip(CodePagesEncodingProvider.Instance.GetEncoding(850)!, "Café/level.dat");
        using var read = ZipNames.OpenRead(temp.Path, systemPage);
        Assert.Equal("Caf�/level.dat", Assert.Single(read.Entries).FullName);
    }

    [Theory]
    [InlineData(null)] // The system has no CJK code page (Western Windows).
    [InlineData(936)] // Chinese.
    [InlineData(932)] // Japanese.
    public void UnflaggedUtf8Names_StayUtf8(int? systemPage)
    {
        using var temp = new TempZip(new UnflaggedUtf8(), "零雾构想/pack.mcmeta");
        using var read = ZipNames.OpenRead(temp.Path, systemPage);
        Assert.Equal("零雾构想/pack.mcmeta", Assert.Single(read.Entries).FullName);
    }

    /// <summary>A zip whose names are written in <paramref name="names"/>: without the UTF-8 flag unless that's UTF-8.</summary>
    private sealed class TempZip : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fluorite-test-{Guid.NewGuid():N}.zip");

        public TempZip(Encoding names, params string[] entries)
        {
            using var zip = new ZipArchive(File.Create(Path), ZipArchiveMode.Create, leaveOpen: false, names);
            foreach (var entry in entries)
                zip.CreateEntry(entry);
        }

        public void Dispose() => File.Delete(Path);
    }

    /// <summary>UTF-8 that ZipArchive doesn't know as UTF-8, so it leaves the flag off, as some tools do.</summary>
    private sealed class UnflaggedUtf8 : Encoding
    {
        private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);
        public override int GetByteCount(char[] chars, int index, int count) => s_utf8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => s_utf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => s_utf8.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => s_utf8.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override int GetMaxByteCount(int charCount) => s_utf8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => s_utf8.GetMaxCharCount(byteCount);
    }
}
