using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Views;

/// <summary>
/// <c>local:TrimToolTip.IsEnabled="True"</c> on a TextBlock with <c>TextTrimming</c>: while the text is cut off with
/// an ellipsis, hovering shows all of it. Text that fits gets no tooltip.
/// </summary>
public static class TrimToolTip
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TrimToolTip), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || e.NewValue is not true)
            return;
        text.IsTextTrimmedChanged += (sender, _) => Update(sender);
        // A recycled list item gets new text that may stay trimmed, so trimming alone doesn't say when to refresh.
        text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (sender, _) => Update((TextBlock)sender));
        Update(text);
    }

    private static void Update(TextBlock text) =>
        ToolTipService.SetToolTip(text, text.IsTextTrimmed && !string.IsNullOrEmpty(text.Text) ? text.Text : null);
}
