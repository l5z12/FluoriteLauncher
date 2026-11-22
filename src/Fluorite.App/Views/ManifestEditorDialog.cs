using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Modding.Metadata;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.Views;

/// <summary>
/// "Edit manifest": the mod's own fabric.mod.json / mods.toml… as text, checked as it's typed, with one-click
/// "Allow Minecraft 1.21.4" (and the loader) for a mod that declares stricter versions than it needs. Saving writes
/// it into the jar; the original is kept and "Restore original" puts it back.
/// </summary>
public static class ManifestEditorDialog
{
    public static async Task ShowAsync(ContentItemViewModel item, ContentListViewModel list)
    {
        var path = item.File.Path;
        IReadOnlyList<string> entries;
        bool signed;
        try
        {
            (entries, signed) = await Task.Run(() => (ModManifest.Find(path), ModManifest.IsSigned(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Manifest_Title"), ex.Message);
            return;
        }
        if (entries.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Manifest_Title"), Loc.Get("Manifest_None"));
            return;
        }

        var preferred = list.InstanceLoaderKind is { } kind ? ModManifest.EntryFor(kind) : null;
        var entry = entries.FirstOrDefault(e => string.Equals(e, preferred, StringComparison.OrdinalIgnoreCase)) ?? entries[0];
        var loaded = "";

        var picker = new ComboBox { ItemsSource = entries.ToList(), SelectedItem = entry, MinWidth = 260, Visibility = entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, Loc.Get("Manifest_FilePicker"));
        var fixes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(picker);
        top.Children.Add(fixes);

        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Height = 380,
            Width = 700, // The dialog's content is at most 712 wide.
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor, Loc.Get("Manifest_Title"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };

        var panel = new StackPanel { Spacing = 12 };
        if (item.Issue is { } issue)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = item.IssueIsError ? InfoBarSeverity.Error : InfoBarSeverity.Warning, Message = issue });
        panel.Children.Add(top);
        panel.Children.Add(editor);
        panel.Children.Add(error);
        panel.Children.Add(Dialogs.Text(Loc.Get(signed ? "Manifest_NoteSigned" : "Manifest_Note"), secondary: true));

        var hasOriginal = list.HasOriginal(item);
        var dialog = Dialogs.Create(Loc.Format("Manifest_TitleFor", item.Name), panel, Loc.Get("Manifest_Save"), secondary: hasOriginal ? Loc.Get("Manifest_Restore") : null);

        // Live: the error under the text, the Allow buttons that still change something, Save only for a valid change.
        void Update()
        {
            var text = Text();
            var problem = ModManifest.Validate(entry, text);
            error.Text = problem ?? "";
            error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            var dirty = text != loaded;
            dialog.IsPrimaryButtonEnabled = dirty && problem is null;
            picker.IsEnabled = !dirty; // Switching files would drop the edit.
            fixes.Children.Clear();
            if (problem is not null)
                return;
            foreach (var (id, label, version) in list.InstanceTargets)
            {
                if (ModManifest.AllowVersion(entry, text, id, version) is not { } widened)
                    continue;
                var fix = new Button { Content = Loc.Format("Manifest_Allow", label, version) };
                ToolTipService.SetToolTip(fix, Loc.Get("Manifest_AllowHint"));
                fix.Click += (_, _) => editor.Text = widened;
                fixes.Children.Add(fix);
            }
        }

        async Task LoadAsync()
        {
            try
            {
                var name = entry;
                loaded = await Task.Run(() => ModManifest.Read(path, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                loaded = "";
                error.Text = ex.Message;
            }
            loaded = loaded.ReplaceLineEndings("\n");
            editor.Text = loaded;
            Update();
        }

        // The box keeps line breaks as \r; checks, edits and the jar use \n (so an untouched file isn't a change).
        string Text() => editor.Text.ReplaceLineEndings("\n");

        editor.TextChanged += (_, _) => Update();
        picker.SelectionChanged += async (_, _) =>
        {
            if (picker.SelectedItem is string chosen && chosen != entry)
            {
                entry = chosen;
                await LoadAsync();
            }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (await list.EditManifestAsync(item, entry, Text()) is { } failed)
                {
                    error.Text = failed;
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (await list.RestoreManifestAsync(item) is { } failed)
                {
                    error.Text = failed;
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
        dialog.Opened += (_, _) => editor.Focus(FocusState.Programmatic);

        await LoadAsync();
        await Dialogs.ShowAsync(dialog);
    }
}
