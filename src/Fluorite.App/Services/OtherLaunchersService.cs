using Fluorite.Core.Instances;
using Fluorite.Core.Settings;
using Fluorite.Modding.Loaders;
using Fluorite.Modding.Modpacks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Services;

/// <summary>
/// Other launchers: folders used in place (Prism/MultiMC, official launcher, HMCL/PCL — saved as
/// <see cref="ExternalRoot"/>s) and instances copied in from launchers whose folders can't be used in place.
/// </summary>
public sealed class OtherLaunchersService(SettingsService settings, FluoriteLayout layout, LoaderService loaders, InstanceRegistry registry)
{
    /// <summary>Raised after roots were added, removed or changed.</summary>
    public event EventHandler? RootsChanged;

    /// <summary>Raised after an instance was copied into Fluorite (the Library opens it).</summary>
    public event EventHandler<GameInstance>? InstanceImported;

    /// <summary>Raised after several instances were copied into Fluorite at once.</summary>
    public event EventHandler? InstancesImported;

    public IReadOnlyList<ExternalRoot> Roots => settings.Current.ExternalRoots;

    public bool IsAdded(string path) => FindRoot(path) is not null;

    private ExternalRoot? FindRoot(string path) =>
        Roots.FirstOrDefault(r => string.Equals(Path.GetFullPath(r.Path).TrimEnd('\\'), Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    public ExternalRoot Add(LayoutKind kind, OtherLauncher launcher, string path)
    {
        var root = new ExternalRoot
        {
            Id = InstanceRegistry.ProposeId(launcher, Roots.Select(r => r.Id)),
            Path = Path.GetFullPath(path),
            Kind = kind,
            Launcher = launcher,
        };
        settings.Current.ExternalRoots.Add(root);
        Save();
        return root;
    }

    public void Remove(string id)
    {
        settings.Current.ExternalRoots.RemoveAll(r => r.Id == id);
        Save();
    }

    public void Save()
    {
        settings.Save();
        RootsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folder picker → recognise the launcher → add it.</summary>
    public async Task<ExternalRoot?> AddFolderAsync()
    {
        if (App.Window is not { } window)
            return null;
        var picked = await new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id).PickSingleFolderAsync();
        if (picked?.Path is not { } path)
            return null;
        if (InstanceRegistry.Detect(path) is not { } detected)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Launchers_NotRecognizedTitle"), Loc.Format("Launchers_NotRecognized", path));
            return null;
        }
        if (IsAdded(detected.Path))
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Launchers_AlreadyAddedTitle"), Loc.Format("Launchers_AlreadyAdded", detected.Path));
            return null;
        }
        return Add(detected.Kind, detected.Launcher, detected.Path);
    }

    /// <summary>
    /// Everything found on this PC. Launchers whose folders can be used in place (Prism/MultiMC, official, HMCL/PCL)
    /// offer both: Add (the Library shows their instances where they are) or Copy (pick instances to copy into
    /// Fluorite's own folder). The others' instances can only be copied in.
    /// </summary>
    public async Task ShowImportDialogAsync()
    {
        var detected = await Task.Run(LauncherDetection.Detect);
        var candidates = await Task.Run(() => detected.Where(d => d.Layout is null)
            .SelectMany(d => InstanceImporter.Scan(d.Launcher, d.Path)).ToList());
        while (true)
        {
            switch (await ShowLaunchersAsync(detected, candidates))
            {
                case { AddFolder: true }:
                    await AddFolderAsync();
                    return;
                case { Candidate: { } candidate }:
                    await ImportAsync(candidate);
                    return;
                case { CopyFrom: { } launcher }:
                    if (await PickToCopyAsync(launcher) is not { } picked)
                        continue; // Back to the launchers.
                    await CopyAsync(picked);
                    return;
                default:
                    return;
            }
        }
    }

    private sealed record Choice(bool AddFolder = false, ImportCandidate? Candidate = null, DetectedLauncher? CopyFrom = null);

    /// <summary>The launchers found on this PC; returns what was picked, or null when closed.</summary>
    private async Task<Choice?> ShowLaunchersAsync(IReadOnlyList<DetectedLauncher> detected, List<ImportCandidate> candidates)
    {
        Choice? choice = null;
        var panel = new StackPanel { Spacing = 8, MinWidth = 600 };
        var dialog = Dialogs.Create(Loc.Get("Launchers_DialogTitle"), Dialogs.Scroll(panel, 560), close: Loc.Get("Common_Close"));

        panel.Children.Add(Dialogs.Header(Loc.Get("Launchers_InPlaceHeader")));
        panel.Children.Add(Dialogs.Text(Loc.Get("Launchers_InPlaceHint"), secondary: true));
        var inPlace = detected.Where(d => d.Layout is not null).ToList();
        if (inPlace.Count == 0)
            panel.Children.Add(Dialogs.Text(Loc.Get("Launchers_NoneFound"), secondary: true));
        foreach (var d in inPlace)
        {
            var added = IsAdded(d.Path);
            var add = new Button { Content = Loc.Get(added ? "Launchers_Added" : "Launchers_Add"), IsEnabled = !added, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            add.Click += (_, _) =>
            {
                Add(d.Layout!.Value, d.Launcher, d.Path);
                add.Content = Loc.Get("Launchers_Added");
                add.IsEnabled = false;
            };

            var copy = new Button { Content = Loc.Get("Launchers_CopyFrom") };
            copy.Click += (_, _) =>
            {
                choice = new Choice(CopyFrom: d);
                dialog.Hide();
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(copy);
            actions.Children.Add(add);
            panel.Children.Add(Row(Loc.Get("Launcher_" + d.Launcher), $"{d.Path} · {Count(d)}", actions));
        }

        panel.Children.Add(Dialogs.Header(Loc.Get("Launchers_CopyHeader")));
        panel.Children.Add(Dialogs.Text(Loc.Get("Launchers_CopyHint"), secondary: true));
        if (candidates.Count == 0)
            panel.Children.Add(Dialogs.Text(Loc.Get("Launchers_NoneFound"), secondary: true));
        foreach (var c in candidates)
        {
            var button = new Button { Content = Loc.Get("Launchers_Copy"), VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) =>
            {
                choice = new Choice(Candidate: c);
                dialog.Hide();
            };
            var version = c.MinecraftVersion is null ? Loc.Get("Launchers_UnknownVersion")
                : c.LoaderUid is null ? c.MinecraftVersion : $"{c.MinecraftVersion} · {ViewModels.InstanceItem.LoaderName(c.LoaderUid)} {c.LoaderVersion}";
            panel.Children.Add(Row(c.Name, $"{Loc.Get("Launcher_" + c.Launcher)} · {version}", button));
        }

        var other = new HyperlinkButton { Content = Loc.Get("Launchers_AddFolder"), Margin = new Thickness(0, 8, 0, 0) };
        other.Click += (_, _) =>
        {
            choice = new Choice(AddFolder: true);
            dialog.Hide();
        };
        panel.Children.Add(other);

        await Dialogs.ShowAsync(dialog);
        return choice;
    }

    /// <summary>The copy screen for one launcher folder: its instances, with check boxes. Null when the user goes back.</summary>
    private async Task<IReadOnlyList<GameInstance>?> PickToCopyAsync(DetectedLauncher d)
    {
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) });
        var panel = new StackPanel { Spacing = 8, MinWidth = 600 };
        panel.Children.Add(Dialogs.Text(Loc.Format("Launchers_CopyFromHint", d.Path), secondary: true));
        panel.Children.Add(list);
        var dialog = Dialogs.Create(Loc.Format("Launchers_CopyFromTitle", Loc.Get("Launcher_" + d.Launcher)), Dialogs.Scroll(panel, 520),
            primary: Loc.Get("Launchers_Copy"), close: Loc.Get("Launchers_Back"));
        dialog.IsPrimaryButtonEnabled = false;

        var boxes = new List<(CheckBox Box, GameInstance Instance)>();
        void Update()
        {
            var n = boxes.Count(b => b.Box.IsChecked == true);
            dialog.IsPrimaryButtonEnabled = n > 0;
            dialog.PrimaryButtonText = n > 1 ? Loc.Format("Launchers_CopyCount", n) : Loc.Get("Launchers_Copy");
        }
        dialog.Opened += async (_, _) =>
        {
            var (instances, error) = await ListAsync(d);
            list.Children.Clear();
            if (error is not null || instances.Count == 0)
                list.Children.Add(Dialogs.Text(error ?? Loc.Get("Launchers_NoneFound"), secondary: true));
            foreach (var instance in instances)
            {
                var box = new CheckBox
                {
                    Content = Lines(instance.Name, ViewModels.InstanceItem.DescribeVersion(instance.Manifest, instance.LaunchVersionId)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                box.Checked += (_, _) => Update();
                box.Unchecked += (_, _) => Update();
                boxes.Add((box, instance));
                list.Children.Add(box);
            }
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;
        return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Instance).ToList();
    }

    /// <summary>A launcher folder's instances, through its root when it's already added (so Fluorite-side edits show).</summary>
    private async Task<(IReadOnlyList<GameInstance> Instances, string? Error)> ListAsync(DetectedLauncher d)
    {
        try
        {
            var source = FindRoot(d.Path) is { } root && registry.LayoutFor(root.Id) is { } added ? added : registry.Browse(d.Layout!.Value, d.Launcher, d.Path);
            var instances = await Task.Run(() => source.GetInstancesAsync());
            return (instances.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>
    /// Copies instances of a Prism/official/HMCL folder into Fluorite's own (loaders are reinstalled there). A single
    /// copy opens afterwards; several just appear in the Library (as do the ones done before a failure or Cancel).
    /// </summary>
    public async Task CopyAsync(IReadOnlyList<GameInstance> sources)
    {
        if (sources.Count == 0)
            return;
        var copied = new List<GameInstance>();
        try
        {
            await Dialogs.BusyAsync(Loc.Get("Launchers_CopyingTitle"),
                sources.Count == 1 ? Loc.Format("Launchers_Copying", sources[0].Name) : Loc.Format("Launchers_CopyingMany", sources.Count),
                ct => Task.Run(async () =>
                {
                    foreach (var source in sources)
                        copied.Add(await InstanceImporter.CopyAsync(layout, source, ct: ct).ConfigureAwait(false));
                    return copied.Count;
                }, ct));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Launchers_CopyFailedTitle"), ex.Message);
        }
        if (sources.Count == 1 && copied.Count == 1)
            InstanceImported?.Invoke(this, copied[0]);
        else if (copied.Count > 0)
            InstancesImported?.Invoke(this, EventArgs.Empty);
    }

    private static string Count(DetectedLauncher d) =>
        d.Layout is LayoutKind.Official or LayoutKind.Isolated ? Loc.Format("Detected_Versions", d.InstanceCount) : Loc.Format("Detected_Instances", d.InstanceCount);

    private static Grid Row(string title, string detail, FrameworkElement action)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Lines(title, detail));
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return grid;
    }

    /// <summary>A name over a secondary detail line.</summary>
    private static StackPanel Lines(string title, string detail)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        return text;
    }

    /// <summary>Copies another launcher's instance in; asks for the version when its metadata didn't say.</summary>
    public async Task<GameInstance?> ImportAsync(ImportCandidate c)
    {
        var mc = c.MinecraftVersion;
        var loaderUid = c.LoaderUid;
        var loaderVersion = c.LoaderVersion;
        if (mc is null || (loaderUid is not null && loaderVersion is null))
        {
            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(Dialogs.Text(Loc.Format("Launchers_NeedVersion", c.Name), secondary: true));
            var mcBox = new TextBox { Header = Loc.Get("Launchers_MinecraftVersion"), Text = mc ?? "", PlaceholderText = "1.21.1" };
            var loaderBox = new ComboBox { Header = Loc.Get("Launchers_Loader"), HorizontalAlignment = HorizontalAlignment.Stretch };
            loaderBox.Items.Add(Loc.Get("Loader_None"));
            foreach (var p in loaders.Providers)
                loaderBox.Items.Add(p.DisplayName);
            loaderBox.SelectedIndex = loaderUid is null ? 0 : loaders.Providers.ToList().FindIndex(p => p.Uid == loaderUid) + 1;
            panel.Children.Add(mcBox);
            panel.Children.Add(loaderBox);
            if (await Dialogs.ShowAsync(Loc.Get("Launchers_NeedVersionTitle"), panel, Loc.Get("Launchers_Copy")) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(mcBox.Text))
                return null;
            mc = mcBox.Text.Trim();
            loaderUid = loaderBox.SelectedIndex > 0 ? loaders.Providers[loaderBox.SelectedIndex - 1].Uid : null;
            loaderVersion = null;
        }

        try
        {
            var instance = await Dialogs.BusyAsync(Loc.Get("Launchers_CopyingTitle"), Loc.Format("Launchers_Copying", c.Name), async ct =>
            {
                var version = loaderVersion;
                if (loaderUid is not null && version is null)
                {
                    var list = await loaders.Get(loaderUid).GetVersionsAsync(mc, ct);
                    version = (list.FirstOrDefault(v => v.Recommended) ?? (list.Count > 0 ? list[0] : null))?.Version
                        ?? throw new InvalidOperationException(Loc.Format("Loader_NotAvailable", ViewModels.InstanceItem.LoaderName(loaderUid), mc));
                }
                return await Task.Run(() => InstanceImporter.ImportAsync(layout, c, mc, loaderUid, version, ct: ct), ct);
            });
            InstanceImported?.Invoke(this, instance);
            return instance;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Launchers_CopyFailedTitle"), ex.Message);
            return null;
        }
    }
}
