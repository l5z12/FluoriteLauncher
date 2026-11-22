using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Fluorite.App.Views;

/// <summary>
/// Keys for cards and list rows, the same on every page. Each menu item shows its key
/// (<c>KeyboardAcceleratorTextOverride</c>, written exactly as these constants), and the page maps the chord to the
/// handler that menu item uses. They act on the item with keyboard focus, never while typing in a box.
/// </summary>
internal static class ItemKeys
{
    public const string Open = "Enter";
    public const string Play = "Ctrl+Enter";
    public const string Rename = "F2";
    public const string Duplicate = "Ctrl+D";
    public const string Copy = "Ctrl+C";
    public const string Backup = "Ctrl+B";
    public const string Enable = "Ctrl+E";
    public const string Disable = "Ctrl+Shift+E";
    public const string ShowInFolder = "Ctrl+Shift+O";
    public const string Delete = "Del";
    public const string MoveUp = "Alt+Up";
    public const string MoveDown = "Alt+Down";
    public const string Select = "Ctrl+Space";

    /// <summary>Hide (or show again) a Library instance or group.</summary>
    public const string Hide = "Ctrl+H";

    /// <summary>Windows' own key for Properties: a mod's info.</summary>
    public const string Properties = "Alt+Enter";

    /// <summary>Held during a click: Ctrl+click and Shift+click select instead of opening.</summary>
    public static bool IsCtrlDown => IsDown(VirtualKey.Control);

    public static bool IsShiftDown => IsDown(VirtualKey.Shift);

    /// <summary>The chord just pressed, written the way the menus show it; null for a key no item uses.</summary>
    public static string? Chord(KeyRoutedEventArgs e)
    {
        var key = e.Key switch
        {
            VirtualKey.Enter => "Enter",
            VirtualKey.Delete => "Del",
            VirtualKey.F2 => "F2",
            VirtualKey.Space => "Space",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            >= VirtualKey.A and <= VirtualKey.Z => ((char)e.Key).ToString(),
            _ => null,
        };
        if (key is null)
            return null;
        var alt = e.KeyStatus.IsMenuKeyDown || IsDown(VirtualKey.Menu);
        return (IsDown(VirtualKey.Control) ? "Ctrl+" : "") + (IsDown(VirtualKey.Shift) ? "Shift+" : "") + (alt ? "Alt+" : "") + key;
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// The row of <paramref name="list"/> that has keyboard focus itself. Null when focus is on a control inside
    /// the row (its own Enter, Space… stay its own) or elsewhere.
    /// </summary>
    public static SelectorItem? FocusedRow(ListViewBase list) =>
        list.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is SelectorItem row && list.IndexFromContainer(row) >= 0 ? row : null;

    /// <summary>True when <paramref name="element"/> itself has keyboard focus (a card, not a button inside it).</summary>
    public static bool HasFocus(UIElement element) =>
        element.XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), element);

    /// <summary>
    /// In the window and not inside a collapsed panel (the instance page keeps every tab's panel, collapsed): a key
    /// for one tab mustn't act while another is shown.
    /// </summary>
    public static bool IsOnScreen(UIElement element)
    {
        // Up to the window's content and no further (above it are the window's own internals). Never reaching it means
        // the element isn't in the window at all: a cached page that isn't the one shown.
        if (element.XamlRoot?.Content is not { } top)
            return false;
        for (DependencyObject? d = element; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement { Visibility: Visibility.Collapsed })
                return false;
            if (ReferenceEquals(d, top))
                return true;
        }
        return false;
    }

    /// <summary>F5 on a panel or page runs its refresh, while it's the one on screen.</summary>
    public static void AddRefreshKey(UIElement panel, Func<System.Windows.Input.ICommand?> command)
    {
        var key = new KeyboardAccelerator { Key = VirtualKey.F5 };
        var last = 0L;
        key.Invoked += async (_, e) =>
        {
            if (!IsOnScreen(panel) || command() is not { } c)
                return;
            e.Handled = true;
            // F5 held down repeats ~30 times a second: one refresh a second is plenty, and lists rebuilt that often
            // ended the app inside WinUI.
            var now = Environment.TickCount64;
            if (now - last < 1000 || !c.CanExecute(null))
                return;
            last = now;
            if (c is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand run)
            {
                // Awaited here, not through Execute: a failure is logged instead of ending the app.
                try
                {
                    await run.ExecuteAsync(null);
                }
                catch (Exception ex)
                {
                    App.Get<ILogger<App>>().LogError(ex, "Refresh (F5) failed");
                }
            }
            else
            {
                c.Execute(null);
            }
        };
        panel.KeyboardAccelerators.Add(key);
        panel.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>A panel's own menu for a right-click on no row, where the pointer is (or at the top left from the keyboard).</summary>
    public static void ShowPanelMenu(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase menu, UIElement target, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        menu.ShowAt(target, new FlyoutShowOptions { Position = e.TryGetPosition(target, out var point) ? point : new Windows.Foundation.Point(24, 24) });
    }

    /// <summary>After a row moved (Alt+Up/Down), keyboard focus follows it to its new place.</summary>
    public static void FocusRow(ListViewBase list, object item) =>
        list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            list.ScrollIntoView(item);
            (list.ContainerFromItem(item) as Control)?.Focus(FocusState.Programmatic); // A focus rectangle only after keyboard use.
        });

    /// <summary>
    /// Right-click or the menu key (Shift+F10) on a row opens the menu behind its "…" button, so rows whose menu
    /// lives there have one menu for mouse, touch and keyboard.
    /// </summary>
    public static void ShowRowMenu(ListViewBase list, ContextRequestedEventArgs e)
    {
        var row = Ancestor<SelectorItem>(e.OriginalSource as DependencyObject, list);
        if (row is null || Descendant(row, d => d is Button { Flyout: MenuFlyout }) is not Button { Flyout: MenuFlyout menu } more)
            return;
        e.Handled = true;
        if (e.TryGetPosition(row, out var point))
        {
            row.Focus(FocusState.Pointer); // See FocusOnRightPress: the keys then act on this row after the menu, too.
            menu.ShowAt(row, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(more);
        }
    }

    /// <summary>
    /// A right-click puts keyboard focus on the row or card under the pointer, as a left click does. The keys its
    /// menu shows then act on it once the menu has closed too: focus otherwise goes back to wherever it was before
    /// (the tab bar, say), and the keys go there with it.
    /// </summary>
    public static void FocusOnRightPress(UIElement host) =>
        host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(host).Properties.IsRightButtonPressed && ItemUnder(e.OriginalSource as DependencyObject, host) is { } item)
                item.Focus(FocusState.Pointer);
        }), handledEventsToo: true);

    /// <summary>The list row, or the card (a control with its own menu), that <paramref name="d"/> is part of.</summary>
    private static Control? ItemUnder(DependencyObject? d, DependencyObject stop)
    {
        for (; d is not null && d != stop; d = VisualTreeHelper.GetParent(d))
        {
            if (d is SelectorItem or Control { ContextFlyout: not null })
                return (Control)d;
        }
        return null;
    }

    private static T? Ancestor<T>(DependencyObject? d, DependencyObject stop) where T : DependencyObject
    {
        for (; d is not null && d != stop; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T found)
                return found;
        }
        return null;
    }

    private static DependencyObject? Descendant(DependencyObject root, Func<DependencyObject, bool> match)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (match(child))
                return child;
            if (Descendant(child, match) is { } found)
                return found;
        }
        return null;
    }
}
