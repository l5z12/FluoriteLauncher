using Fluorite.Core.Servers;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.Views;

/// <summary>
/// <c>local:MotdText.Spans="{x:Bind Motd}"</c> fills a TextBlock with Minecraft-styled runs (colours, bold, italic…).
/// Minecraft's palette is made for a dark background, so the TextBlock should sit on one.
/// </summary>
public static class MotdText
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(object), typeof(MotdText), new PropertyMetadata(null, OnSpansChanged));

    private static readonly Dictionary<string, SolidColorBrush> s_brushes = new(StringComparer.OrdinalIgnoreCase);

    public static object? GetSpans(DependencyObject element) => element.GetValue(SpansProperty);

    public static void SetSpans(DependencyObject element, object? value) => element.SetValue(SpansProperty, value);

    private static void OnSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<ChatSpan> spans)
            return;
        foreach (var span in spans)
        {
            var lines = span.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    block.Inlines.Add(new LineBreak());
                if (lines[i].Length == 0)
                    continue;
                var run = new Run { Text = lines[i] };
                if (span.Color is { } color && Brush(color) is { } brush)
                    run.Foreground = brush;
                if (span.Bold)
                    run.FontWeight = FontWeights.Bold;
                if (span.Italic)
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (span.Underlined || span.Strikethrough)
                {
                    run.TextDecorations = (span.Underlined ? Windows.UI.Text.TextDecorations.Underline : Windows.UI.Text.TextDecorations.None)
                        | (span.Strikethrough ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None);
                }
                block.Inlines.Add(run);
            }
        }
    }

    private static SolidColorBrush? Brush(string hex)
    {
        if (s_brushes.TryGetValue(hex, out var cached))
            return cached;
        if (hex.Length != 7 || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return null;
        var brush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        s_brushes[hex] = brush;
        return brush;
    }
}
