using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Fluorite.App.Views;

/// <summary>Screenshot gallery: click to preview, right-click for copy/share/delete (applies to the selection).</summary>
public sealed partial class ScreenshotsPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ScreenshotsViewModel), typeof(ScreenshotsPanel),
        new PropertyMetadata(null, (d, _) => ((ScreenshotsPanel)d).Bindings?.Update()));

    public ScreenshotsPanel()
    {
        InitializeComponent();
        ItemKeys.FocusOnRightPress(Gallery);
        ItemKeys.AddRefreshKey(this, () => ViewModel?.RefreshCommand);
    }

    public ScreenshotsViewModel? ViewModel
    {
        get => (ScreenshotsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>The selection when the clicked item is part of it, otherwise just that item.</summary>
    private List<ScreenshotItemViewModel> Targets(object sender)
    {
        var selected = Gallery.SelectedItems.OfType<ScreenshotItemViewModel>().ToList();
        var item = Bind.ItemOf<ScreenshotItemViewModel>(sender);
        return item is null ? selected : selected.Contains(item) ? selected : [item];
    }

    /// <summary>Only tiles on (or near) the screen hold a thumbnail: recycled tiles give theirs back.</summary>
    private void Gallery_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ScreenshotItemViewModel item)
            return;
        if (args.InRecycleQueue)
            item.ReleaseThumbnail();
        else
            _ = item.LoadThumbnailAsync();
    }

    private async void Gallery_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ScreenshotItemViewModel item)
            await PreviewAsync(item);
    }

    private async void Gallery_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var selected = Gallery.SelectedItems.OfType<ScreenshotItemViewModel>().ToList();
        var chord = ItemKeys.Chord(e);
        switch (chord)
        {
            case ItemKeys.Delete or ItemKeys.DeletePermanently when selected.Count > 0:
                e.Handled = true;
                await vm.DeleteAsync(selected, permanent: chord == ItemKeys.DeletePermanently);
                break;
            case ItemKeys.Copy when selected.Count > 0:
                e.Handled = true;
                await vm.CopyAsync(selected);
                break;
            case ItemKeys.ShowInFolder when ItemKeys.FocusedRow(Gallery) is { } tile:
                e.Handled = true;
                ShowInFolder_Click(tile, e);
                break;
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.CopyAsync(Targets(sender));
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.ShareAsync(Targets(sender));
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ScreenshotItemViewModel>(sender) is { } item)
            ScreenshotsViewModel.Open(item);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ScreenshotItemViewModel>(sender) is { } item)
            ScreenshotsViewModel.ShowInFolder(item);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.DeleteAsync(Targets(sender), permanent: ShiftDelete.ForGood(sender));
    }

    /// <summary>A large view with previous/next and the same actions.</summary>
    private async Task PreviewAsync(ScreenshotItemViewModel start)
    {
        if (ViewModel is not { } vm)
            return;
        var current = start;
        var image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxHeight = 560 };
        var caption = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var prev = IconButton("", "Screenshots_Previous");
        var next = IconButton("", "Screenshots_Next");
        var copy = IconButton("", "Screenshots_CopyItem/Text");
        var share = IconButton("", "Screenshots_ShareItem/Text");
        var open = IconButton("", "Screenshots_OpenItem/Text");
        var delete = IconButton("", "Content_Delete/Text");

        var bar = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var b in new[] { copy, share, open, delete })
            actions.Children.Add(b);
        Grid.SetColumn(next, 1);
        Grid.SetColumn(caption, 2);
        Grid.SetColumn(actions, 3);
        caption.Margin = new Thickness(8, 0, 8, 0);
        bar.Children.Add(prev);
        bar.Children.Add(next);
        bar.Children.Add(caption);
        bar.Children.Add(actions);

        var panel = new StackPanel { Width = 1000 };
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(6), Child = image, MinHeight = 200 });
        panel.Children.Add(bar);

        var dialog = Dialogs.Create(start.FileName, panel, close: Loc.Get("Common_Close"));
        dialog.Resources["ContentDialogMaxWidth"] = 1100d;

        async Task ShowAsync(ScreenshotItemViewModel item)
        {
            current = item;
            dialog.Title = item.FileName;
            caption.Text = item.Taken;
            var index = vm.Items.IndexOf(item);
            prev.IsEnabled = index > 0;
            next.IsEnabled = index >= 0 && index < vm.Items.Count - 1;
            var loaded = await ImageLoader.LoadAsync(item.Shot.Path, 1000);
            if (current != item)
            {
                ImageLoader.Release(loaded); // Moved on while it decoded.
                return;
            }
            var previous = image.Source;
            image.Source = loaded;
            ImageLoader.Release(previous);
        }

        prev.Click += async (_, _) => { if (vm.Items.IndexOf(current) is var i and > 0) await ShowAsync(vm.Items[i - 1]); };
        next.Click += async (_, _) => { if (vm.Items.IndexOf(current) is var i && i >= 0 && i < vm.Items.Count - 1) await ShowAsync(vm.Items[i + 1]); };
        copy.Click += async (_, _) => await vm.CopyAsync([current]);
        share.Click += async (_, _) => await vm.ShareAsync([current]);
        open.Click += (_, _) => ScreenshotsViewModel.Open(current);
        delete.Click += async (_, _) =>
        {
            var permanent = ItemKeys.IsShiftDown;
            dialog.Hide();
            await vm.DeleteAsync([current], permanent);
        };
        dialog.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Left && prev.IsEnabled)
                await ShowAsync(vm.Items[vm.Items.IndexOf(current) - 1]);
            else if (e.Key == Windows.System.VirtualKey.Right && next.IsEnabled)
                await ShowAsync(vm.Items[vm.Items.IndexOf(current) + 1]);
        };
        await ShowAsync(start);
        await Dialogs.ShowAsync(dialog);
        var shown = image.Source;
        image.Source = null;
        ImageLoader.Release(shown);
    }

    private static Button IconButton(string glyph, string nameKey)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 } };
        var name = Loc.Get(nameKey);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        ToolTipService.SetToolTip(b, name);
        return b;
    }
}
