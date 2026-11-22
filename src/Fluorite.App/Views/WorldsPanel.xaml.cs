using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Fluorite.App.Views;

/// <summary>An instance's worlds with their backups; accepts dropped world zips and folders.</summary>
public sealed partial class WorldsPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(WorldsViewModel), typeof(WorldsPanel),
        new PropertyMetadata(null, (d, _) => ((WorldsPanel)d).Bindings?.Update()));

    public WorldsPanel()
    {
        InitializeComponent();
        // Also keys a row handled for itself; these act only on a focused row, not on a button inside it.
        List.AddHandler(KeyDownEvent, new KeyEventHandler(List_KeyDown), handledEventsToo: true);
        BackupList.AddHandler(KeyDownEvent, new KeyEventHandler(BackupList_KeyDown), handledEventsToo: true);
        ItemKeys.AddRefreshKey(this, () => ViewModel?.RefreshCommand);
    }

    /// <summary>Ctrl+F on the instance page.</summary>
    public bool FocusFilter() => FilterBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);

    /// <summary>Right-click or the menu key on a row opens its "…" menu; on no world, the list's own menu.</summary>
    private void Rows_ContextRequested(UIElement sender, ContextRequestedEventArgs args) => ItemKeys.ShowRowMenu((ListViewBase)sender, args);

    /// <summary>The keys shown in a world's menu, plus Enter (or Ctrl+Enter) to play and Ctrl+B to back up.</summary>
    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemKeys.FocusedRow(List) is not { } row || Bind.ItemOf<WorldItemViewModel>(row) is not { } world)
            return;
        switch (ItemKeys.Chord(e))
        {
            case ItemKeys.Open or ItemKeys.Play when world.CanPlay:
                Play_Click(row, e);
                break;
            case ItemKeys.Backup when !world.IsBusy:
                Backup_Click(row, e);
                break;
            case ItemKeys.Rename when !world.HasError:
                Rename_Click(row, e);
                break;
            case ItemKeys.Duplicate when !world.HasError:
                Copy_Click(row, e);
                break;
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

    private void BackupList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemKeys.FocusedRow(BackupList) is not { } row)
            return;
        switch (ItemKeys.Chord(e))
        {
            case ItemKeys.ShowInFolder:
                BackupShowInFolder_Click(row, e);
                break;
            case ItemKeys.Delete:
                BackupDelete_Click(row, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    public WorldsViewModel? ViewModel
    {
        get => (WorldsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static bool ShowNoQuickPlay(bool canOpenWorlds, bool isEmpty) => !canOpenWorlds && !isEmpty;

    /// <summary>"  ·  text", or nothing for empty text (joins optional parts of a caption).</summary>
    public static string Separated(string? text) => string.IsNullOrEmpty(text) ? "" : "  ·  " + text;

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is WorldItemViewModel item)
            _ = item.LoadIconAsync();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            vm.Play(world);
    }

    private void World_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { CanPlay: true } world)
            vm.Play(world);
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            await vm.BackupAsync(world);
    }

    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { SelectedWorld: { } world } vm)
            await vm.BackupAsync(world);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            await vm.RenameAsync(world);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            await vm.CopyAsync(world);
    }

    private void DataPacks_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            vm.ShowDataPacks?.Invoke(world.Info.FolderName);
    }

    private void CopySeed_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            vm.CopySeed(world);
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            vm.CreateShortcut(world);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            WorldsViewModel.ShowInFolder(world);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldItemViewModel>(sender) is { } world)
            await vm.DeleteAsync(world);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldBackupItem>(sender) is { } backup)
            await vm.RestoreAsync(backup);
    }

    private void BackupShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<WorldBackupItem>(sender) is { } backup)
            WorldsViewModel.ShowBackupInFolder(backup);
    }

    private async void BackupDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<WorldBackupItem>(sender) is { } backup)
            await vm.DeleteBackupAsync(backup);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || App.Window is not { } window)
            return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".zip");
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            await vm.ImportAsync(files.Select(f => f.Path));
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get("Worlds_DropCaption");
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Where(i => i is StorageFolder || (i is StorageFile f && f.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase))).Select(i => i.Path).ToList();
        if (paths.Count > 0)
            await vm.ImportAsync(paths);
    }
}
