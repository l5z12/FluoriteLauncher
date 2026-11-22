using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Fluorite.App.Views;

/// <summary>An instance's snapshots: take one, go back to one.</summary>
public sealed partial class SnapshotsPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SnapshotsViewModel), typeof(SnapshotsPanel),
        new PropertyMetadata(null, (d, _) => ((SnapshotsPanel)d).Bindings?.Update()));

    public SnapshotsPanel()
    {
        InitializeComponent();
        // Also keys a row handled for itself; these act only on a focused row, not on a button inside it.
        List.AddHandler(KeyDownEvent, new KeyEventHandler(List_KeyDown), handledEventsToo: true);
        ItemKeys.AddRefreshKey(this, () => ViewModel?.RefreshCommand);
    }

    public SnapshotsViewModel? ViewModel
    {
        get => (SnapshotsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Right-click or the menu key on a row opens its "…" menu; on no row, the list's own menu.</summary>
    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args) => ItemKeys.ShowRowMenu(List, args);

    /// <summary>The keys shown in a snapshot's menu (restoring stays a click: it replaces files).</summary>
    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemKeys.FocusedRow(List) is not { } row)
            return;
        switch (ItemKeys.Chord(e))
        {
            case ItemKeys.ShowInFolder:
                ShowInFolder_Click(row, e);
                break;
            case ItemKeys.Delete:
                Delete_Click(row, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<SnapshotItem>(sender) is { } item)
            await vm.RestoreAsync(item);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<SnapshotItem>(sender) is { } item)
            SnapshotsViewModel.ShowInFolder(item);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<SnapshotItem>(sender) is { } item)
            await vm.DeleteAsync(item);
    }
}
