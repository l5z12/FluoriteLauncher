using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Core.Instances;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.Views;

/// <summary>
/// "Change icon": an image of the user's (picked, or dropped on the dialog), one of the built-in icons, or none, which
/// shows the letter tile. Double-clicking a built-in icon saves it straight away.
/// </summary>
public static class IconPicker
{
    /// <summary>Shows the picker and applies what's chosen. True when the icon changed.</summary>
    public static async Task<bool> ChangeAsync(InstanceItem item)
    {
        var registry = App.Get<InstanceRegistry>();
        var instance = item.Instance;
        var (currentIcon, currentKey) = (instance.Manifest.Icon, instance.Manifest.IconKey);
        string? image = null, key = null;
        bool chosen = false, broken = false, quiet = false;

        // The preview: the icon as chosen so far, or the letter tile the lists show without one.
        var letter = new Border
        {
            Background = (Brush)Application.Current.Resources["FluoriteBrandBrush"],
            Child = new TextBlock
            {
                Text = item.Initial, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["FluoriteOnBrandBrush"],
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var picture = new Image { Stretch = Stretch.Uniform };
        var previewGrid = new Grid();
        previewGrid.Children.Add(letter);
        previewGrid.Children.Add(picture);
        var preview = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Child = previewGrid };

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        var browse = new Button { Content = Loc.Get("IconDialog_Browse") };
        var none = new Button { Content = Loc.Get("IconDialog_None") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(browse);
        buttons.Children.Add(none);
        var side = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(buttons);
        side.Children.Add(Dialogs.Text(Loc.Get("IconDialog_Hint"), secondary: true));
        var top = new Grid { ColumnSpacing = 16 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(preview);
        Grid.SetColumn(side, 1);
        top.Children.Add(side);

        var grid = new GridView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 264 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, Loc.Get("IconDialog_BuiltIn"));
        foreach (var k in InstanceItem.BuiltInIcons)
        {
            var name = k.Replace('_', ' ');
            var cell = new GridViewItem
            {
                Tag = k,
                Content = new Image { Width = 40, Height = 40, Source = InstanceItem.LoadIcon(InstanceItem.BuiltInIcon(k)!) },
            };
            ToolTipService.SetToolTip(cell, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, name);
            grid.Items.Add(cell);
        }

        var panel = new StackPanel { Spacing = 12, Width = 480, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), AllowDrop = true };
        panel.Children.Add(top);
        panel.Children.Add(error);
        panel.Children.Add(Dialogs.Header(Loc.Get("IconDialog_BuiltIn")));
        panel.Children.Add(grid);
        // Another launcher's instance: say whether that launcher sees the change.
        if (instance.IsExternal)
        {
            var note = registry.WritesBack(instance) ? "IconDialog_AlsoSaved"
                : instance.Layout == LayoutKind.MultiMC ? "IconDialog_FluoriteOnlyCanWrite" // Only Prism/MultiMC folders can be made writable.
                : "IconDialog_FluoriteOnly";
            panel.Children.Add(Dialogs.Text(Loc.Format(note, item.SourceLabel), secondary: true));
        }

        var dialog = Dialogs.Create(Loc.Get("IconDialog_Title"), panel, Loc.Get("IconDialog_Save"));

        void Show(ImageSource? source, string? message = null)
        {
            broken = message is not null;
            picture.Source = source;
            letter.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
            error.Text = message ?? "";
            error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = chosen && !broken && (image is not null || key != currentKey || (key is null && currentIcon is not null));
        }

        void Choose(string? newImage, string? newKey)
        {
            (image, key, chosen) = (newImage, newKey, true);
            quiet = true;
            if (newKey is null)
                grid.SelectedItem = null;
            quiet = false;
            Show(newImage is not null ? InstanceItem.LoadIcon(new Uri(newImage))
                : newKey is not null ? InstanceItem.LoadIcon(InstanceItem.BuiltInIcon(newKey)!)
                : null);
        }

        // A file that can't be used is only mentioned: what was chosen before stays chosen.
        void Warn(string message)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
        }

        void ChooseFile(string path)
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn(ex.Message);
                return;
            }
            if (!InstanceIcons.IsImage(path))
                Warn(Loc.Get("IconDialog_NotImage"));
            else if (size > InstanceIcons.MaxBytes)
                Warn(Loc.Get("IconDialog_TooLarge"));
            else
                Choose(path, null);
        }

        picture.ImageFailed += (_, _) =>
        {
            if (image is not null)
                Show(null, Loc.Get("IconDialog_Unreadable"));
        };
        grid.SelectionChanged += (_, _) =>
        {
            if (!quiet && grid.SelectedItem is GridViewItem { Tag: string k })
                Choose(null, k);
        };
        none.Click += (_, _) => Choose(null, null);
        browse.Click += async (_, _) =>
        {
            if (App.Window is not { } window)
                return;
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            foreach (var ext in InstanceIcons.Extensions)
                picker.FileTypeFilter.Add(ext);
            if ((await picker.PickSingleFileAsync())?.Path is { } path)
                ChooseFile(path);
        };
        panel.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = Loc.Get("IconDialog_DropCaption");
            }
        };
        panel.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<Windows.Storage.StorageFile>().FirstOrDefault() is { } file)
                ChooseFile(file.Path);
        };
        var commit = false;
        grid.DoubleTapped += (_, _) =>
        {
            if (dialog.IsPrimaryButtonEnabled)
            {
                commit = true;
                dialog.Hide();
            }
        };

        // Opens on the icon it has now, its built-in one selected.
        quiet = true;
        grid.SelectedItem = grid.Items.OfType<GridViewItem>().FirstOrDefault(c => currentIcon is null && (string)c.Tag == currentKey);
        quiet = false;
        Show(item.Icon);
        dialog.Opened += (_, _) =>
        {
            if (grid.SelectedItem is { } selected)
                grid.ScrollIntoView(selected);
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary && !commit)
            return false;
        try
        {
            await registry.SetIconAsync(instance, image, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("IconDialog_Failed"), ex.Message);
            return false;
        }
        item.RefreshIcon();
        return true;
    }
}
