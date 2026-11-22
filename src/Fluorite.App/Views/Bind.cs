using Fluorite.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.Views;

/// <summary>
/// Helper functions for x:Bind function bindings. x:Bind doesn't call a function when its argument is null; it uses
/// the binding's FallbackValue, or else leaves the old value. So every binding through a function that takes a
/// nullable argument needs one: <c>VisibleIfText</c>/<c>VisibleIfNotNull</c> → <c>FallbackValue=Collapsed</c>,
/// <c>IsNotNull</c> → <c>FallbackValue=False</c>.
/// </summary>
public static class Bind
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfPositive(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What Narrator adds after a picked card's name: "selected".</summary>
    public static string SelectedStatus(bool selected) => selected ? Services.Loc.Get("Library_ItemSelected") : "";
    /// <summary>What Narrator adds to an instance card or row: "Selected", "Hidden", "Selected, Hidden".</summary>
    public static string InstanceStatus(bool selected, bool hidden) => (selected, hidden) switch
    {
        (true, true) => Services.Loc.Get("Library_ItemSelected") + ", " + Services.Loc.Get("Library_ItemHidden"),
        (true, false) => Services.Loc.Get("Library_ItemSelected"),
        (false, true) => Services.Loc.Get("Library_ItemHidden"),
        _ => "",
    };

    /// <summary>A hidden instance, shown while "Show hidden" is on.</summary>
    public static double DimIf(bool dim) => dim ? 0.5 : 1.0;

    /// <summary>Hide (eye-slash) or show (eye).</summary>
    public static string HideGlyph(bool hidden) => hidden ? "" : "";

    public static Visibility VisibleIfZero(int value) => value == 0 ? Visibility.Visible : Visibility.Collapsed;
    public static bool Not(bool value) => !value;
    public static bool Any(bool a, bool b) => a || b;
    public static bool IsNotNull(object? value) => value is not null;
    public static double EnabledOpacity(bool enabled) => enabled ? 1.0 : 0.55;
    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity(bool error) =>
        error ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
    public static Brush JobBrush(bool failed) => Resource(failed ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
    public static Brush IssueBrush(bool error) => Resource(error ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");
    /// <summary>A crash finding's icon: a warning for a likely cause, a question mark for a possible one.</summary>
    public static string FindingGlyph(bool possible) => possible ? "" : "";
    public static Brush FindingBrush(bool possible) => Resource(possible ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
    public static string PinText(bool pinned) => Services.Loc.Get(pinned ? "Content_Unpin" : "Content_Pin");
    public static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    public static string Level(GameLogLevel l) => l == GameLogLevel.Unknown ? "" : l.ToString().ToUpperInvariant();

    public static Brush LevelBrush(GameLogLevel level) => level switch
    {
        GameLogLevel.Error or GameLogLevel.Fatal => Resource("SystemFillColorCriticalBrush"),
        GameLogLevel.Warn => Resource("SystemFillColorCautionBrush"),
        GameLogLevel.Debug or GameLogLevel.Trace => Resource("TextFillColorTertiaryBrush"),
        _ => Resource("TextFillColorPrimaryBrush"),
    };

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// The data item behind a templated control: <c>Tag="{x:Bind}"</c> first (ItemsRepeater and flyouts don't reliably
    /// propagate DataContext), then DataContext.
    /// </summary>
    public static T? ItemOf<T>(object sender) where T : class =>
        sender is FrameworkElement fe ? fe.Tag as T ?? fe.DataContext as T : null;
}
