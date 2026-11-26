using System.Runtime.CompilerServices;
using Fluorite.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Views;

/// <summary>
/// <c>local:ShiftDelete.IsEnabled="True"</c> on a menu's Delete item: while Shift is held it reads "Delete
/// permanently", Shift+Del, as in Explorer, and a click on it then deletes for good. It follows Shift for as long as
/// the menu is open, so one opened with Shift+F10 turns back into plain Delete once Shift is let go.
/// </summary>
public static class ShiftDelete
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ShiftDelete), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly ConditionalWeakTable<MenuFlyoutItem, State> s_states = new();

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>
    /// Whether a Delete click deletes for good. A menu item's click does what the item said when it was clicked;
    /// anything else (a key, a button) goes by Shift being held.
    /// </summary>
    public static bool ForGood(object sender) =>
        sender is MenuFlyoutItem item && s_states.TryGetValue(item, out var state) && state.Text is not null ? state.ForGood : ItemKeys.IsShiftDown;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MenuFlyoutItem item || e.NewValue is not true)
            return;
        // A menu's items are in the window only while it's open: Shift is watched just then.
        item.Loaded += (_, _) =>
        {
            var state = s_states.GetValue(item, _ => new State());
            state.Text ??= item.Text; // What x:Uid gave it: "Delete".
            Update(item, state, force: true);
            if (state.Timer is null)
            {
                state.Timer = item.DispatcherQueue.CreateTimer();
                state.Timer.Interval = TimeSpan.FromMilliseconds(50);
                state.Timer.Tick += (_, _) => Update(item, state, force: false);
            }
            state.Timer.Start();
        };
        item.Unloaded += (_, _) =>
        {
            if (s_states.TryGetValue(item, out var state))
                state.Timer?.Stop();
        };
    }

    private static void Update(MenuFlyoutItem item, State state, bool force)
    {
        var forGood = ItemKeys.IsShiftDown;
        if (forGood == state.ForGood && !force)
            return;
        state.ForGood = forGood;
        item.Text = forGood ? Loc.Get("Delete_Permanently") : state.Text;
        item.KeyboardAcceleratorTextOverride = forGood ? ItemKeys.DeletePermanently : ItemKeys.Delete;
    }

    private sealed class State
    {
        public string? Text;
        public bool ForGood;
        public DispatcherQueueTimer? Timer;
    }
}
