using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Fluorite.App.Services;

/// <summary>
/// A brief "Copied" under the button that copied, so a click that changes nothing on screen still answers. Narrator
/// hears it too. Menu items get only the Narrator part: their menu has closed.
/// </summary>
public static class CopiedFeedback
{
    public static void Show(object? sender)
    {
        if (sender is not FrameworkElement { XamlRoot: not null } anchor)
            return;
        var text = Loc.Get("Common_Copied");
        (FrameworkElementAutomationPeer.FromElement(anchor) ?? FrameworkElementAutomationPeer.CreatePeerForElement(anchor))?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "Copied");
        if (anchor is MenuFlyoutItemBase)
            return;

        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = text } },
            },
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode = FlyoutShowMode.Transient, // Focus stays where it was.
        };
        flyout.ShowAt(anchor);
        var timer = anchor.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.2);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            flyout.Hide();
        };
        timer.Start();
    }
}
