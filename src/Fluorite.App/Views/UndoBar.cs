using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Fluorite.App.Views;

/// <summary>The "Removed “X”. Undo" bar of a list; Ctrl+Z undoes too (not while typing in a box: that's the box's own undo).</summary>
public sealed partial class UndoBar : UserControl
{
    public static readonly DependencyProperty OfferProperty = DependencyProperty.Register(
        nameof(Offer), typeof(UndoOffer), typeof(UndoBar), new PropertyMetadata(null, (d, _) => ((UndoBar)d).Update()));

    private readonly InfoBar _bar;

    public UndoBar()
    {
        var undo = new Button { Content = Loc.Get("Common_Undo") };
        ToolTipService.SetToolTip(undo, Loc.Get("Common_UndoTip"));
        undo.Click += async (_, _) =>
        {
            if (Offer is { } offer)
                await offer.UndoAsync();
        };
        var key = new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
        key.Invoked += Undo_Invoked;
        KeyboardAccelerators.Add(key);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        _bar = new InfoBar { Severity = InfoBarSeverity.Informational, IsClosable = true, IsOpen = false, ActionButton = undo };
        // Closing it says "fine, it's gone".
        _bar.CloseButtonClick += async (_, _) =>
        {
            if (Offer is { } offer)
                await offer.CommitAsync();
        };
        _bar.PointerEntered += (_, _) => Offer?.Pause();
        _bar.PointerExited += (_, _) => Offer?.Resume();
        Content = _bar;
    }

    public UndoOffer? Offer
    {
        get => (UndoOffer?)GetValue(OfferProperty);
        set => SetValue(OfferProperty, value);
    }

    private void Update()
    {
        _bar.Message = Offer?.Message ?? "";
        _bar.IsOpen = Offer is not null;
    }

    private async void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Offer is not { } offer || !ItemKeys.IsOnScreen(this) || FocusManager.GetFocusedElement(XamlRoot) is TextBox or RichEditBox or PasswordBox)
            return;
        args.Handled = true;
        await offer.UndoAsync();
    }
}
