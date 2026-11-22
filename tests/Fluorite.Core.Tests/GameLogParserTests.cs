using Fluorite.Core.Logging;

namespace Fluorite.Core.Tests;

public class GameLogParserTests
{
    [Fact]
    public void ParsesMultiLineLog4jEvent()
    {
        var p = new GameLogParser();
        var now = DateTimeOffset.Now;
        Assert.Empty(p.Feed("<log4j:Event logger=\"net.minecraft.client.Minecraft\" timestamp=\"1731100000000\" level=\"WARN\" thread=\"Render thread\">", false, now));
        Assert.Empty(p.Feed("  <log4j:Message><![CDATA[Something <odd> & happened]]></log4j:Message>", false, now));
        Assert.Empty(p.Feed("  <log4j:Throwable><![CDATA[java.lang.RuntimeException: boom", false, now));
        Assert.Empty(p.Feed("\tat Foo.bar(Foo.java:1)]]></log4j:Throwable>", false, now));
        var e = Assert.Single(p.Feed("</log4j:Event>", false, now));

        Assert.Equal(GameLogLevel.Warn, e.Level);
        Assert.Equal("Render thread", e.Thread);
        Assert.Equal("net.minecraft.client.Minecraft", e.Logger);
        Assert.Equal("Something <odd> & happened", e.Message);
        Assert.Contains("boom", e.Throwable, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1731100000000), e.Timestamp);
    }

    [Theory]
    [InlineData("[12:00:00] [main/INFO]: Loading Minecraft", GameLogLevel.Info)]
    [InlineData("[12:00:00] [Render thread/ERROR]: oops", GameLogLevel.Error)]
    [InlineData("Exception in thread \"main\" java.lang.Error", GameLogLevel.Error)]
    [InlineData("plain println output", GameLogLevel.Unknown)]
    public void ClassifiesPlainLines(string line, GameLogLevel expected) =>
        Assert.Equal(expected, Assert.Single(new GameLogParser().Feed(line, false, DateTimeOffset.Now)).Level);

    [Fact]
    public void MalformedXml_FallsBackToPlain()
    {
        var p = new GameLogParser();
        var e = Assert.Single(p.Feed("<log4j:Event level=\"INFO\"><broken</log4j:Event>", false, DateTimeOffset.Now));
        Assert.Contains("broken", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MergesJavaUtilLoggingRecords()
    {
        var p = new GameLogParser();
        var now = DateTimeOffset.Now;
        Assert.Empty(p.Feed("Nov 09, 2026 12:31:03 AM net.minecraft.launchwrapper.LogWrapper log", true, now));
        var info = Assert.Single(p.Feed("INFO: Using tweak class name net.minecraft.launchwrapper.VanillaTweaker", true, now));
        Assert.Equal(GameLogLevel.Info, info.Level);
        Assert.Equal("net.minecraft.launchwrapper.LogWrapper", info.Logger);
        Assert.Equal("Using tweak class name net.minecraft.launchwrapper.VanillaTweaker", info.Message);

        Assert.Empty(p.Feed("Nov 09, 2026 12:31:04 AM net.java.games.input.DefaultControllerEnvironment getControllers", true, now));
        Assert.Equal(GameLogLevel.Warn, Assert.Single(p.Feed("WARNING: Found unknown Windows version: Windows 8.1", true, now)).Level);

        // A header not followed by a JUL message falls back to normal handling of the next line.
        Assert.Empty(p.Feed("Nov 09, 2026 12:31:05 AM some.Logger method", false, now));
        Assert.Equal("plain line", Assert.Single(p.Feed("plain line", false, now)).Message);
    }

    [Fact]
    public void Flush_EmitsUnterminatedEvent()
    {
        var p = new GameLogParser();
        Assert.Empty(p.Feed("<log4j:Event level=\"INFO\">", false, DateTimeOffset.Now));
        Assert.Single(p.Flush(DateTimeOffset.Now));
    }
}
