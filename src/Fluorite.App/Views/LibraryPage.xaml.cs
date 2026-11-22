using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Core.Versions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

/// <summary>
/// Cached (<c>NavigationCacheMode="Required"</c>), like the other pages whose view model is a singleton (Home, Accounts,
/// Settings, Activity): one page per shared collection. A new page per visit left old ones listening, and refilling the
/// collections then failed inside them (E_FAIL) and ended the app.
/// </summary>
public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        InitializeComponent();
        ItemKeys.FocusOnRightPress(this); // Cards and group headers: their menus show keys for the focused one.
        ItemKeys.AddRefreshKey(this, () => ViewModel.LoadCommand);
        NewInstanceLoader.ViewModel = Picker;
        Picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoaderPickerViewModel.SelectedOptionIndex))
                UpdateNamePlaceholder();
        };
    }

    /// <summary>Ctrl+F.</summary>
    public void FocusSearch() => SearchBox.Focus(FocusState.Keyboard);

    /// <summary>Ctrl+N.</summary>
    public void StartNewInstance() => NewInstance_Click(this, new RoutedEventArgs());

    /// <summary>Ctrl+O.</summary>
    public void StartImportFile() => ImportFile_Click(this, new RoutedEventArgs());

    /// <summary>The keys shown in a card's menu, on the focused card (Enter is the card's own click: Open).</summary>
    private void Card_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is not UIElement card || !ItemKeys.HasFocus(card) || Bind.ItemOf<InstanceItem>(sender) is not { } item)
            return;
        switch (ItemKeys.Chord(e))
        {
            case ItemKeys.Select:
                ViewModel.ToggleSelected(item);
                break;
            case ItemKeys.Delete when item.IsSelected && ViewModel.SelectionCount > 1:
                DeleteSelection_Click(sender, e);
                break;
            case ItemKeys.Play when !item.IsRunning:
                Play_Click(sender, e);
                break;
            case ItemKeys.Rename:
                Rename_Click(sender, e);
                break;
            case ItemKeys.Duplicate:
                Duplicate_Click(sender, e);
                break;
            case ItemKeys.ShowInFolder:
                OpenFolder_Click(sender, e);
                break;
            case ItemKeys.Delete when !item.IsExternal:
                Delete_Click(sender, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>The empty name box shows the name the instance will get: "Fabric 1.21.1".</summary>
    private void UpdateNamePlaceholder() =>
        NewInstanceName.PlaceholderText = NewInstanceVersion.SelectedItem is VersionManifestEntry v
            ? LibraryViewModel.DefaultName(v.Id, Picker.SelectedUid)
            : Loc.Get("NewInstance_Name/PlaceholderText");

    private LoaderPickerViewModel Picker { get; } = App.Get<LoaderPickerViewModel>();

    public LibraryViewModel ViewModel { get; } = App.Get<LibraryViewModel>();

    private static ModpackInstallService Packs => App.Get<ModpackInstallService>();
    private static OtherLaunchersService Launchers => App.Get<OtherLaunchersService>();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        Packs.InstanceCreated += OnInstanceAdded;
        Launchers.InstanceImported += OnInstanceAdded;
        Launchers.InstancesImported += OnLibraryChanged;
        Launchers.RootsChanged += OnLibraryChanged;
        await ViewModel.LoadAsync();

        // From the command palette. Only on a new visit: going back to this entry mustn't open the dialog again.
        if (e.NavigationMode == NavigationMode.New && e.Parameter is string action)
        {
            switch (action)
            {
                case "NewInstance":
                    NewInstance_Click(this, new RoutedEventArgs());
                    break;
                case "ImportFile":
                    ImportFile_Click(this, new RoutedEventArgs());
                    break;
                case "ImportLink":
                    ImportLink_Click(this, new RoutedEventArgs());
                    break;
            }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Packs.InstanceCreated -= OnInstanceAdded;
        Launchers.InstanceImported -= OnInstanceAdded;
        Launchers.InstancesImported -= OnLibraryChanged;
        Launchers.RootsChanged -= OnLibraryChanged;
    }

    private async void OnInstanceAdded(object? sender, Fluorite.Core.Instances.GameInstance instance)
    {
        await ViewModel.LoadAsync();
        if (ViewModel.Instances.FirstOrDefault(i => i.Instance.Id == instance.Id) is { } item)
            App.Window?.NavigateToInstance(item);
    }

    private async void OnLibraryChanged(object? sender, EventArgs e) => await ViewModel.LoadAsync();

    // ── Import ────────────────────────────────────────────────────────────

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is not { } window)
            return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".mrpack");
        picker.FileTypeFilter.Add(".zip");
        if ((await picker.PickSingleFileAsync())?.Path is { } path)
            await Packs.InstallFromFileAsync(path);
    }

    private async void ImportLink_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = Loc.Get("Import_LinkPlaceholder"), MinWidth = 520 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Loc.Get("Import_LinkField"));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Dialogs.Text(Loc.Get("Import_LinkHint"), secondary: true));
        panel.Children.Add(box);
        if (await Dialogs.ShowAsync(Loc.Get("Import_LinkTitle"), panel, Loc.Get("Import_LinkButton")) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
            await Packs.InstallFromLocationAsync(box.Text.Trim());
    }

    private async void ImportLauncher_Click(object sender, RoutedEventArgs e) => await Launchers.ShowImportDialogAsync();

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get("Import_DropCaption");
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var file in items.OfType<Windows.Storage.StorageFile>().Where(f => f.Path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            await Packs.InstallFromFileAsync(file.Path);
            break; // One at a time; each install asks for confirmation.
        }
    }

    /// <summary>A click opens the instance; on a card or row, Ctrl+click and Shift+click select instead.</summary>
    private void Instance_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is not { } item)
            return;
        if (sender is Button)
        {
            if (ItemKeys.IsCtrlDown)
            {
                ViewModel.ToggleSelected(item);
                return;
            }
            if (ItemKeys.IsShiftDown)
            {
                ViewModel.SelectRange(item);
                return;
            }
        }
        ViewModel.ClearSelection();
        App.Window?.NavigateToInstance(item);
    }

    /// <summary>The card's update badge: straight to its Mods tab.</summary>
    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item)
            App.Window?.NavigateToInstance(item, tab: nameof(Fluorite.Modding.Content.ContentKind.Mod));
    }

    // ── Selection ─────────────────────────────────────────────────────────

    /// <summary>Ctrl+A selects every instance shown; not while typing (the box selects its text).</summary>
    private void SelectAll_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTypingOrInDialog())
            return;
        args.Handled = true;
        ViewModel.SelectAll();
    }

    private void ClearSelection_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.SelectionCount == 0 || IsTypingOrInDialog())
            return;
        args.Handled = true;
        ViewModel.ClearSelection();
    }

    private bool IsTypingOrInDialog() =>
        XamlRoot is not { } root
        || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is TextBox
        || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    private void SelectionGroupMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu)
            FillGroupMenu(menu.Items, ViewModel.Selection);
    }

    private async void DeleteSelection_Click(object sender, RoutedEventArgs e)
    {
        var items = ViewModel.Selection;
        if (items.Count == 0)
            return;
        DeleteDialog.Title = Loc.Format("Library_DeleteManyTitle", items.Count);
        DeleteText.Text = string.Join("\n", items.Take(8).Select(i => "• " + i.Name)) + (items.Count > 8 ? "\n…" : "") + "\n\n" + Loc.Get("Library_DeleteManyBody");
        if (await DeleteDialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteAsync(items);
    }

    // ── Drag to a group ───────────────────────────────────────────────────

    /// <summary>What's being dragged: the selection when the card is part of it, else that card.</summary>
    private List<InstanceItem>? _dragging;

    private void Instance_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is not { } item)
        {
            args.Cancel = true;
            return;
        }
        _dragging = item.IsSelected && ViewModel.SelectionCount > 1 ? ViewModel.Selection.ToList() : [item];
        args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        args.Data.SetText(string.Join(", ", _dragging.Select(i => i.Name)));
    }

    private void GroupHeader_DragOver(object sender, DragEventArgs e)
    {
        if (_dragging is not { Count: > 0 } items || Bind.ItemOf<InstanceGroup>(sender) is not { } group)
            return;
        e.Handled = true;
        if (items.All(i => string.Equals(i.Group, group.Name, StringComparison.OrdinalIgnoreCase)))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None; // Already there.
            return;
        }
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.Caption = group.Name is { } name ? Loc.Format("Library_DropToGroup", name) : Loc.Get("Library_RemoveFromGroup");
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void GroupHeader_Drop(object sender, DragEventArgs e)
    {
        if (_dragging is not { Count: > 0 } items || Bind.ItemOf<InstanceGroup>(sender) is not { } group)
            return;
        e.Handled = true;
        _dragging = null;
        await ViewModel.SetGroupAsync(items, group.Name);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item)
            ViewModel.PlayCommand.Execute(item);
    }

    /// <summary>
    /// The one instance menu, shared by every card and row: it takes the instance from the element it opened on,
    /// and shows only what applies to it.
    /// </summary>
    private async void InstanceMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || Bind.ItemOf<InstanceItem>(menu.Target) is not { } item)
            return;
        foreach (var entry in menu.Items)
        {
            entry.Tag = item;
            switch (Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(entry))
            {
                case "Play" or "LaunchAs":
                    entry.IsEnabled = !item.IsRunning;
                    break;
                case "Duplicate" or "DeleteSeparator" or "Delete":
                    entry.Visibility = item.IsExternal ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "CopyToFluorite":
                    entry.Visibility = item.IsExternal ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "Group" when entry is MenuFlyoutSubItem groups:
                    FillGroupMenu(groups.Items, item);
                    break;
            }
        }
        await LaunchAsMenu.FillSubmenuAsync(sender, ViewModel.PlayAs);
    }

    private void FillGroupMenu(IList<MenuFlyoutItemBase> items, InstanceItem item) => FillGroupMenu(items, [item]);

    /// <summary>"Move to group" for one or more: the groups in use (the one they're all in checked), a new one, or none.</summary>
    private void FillGroupMenu(IList<MenuFlyoutItemBase> items, IReadOnlyList<InstanceItem> targets)
    {
        items.Clear();
        foreach (var name in ViewModel.GroupNames)
        {
            var current = targets.All(t => string.Equals(name, t.Group, StringComparison.OrdinalIgnoreCase));
            var entry = new MenuFlyoutItem { Text = name, Icon = current ? new FontIcon { Glyph = "" } : null, IsEnabled = !current };
            entry.Click += async (_, _) => await ViewModel.SetGroupAsync(targets, name);
            items.Add(entry);
        }
        if (items.Count > 0)
            items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = Loc.Get("Library_NewGroup"), Icon = new FontIcon { Glyph = "" } };
        create.Click += async (_, _) =>
        {
            if (await Dialogs.PromptAsync(Loc.Get("Library_NewGroupTitle"), Loc.Get("Library_NewGroupButton"), Loc.Get("Library_GroupName"),
                    placeholder: Loc.Get("Library_GroupNamePlaceholder")) is { } name)
                await ViewModel.SetGroupAsync(targets, name);
        };
        items.Add(create);
        if (targets.Any(t => t.Group is not null))
        {
            var remove = new MenuFlyoutItem { Text = Loc.Get("Library_RemoveFromGroup"), Icon = new FontIcon { Glyph = "" } };
            remove.Click += async (_, _) => await ViewModel.SetGroupAsync(targets, null);
            items.Add(remove);
        }
    }

    // ── Group headers ─────────────────────────────────────────────────────

    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceGroup>(sender) is { } group)
            ViewModel.ToggleGroup(group);
    }

    /// <summary>F2 renames the focused group, as its menu says.</summary>
    private void GroupHeader_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (ItemKeys.Chord(e) == ItemKeys.Rename && Bind.ItemOf<InstanceGroup>(sender) is { IsNamed: true })
        {
            e.Handled = true;
            RenameGroup_Click(sender, e);
        }
    }

    private async void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceGroup>(sender) is { Name: { } name }
            && await Dialogs.PromptAsync(Loc.Get("Library_RenameGroupTitle"), Loc.Get("RenameDialog/PrimaryButtonText"), Loc.Get("Library_GroupName"), initial: name) is { } newName)
            await ViewModel.RenameGroupAsync(name, newName);
    }

    /// <summary>The instances stay; they just move to the ungrouped ones.</summary>
    private async void Ungroup_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceGroup>(sender) is { Name: { } name })
            await ViewModel.RenameGroupAsync(name, null);
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => ViewModel.SetAllExpanded(true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => ViewModel.SetAllExpanded(false);

    // ── The blank-area menu ───────────────────────────────────────────────

    /// <summary>Ticks the current view and sort; offers expand/collapse only for groups that can.</summary>
    private void LibraryMenu_Opening(object sender, object e)
    {
        Check(MenuView, ViewModel.ViewIndex);
        Check(MenuSort, ViewModel.SortIndex);
        var headed = ViewModel.Groups.Where(g => g.HasHeader).ToList();
        MenuExpandAll.Visibility = headed.Any(g => !g.IsExpanded) ? Visibility.Visible : Visibility.Collapsed;
        MenuCollapseAll.Visibility = headed.Any(g => g.IsExpanded) ? Visibility.Visible : Visibility.Collapsed;

        static void Check(MenuFlyoutSubItem menu, int index)
        {
            for (var i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i] is RadioMenuFlyoutItem radio)
                    radio.IsChecked = i == index;
            }
        }
    }

    private void MenuView_Click(object sender, RoutedEventArgs e) => ViewModel.ViewIndex = MenuView.Items.IndexOf((MenuFlyoutItemBase)sender);

    private void MenuSort_Click(object sender, RoutedEventArgs e) => ViewModel.SortIndex = MenuSort.Items.IndexOf((MenuFlyoutItemBase)sender);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item)
            ViewModel.OpenFolderCommand.Execute(item);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is not { } item)
            return;
        _renaming = item.Name;
        RenameText.Text = item.Name;
        RenameText.PlaceholderText = item.Name;
        RenameDialog.IsPrimaryButtonEnabled = false;
        if (await RenameDialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RenameAsync(item, RenameText.Text);
    }

    private string _renaming = "";

    /// <summary>Ready to type over: the whole name is selected.</summary>
    private void RenameDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        RenameText.Focus(FocusState.Programmatic);
        RenameText.SelectAll();
    }

    /// <summary>Rename only does something with a new, non-empty name.</summary>
    private void RenameText_TextChanged(object sender, TextChangedEventArgs e) =>
        RenameDialog.IsPrimaryButtonEnabled = RenameText.Text.Trim() is { Length: > 0 } name && name != _renaming;

    private async void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item && await IconPicker.ChangeAsync(item))
            await ViewModel.LoadAsync(); // Cards bind the icon once.
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item)
            await ViewModel.DuplicateAsync(item);
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item)
            ViewModel.CreateShortcut(item);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is not { } item)
            return;
        DeleteDialog.Title = Loc.Format("Delete_Title", item.Name);
        DeleteText.Text = Loc.Get("Delete_Body");
        if (await DeleteDialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteAsync(item);
    }

    private async void NewInstance_Click(object sender, RoutedEventArgs e)
    {
        NewInstanceName.Text = "";
        NewInstanceGroup.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<string>(ViewModel.GroupNames); // AOT: a concrete list type.
        NewInstanceGroup.SelectedIndex = -1;
        NewInstanceGroup.Text = "";
        ShowError(null);
        Picker.SelectedOptionIndex = 0;
        await LoadVersionsAsync();
        await NewInstanceDialog.ShowAsync();
    }

    private void NewInstanceVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Picker.MinecraftVersion = (NewInstanceVersion.SelectedItem as VersionManifestEntry)?.Id;
        UpdateNamePlaceholder();
    }

    private async void VersionFilter_Click(object sender, RoutedEventArgs e) => await LoadVersionsAsync();

    private async Task LoadVersionsAsync()
    {
        try
        {
            var list = await ViewModel.GetVersionsAsync(ShowSnapshots.IsChecked == true, ShowOld.IsChecked == true);
            NewInstanceVersion.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Fluorite.Core.Versions.VersionManifestEntry>(list); // AOT: a concrete list type.
            NewInstanceVersion.SelectedIndex = list.Count > 0 ? 0 : -1;
        }
        catch (HttpRequestException ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>The error line takes no space (and adds no StackPanel spacing) until there is something to say.</summary>
    private void ShowError(string? message)
    {
        NewInstanceError.Text = message ?? "";
        NewInstanceError.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void NewInstanceDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (NewInstanceVersion.SelectedItem is not VersionManifestEntry version || Picker.IsIncomplete)
        {
            ShowError(Picker.IsIncomplete ? Loc.Get("Loader_PickVersion") : null);
            args.Cancel = true;
            return;
        }
        var deferral = args.GetDeferral();
        try
        {
            await ViewModel.CreateAsync(NewInstanceName.Text, version.Id, Picker, NewInstanceGroup.Text);
        }
        catch (IOException ex)
        {
            ShowError(ex.Message);
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
