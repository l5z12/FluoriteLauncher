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
