using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.Views;

/// <summary>An instance's multiplayer servers with live status; drag to reorder.</summary>
public sealed partial class ServersPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ServersViewModel), typeof(ServersPanel),
        new PropertyMetadata(null, (d, _) => ((ServersPanel)d).Bindings?.Update()));

    public ServersPanel()
    {
        InitializeComponent();
        // Also keys a row handled for itself; these act only on a focused row, not on a button inside it.
        List.AddHandler(KeyDownEvent, new KeyEventHandler(List_KeyDown), handledEventsToo: true);
        ItemKeys.AddRefreshKey(this, () => ViewModel?.RefreshCommand);
    }

    /// <summary>Right-click or the menu key on a row opens its "…" menu; on no row, the list's own menu (Add, Refresh).</summary>
    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args) => ItemKeys.ShowRowMenu(List, args);

    /// <summary>The keys shown in a server's menu, plus Enter (or Ctrl+Enter) to join.</summary>
    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemKeys.FocusedRow(List) is not { } row || Bind.ItemOf<ServerItemViewModel>(row) is null)
            return;
        switch (ItemKeys.Chord(e))
        {
            case ItemKeys.Open or ItemKeys.Play:
                Join_Click(row, e);
                break;
            case ItemKeys.Rename:
                Edit_Click(row, e);
                break;
            case ItemKeys.Copy:
                CopyAddress_Click(row, e);
                break;
            case ItemKeys.MoveUp:
                MoveUp_Click(row, e);
                break;
            case ItemKeys.MoveDown:
                MoveDown_Click(row, e);
                break;
            case ItemKeys.Delete or ItemKeys.DeletePermanently: // An entry in servers.dat: no Recycle Bin either way.
                Delete_Click(row, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ServerItemViewModel>(sender) is { } server && vm.Move(server, delta))
            ItemKeys.FocusRow(List, server);
    }

    public ServersViewModel? ViewModel
    {
        get => (ServersViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static Brush SignalBrush(bool online) =>
        (Brush)Application.Current.Resources[online ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush"];

    private void List_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => ViewModel?.SaveOrder();

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ServerItemViewModel>(sender) is { } server)
            vm.Join(server);
    }

    private void Server_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ServerItemViewModel>(sender) is { } server)
            vm.Join(server);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ServerItemViewModel>(sender) is { } server)
            await vm.EditAsync(server);
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ServerItemViewModel>(sender) is { } server)
            vm.CopyAddress(server);
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ServerItemViewModel>(sender) is { } server)
            vm.CreateShortcut(server);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ServerItemViewModel>(sender) is { } server)
            vm.Delete(server);
    }

    private void AddServer_Click(object sender, RoutedEventArgs e) => ViewModel?.AddCommand.Execute(null);

    private void RefreshServers_Click(object sender, RoutedEventArgs e) => ViewModel?.RefreshCommand.Execute(null);
}
