using System.Text;

namespace Fluorite.Core.Text;

/// <summary>
/// Undoes UTF-8 that was read as Windows-1252 (or Latin-1): "é›¶é›¾" → "零雾", "Â§" → "§". Some launchers and zip
/// tools name the files they extract like this, so a Chinese pack arrives as a folder of garbled names.
/// </summary>
public static class Mojibake
{
    /// <summary>What Windows-1252 decodes 0x80–0x9F to; its five unassigned bytes become the C1 control of the same value.</summary>
    private const string Cp1252High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The text before it was garbled, or <paramref name="text"/> itself when it isn't garbled. Only text that maps
    /// back to valid UTF-8 changes: real names with accents ("Pokémon") aren't, so they stay as they are.
    /// </summary>
    public static string Repair(string text)
    {
        if (Ascii.IsValid(text))
            return text;
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c <= 0xFF)
            {
                bytes[i] = (byte)c;
                continue;
            }
            var high = Cp1252High.IndexOf(c, StringComparison.Ordinal);
            if (high < 0)
                return text; // Not a Windows-1252 character: this wasn't decoded that way.
            bytes[i] = (byte)(0x80 + high);
        }
        try
        {
            return s_strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }
}
