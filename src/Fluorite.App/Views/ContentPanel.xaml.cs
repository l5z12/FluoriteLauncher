using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Fluorite.App.Views;

/// <summary>List of an instance's mods / resource packs / shaders / data packs with actions; accepts dropped files.</summary>
public sealed partial class ContentPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ContentListViewModel), typeof(ContentPanel),
        new PropertyMetadata(null, (d, _) => ((ContentPanel)d).Bindings?.Update()));

    public ContentPanel()
    {
        InitializeComponent();
        // Also keys a row handled for itself: the list's own keys come first, and these act only where they mean something.
        List.AddHandler(KeyDownEvent, new KeyEventHandler(List_KeyDown), handledEventsToo: true);
        // Rows handle their own presses; the right button's is wanted anyway.
        List.AddHandler(PointerPressedEvent, new PointerEventHandler(List_PointerPressed), handledEventsToo: true);
        ItemKeys.AddRefreshKey(this, () => ViewModel?.RefreshCommand);
    }

    /// <summary>The row the last right-click was on (null: the blank part), and whether the menu about to open is from it.</summary>
    private ContentItemViewModel? _rightClickedRow;
    private bool _menuFromPointer;

    /// <summary>
    /// A right-click selects the row under the pointer, as Explorer does: alone, unless it's already part of the
    /// selection (then the selection stays, for acting on all of it). It also takes keyboard focus, as a left click
    /// does, so the menu's keys still act on the row once the menu has closed. Pressed, so before the menu opens.
    /// </summary>
    private void List_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _menuFromPointer = e.GetCurrentPoint(List).Properties.IsRightButtonPressed;
        if (!_menuFromPointer)
            return;
        var container = ContainerOf(e.OriginalSource as DependencyObject);
        _rightClickedRow = container is null ? null : List.ItemFromContainer(container) as ContentItemViewModel;
        if (_rightClickedRow is { } row && !List.SelectedItems.Contains(row))
        {
            List.SelectedItems.Clear();
            List.SelectedItems.Add(row);
        }
        container?.Focus(FocusState.Pointer);
    }

    private ListViewItem? ContainerOf(DependencyObject? element)
    {
        for (; element is not null && element != List; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem container)
                return container;
        }
        return null;
    }

    /// <summary>
    /// The row a key for one row acts on: the one with focus, else the selection when it's a single row (focus on
    /// the row's switch, say, or in its menu).
    /// </summary>
    private ContentItemViewModel? KeyTarget() =>
        ItemKeys.FocusedRow(List) is { } focused ? List.ItemFromContainer(focused) as ContentItemViewModel
        : List.SelectedItems.Count == 1 ? List.SelectedItems[0] as ContentItemViewModel
        : null;

    /// <summary>
    /// Mod info and Edit manifest for the mod row it opened on (right-clicked, or focused for the menu key); the
    /// selection's actions when there is one; on the blank part with nothing selected, only the list's own.
    /// </summary>
    private void ListMenu_Opening(object sender, object e)
    {
        var target = _menuFromPointer ? _rightClickedRow : ItemKeys.FocusedRow(List) is { } focused ? List.ItemFromContainer(focused) as ContentItemViewModel : null;
        _menuFromPointer = false;
        var visibility = List.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MenuEnable.Visibility = MenuDisable.Visibility = MenuDelete.Visibility = MenuSelectionSeparator.Visibility = visibility;
        var row = target is { IsMod: true } ? Visibility.Visible : Visibility.Collapsed;
        MenuModInfo.Visibility = MenuEditManifest.Visibility = MenuRowSeparator.Visibility = row;
        MenuModInfo.Tag = MenuEditManifest.Tag = target;
    }

    private async void ModInfo_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ContentItemViewModel>(sender) is { IsMod: true } item)
            await ShowModInfoAsync(item, vm);
    }

    private async void EditManifest_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ContentItemViewModel>(sender) is { IsMod: true } item)
        {
            await ManifestEditorDialog.ShowAsync(item, vm);
            ItemKeys.FocusRow(List, item);
        }
    }

    /// <summary>
    /// Then focus goes back to the row: opened from a menu, the dialog would return it to that menu's item, which is
    /// gone, and the next key would reach nothing.
    /// </summary>
    private async Task ShowModInfoAsync(ContentItemViewModel item, ContentListViewModel vm)
    {
        await ModInfoDialog.ShowAsync(item, vm);
        ItemKeys.FocusRow(List, item);
    }

    /// <summary>Ctrl+F on the instance page.</summary>
    public bool FocusFilter() => FilterBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);

    public ContentListViewModel? ViewModel
    {
        get => (ContentListViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private List<ContentItemViewModel> Selected => List.SelectedItems.OfType<ContentItemViewModel>().ToList();

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ContentItemViewModel item)
            _ = item.LoadIconAsync();
    }

    /// <summary>The keys shown in the menus: Del, Ctrl+E and Ctrl+Shift+E on the selection; Enter and Ctrl+Shift+O on the focused row.</summary>
    private async void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var selected = Selected;
        var row = ItemKeys.FocusedRow(List);
        var target = KeyTarget();
        // Pressed with the list's menu open (its keys reach the list too): the menu goes, its items would be stale.
        void Act()
        {
            e.Handled = true;
            ListMenu.Hide();
        }
        switch (ItemKeys.Chord(e))
        {
            case ItemKeys.Delete when selected.Count > 0:
                Act();
                await vm.DeleteAsync(selected);
                break;
            case ItemKeys.Enable when selected.Count > 0:
                Act();
                await vm.SetAllEnabledAsync(selected, true);
                break;
            case ItemKeys.Disable when selected.Count > 0:
                Act();
                await vm.SetAllEnabledAsync(selected, false);
                break;
            // Enter only on the row itself: on its buttons, and in a menu, Enter is theirs.
            case ItemKeys.Open when row is not null && Bind.ItemOf<ContentItemViewModel>(row) is { HasSource: true }:
                Act();
                OpenProject_Click(row, e);
                break;
            case ItemKeys.ShowInFolder when target is not null:
                Act();
                ContentListViewModel.ShowInFolder(target);
                break;
            case ItemKeys.Properties when target is { IsMod: true }:
                Act();
                await ShowModInfoAsync(target, vm);
                break;
        }
    }

    private async void EnableSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.SetAllEnabledAsync(Selected, true);
    }

    private async void DisableSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.SetAllEnabledAsync(Selected, false);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.DeleteAsync(Selected);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ContentItemViewModel>(sender) is { } item)
            await vm.DeleteAsync([item]);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ContentItemViewModel>(sender) is { } item)
            await vm.UpdateOneAsync(item);
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ContentItemViewModel>(sender) is { } item)
            await vm.TogglePinAsync(item);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ContentItemViewModel>(sender) is { } item)
            ContentListViewModel.ShowInFolder(item);
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ContentItemViewModel>(sender) is { } item && vm.ProjectFor(item) is { } nav)
            App.Window?.NavigateToProject(nav);
    }

    private async void Changelog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Bind.ItemOf<ContentItemViewModel>(sender) is { Update: { } latest } item)
            await Dialogs.ShowHtmlAsync(Loc.Format("Changelog_Title", $"{item.Name} {latest.VersionNumber}"), () => vm.GetChangelogHtmlAsync(item));
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { Location: { } location } vm || App.Window is not { } window)
            return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        foreach (var ext in location.Extensions)
            picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            ReportImported(await vm.ImportAsync(files.Select(f => f.Path)));
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && ViewModel?.Location is not null)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get("Content_DropCaption");
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        ReportImported(await vm.ImportAsync(items.OfType<StorageFile>().Select(f => f.Path)));
    }

    private void ReportImported(int count)
    {
        if (ViewModel is { } vm)
            vm.Status = count > 0 ? Loc.Format("Content_Imported", count) : Loc.Get("Content_ImportNothing");
    }
}
