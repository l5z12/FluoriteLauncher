using System.Collections.ObjectModel;
using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Fluorite.App.Views;

/// <summary>
/// Ctrl+K: type to find an instance, page, setting or action and run it with Enter. Lives over the whole window,
/// collapsed until opened.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private MainWindow? _window;
    private List<PaletteCommand> _all = [];
    private int _session;
    private Control? _previousFocus;

    public CommandPalette()
    {
        InitializeComponent();
    }

    public ObservableCollection<PaletteCommand> Items { get; } = [];

    public bool IsOpen => Visibility == Visibility.Visible;

    public async Task OpenAsync(MainWindow window)
    {
        if (IsOpen)
        {
            SearchBox.Focus(FocusState.Programmatic);
            return;
        }
        _window = window;
        _previousFocus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        SearchBox.Text = "";
        Items.Clear();
        Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);

        // The list is rebuilt per opening (instances, accounts and running games change); typing works meanwhile.
        var session = ++_session;
        var all = await PaletteCommands.BuildAsync(window);
        if (session != _session)
            return;
        _all = all;
        Filter();
    }

    public void Close(bool restoreFocus = true)
    {
        if (!IsOpen)
            return;
        _session++;
        Visibility = Visibility.Collapsed;
        Items.Clear();
        _all = [];
        if (restoreFocus)
            _previousFocus?.Focus(FocusState.Programmatic);
        _previousFocus = null;
    }

    private void Filter()
    {
        if (_window is null)
            return;
        var text = SearchBox.Text.Trim();
        IEnumerable<PaletteCommand> results;
        if (text.Length == 0)
        {
            results = _all.Where(c => c.Suggested);
        }
        else
        {
            var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            results = _all
                .Select((command, order) => (command, order, score: command.Score(terms)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.order)
                .Take(50)
                .Select(x => x.command)
                .Concat(PaletteCommands.ForQuery(_window, text));
        }
        Items.Clear();
        foreach (var command in results)
            Items.Add(command);
        if (Items.Count > 0)
        {
            Results.SelectedIndex = 0;
            Results.ScrollIntoView(Items[0]);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Filter();

    private async void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                Move(1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                Move(-1);
                break;
            case VirtualKey.PageDown:
                e.Handled = true;
                Move(8);
                break;
            case VirtualKey.PageUp:
                e.Handled = true;
                Move(-8);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                await RunAsync(Results.SelectedItem as PaletteCommand);
                break;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Moves the highlighted result while typing continues in the box; Narrator reads the new one.</summary>
    private void Move(int delta)
    {
        if (Items.Count == 0)
            return;
        var index = Math.Clamp(Results.SelectedIndex + delta, 0, Items.Count - 1);
        Results.SelectedIndex = index;
        Results.ScrollIntoView(Items[index]);
        var peer = FrameworkElementAutomationPeer.FromElement(SearchBox) ?? FrameworkElementAutomationPeer.CreatePeerForElement(SearchBox);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ItemAdded, AutomationNotificationProcessing.ImportantMostRecent,
            Items[index].ToString(), "PaletteSelection");
    }

    private async void Results_ItemClick(object sender, ItemClickEventArgs e) => await RunAsync(e.ClickedItem as PaletteCommand);

    private void Backdrop_Tapped(object sender, TappedRoutedEventArgs e) => Close();

    private async Task RunAsync(PaletteCommand? command)
    {
        if (command is null)
            return;
        Close(restoreFocus: false);
        try
        {
            await command.Run();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Palette_Failed"), ex.Message);
        }
    }
}
