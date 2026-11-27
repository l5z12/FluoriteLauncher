using Fluorite.Core;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Content;
using Fluorite.Modding.Modpacks;
using Fluorite.Modding.Platforms;
using Fluorite.Platform.Windows.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Services;

/// <summary>
/// UI flows for modpacks: install from a file, link or Browse (confirm → install with progress in Activity → manual
/// downloads if any), check for and review updates, and export.
/// </summary>
public sealed class ModpackInstallService(
    ModpackService packs, ContentService content, ContentInstallService jobs, FluoriteLayout layout, InstanceRegistry registry, HttpClient http,
    SettingsService settings)
{
    /// <summary>Raised on the UI thread after a pack was installed into a new instance.</summary>
    public event EventHandler<GameInstance>? InstanceCreated;

    /// <summary>A modpack file by its name: a .mrpack, or a .zip (which may still turn out to be something else).</summary>
    public static bool HasModpackExtension(string path) =>
        path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dropped files split into modpacks (a .mrpack, or a zip that holds a pack) and the rest, for the panels that take
    /// zips of their own (worlds, resource packs): a pack dropped there installs instead of being added as content.
    /// </summary>
    public static Task<(List<string> Packs, List<string> Others)> SplitModpacksAsync(IEnumerable<string> paths) => Task.Run(() =>
    {
        var packs = new List<string>();
        var others = new List<string>();
        foreach (var path in paths)
        {
            var isPack = path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                || (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && ModpackReader.IsModpack(path));
            (isPack ? packs : others).Add(path);
        }
        return (packs, others);
    });

    private static string NewWorkDir() => Path.Combine(AppPaths.DataRoot, "cache", "packs", Guid.NewGuid().ToString("N")[..8]);

    public Task<GameInstance?> InstallFromFileAsync(string path) =>
        InstallAsync(Path.GetFileName(path), (_, _, ct) => packs.LoadFileAsync(path, ct));

    public Task<GameInstance?> InstallFromLocationAsync(string location) =>
        InstallAsync(location, (work, progress, ct) => packs.LoadLocationAsync(location, work, progress, ct));

    /// <param name="version">Exact version, or null for the newest release.</param>
    public Task<GameInstance?> InstallProjectAsync(RemoteProject project, RemoteVersion? version) =>
        InstallAsync(project.Title, async (work, progress, ct) =>
        {
            var v = version ?? await packs.LatestVersionAsync(project.Platform, project.Id, ct: ct);
            var m = await packs.LoadVersionAsync(v, work, progress, ct);
            m.IconUrl ??= project.IconUrl;
            m.Summary ??= project.Summary;
            return m;
        });

    private async Task<GameInstance?> InstallAsync(string what, Func<string, IProgress<DownloadProgress>, CancellationToken, Task<PackManifest>> load)
    {
        var work = NewWorkDir();
        var groups = Task.Run(GroupNamesAsync); // For the dialog's group box; read while the pack is.
        try
        {
            PackManifest manifest;
            try
            {
                manifest = await Dialogs.BusyAsync(Loc.Get("Modpack_ReadingTitle"), Loc.Format("Modpack_Reading", what),
                    (progress, ct) => Task.Run(() => load(work, progress, ct), ct));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                await Dialogs.ShowMessageAsync(Loc.Get("Modpack_ReadFailedTitle"), ex.Message);
                return null;
            }

            var choice = await ConfirmAsync(manifest, await groups);
            if (choice is not { } c)
                return null;

            var instance = await ModpackInstaller.CreateInstanceAsync(layout, manifest, c.Name, c.Group);
            await TryDownloadIconAsync(instance, manifest.IconUrl);
            var job = jobs.Start(manifest.Name, instance, null);
            job.Status = Loc.Get("Modpack_Installing");
            try
            {
                var manager = content.For(instance);
                var result = await Task.Run(() => packs.Installer.InstallAsync(manifest, instance, manager, c.IncludeOptional, Progress(job)));
                await ManualAsync(manager, result.Blocked);
                ContentInstallService.Finish(job, Loc.Get("Modpack_Installed"));
                InstanceCreated?.Invoke(this, instance);
                return instance;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                ContentInstallService.Finish(job, ex.Message, failed: true);
                TryRemove(instance.Directory); // Don't leave a half-installed instance in the Library.
                await Dialogs.ShowMessageAsync(Loc.Get("Modpack_InstallFailedTitle"), ex.Message);
                return null;
            }
        }
        finally
        {
            TryRemove(work);
        }
    }

    private sealed record InstallChoice(string Name, string? Group, bool IncludeOptional);

    private static async Task<InstallChoice?> ConfirmAsync(PackManifest m, IReadOnlyList<string> groups)
    {
        var panel = new Views.ModpackConfirmPanel(m, groups);
        var dialog = Dialogs.Create(Loc.Get("Modpack_InstallTitle"), Dialogs.Scroll(panel, 560), Loc.Get("Content_Install"));
        dialog.Opened += (_, _) => panel.FocusName();
        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? new InstallChoice(panel.InstanceName, panel.Group, panel.IncludeOptional) : null;
    }

    /// <summary>
    /// Every Library group, A–Z, as New instance and "Move to group" offer them: those in use and the empty ones made
    /// with "New group…". Read here rather than from the Library, which may not have loaded (installing from Browse).
    /// </summary>
    private async Task<IReadOnlyList<string>> GroupNamesAsync()
    {
        IEnumerable<string?> inUse;
        try
        {
            inUse = (await registry.GetInstancesAsync()).Select(i => i.Manifest.Group);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            inUse = [];
        }
        return inUse.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g!.Trim())
            .Concat(settings.Current.CreatedGroups)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ── Updates ───────────────────────────────────────────────────────────

    public static PackState? StateOf(GameInstance instance) => PackState.Load(instance.MetadataDirectory);

    /// <summary>Checks for a newer version of the instance's pack; shows what would change and applies it if confirmed.</summary>
    public async Task<bool> UpdateAsync(GameInstance instance)
    {
        if (StateOf(instance) is not { } state)
            return false;
        var work = NewWorkDir();
        try
        {
            PackUpdatePlan? plan;
            PackUpdateInfo? info;
            try
            {
                (info, plan) = await Dialogs.BusyAsync(Loc.Get("Modpack_CheckingTitle"), Loc.Format("Modpack_Checking", state.Name), async (progress, ct) =>
                {
                    var found = await packs.CheckUpdateAsync(state, ct);
                    if (found is null)
                        return ((PackUpdateInfo?)null, (PackUpdatePlan?)null);
                    var manifest = await packs.LoadUpdateAsync(state, found, work, progress, ct);
                    return (found, await ModpackInstaller.PlanUpdateAsync(instance, manifest, ct: ct));
                });
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                await Dialogs.ShowMessageAsync(Loc.Get("Modpack_UpdateFailedTitle"), ex.Message);
                return false;
            }
            if (plan is null || info is null)
            {
                await Dialogs.ShowMessageAsync(Loc.Get("Modpack_UpToDateTitle"), Loc.Format("Modpack_UpToDate", state.Name, state.Version ?? ""));
                return false;
            }
            if (!await ReviewAsync(state, info, plan))
                return false;

            var job = jobs.Start(Loc.Format("Modpack_UpdatingTo", state.Name, info.VersionName), instance, null);
            try
            {
                job.Status = Loc.Get("Snapshot_Taking");
                await App.Get<SnapshotService>().TakeBeforeUpdateAsync(instance, Loc.Format("Snapshot_BeforePackUpdate", state.Version ?? "", info.VersionName));
                var manager = content.For(instance);
                var result = await Task.Run(() => packs.Installer.ApplyUpdateAsync(plan, instance, manager, i => registry.SaveAsync(i), p => RecycleBin.Send(p), Progress(job)));
                await ManualAsync(manager, result.Blocked);
                ContentInstallService.Finish(job, Loc.Get("Modpack_Updated"));
                jobs.NotifyChanged(instance);
                return true;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                ContentInstallService.Finish(job, ex.Message, failed: true);
                await Dialogs.ShowMessageAsync(Loc.Get("Modpack_UpdateFailedTitle"), ex.Message);
                return false;
            }
        }
        finally
        {
            TryRemove(work);
        }
    }

    private static async Task<bool> ReviewAsync(PackState state, PackUpdateInfo info, PackUpdatePlan plan)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 560 };
        panel.Children.Add(Dialogs.Text($"{state.Name}: {state.Version} → {info.VersionName}"));
        if (plan.NewMinecraft is { } mc)
            panel.Children.Add(Dialogs.Text(Loc.Format("Modpack_MinecraftChange", mc), secondary: true));
        if (plan.NewLoader is { Uid: { } uid } loader)
            panel.Children.Add(Dialogs.Text(Loc.Format("Modpack_LoaderChange", ViewModels.InstanceItem.LoaderName(uid), loader.Version ?? ""), secondary: true));

        int Count(PackChangeKind k) => plan.Changes.Count(c => c.Kind == k);
        panel.Children.Add(Dialogs.Text(Loc.Format("Modpack_ChangeSummary", Count(PackChangeKind.Add), Count(PackChangeKind.Replace), Count(PackChangeKind.Remove), plan.Unchanged)));

        var yours = plan.Changes.Where(c => c.Kind is PackChangeKind.Modified or PackChangeKind.Conflict).ToList();
        if (yours.Count > 0)
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("Modpack_YourChanges")));
            panel.Children.Add(Dialogs.Text(Loc.Get("Modpack_YourChangesHint"), secondary: true));
            foreach (var c in yours)
            {
                var box = new CheckBox { Content = c.Path, IsChecked = false, Tag = c };
                box.Checked += (_, _) => c.Apply = true;
                box.Unchecked += (_, _) => c.Apply = false;
                ToolTipService.SetToolTip(box, c.Kind == PackChangeKind.Modified
                    ? (c.Entry is null ? Loc.Get("Modpack_ModifiedRemoved") : Loc.Get("Modpack_ModifiedChanged"))
                    : Loc.Get("Modpack_Conflict"));
                panel.Children.Add(box);
            }
        }

        var other = plan.Changes.Where(c => c.Kind is PackChangeKind.Add or PackChangeKind.Replace or PackChangeKind.Remove).ToList();
        if (other.Count > 0)
        {
            var details = new Expander { Header = Loc.Get("Modpack_AllChanges"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            details.Content = Dialogs.Bullets(other.Take(200).Select(c => $"{KindLabel(c.Kind)}  {c.Path}").Concat(other.Count > 200 ? [Loc.Format("Modpack_AndMore", other.Count - 200)] : []));
            panel.Children.Add(details);
        }

        var result = await Dialogs.ShowAsync(Loc.Get("Modpack_UpdateTitle"), Dialogs.Scroll(panel, 540), Loc.Get("Modpack_UpdateButton"));
        return result == ContentDialogResult.Primary;
    }

    private static string KindLabel(PackChangeKind k) => k switch
    {
        PackChangeKind.Add => Loc.Get("Modpack_KindAdd"),
        PackChangeKind.Replace => Loc.Get("Modpack_KindReplace"),
        PackChangeKind.Remove => Loc.Get("Modpack_KindRemove"),
        _ => k.ToString(),
    };

    // ── Export ────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks how (format, details, what goes in), starting from the instance's last export; then where, and exports with
    /// progress in Activity. The choices are kept for next time once Export is pressed.
    /// </summary>
    public async Task ExportAsync(GameInstance instance)
    {
        if (App.Window is not { } window)
            return;
        var saved = PackExportState.Load(instance.MetadataDirectory);
        var panel = new Views.ExportPanel(instance, saved, StateOf(instance), settings.Current.LastExportFormat, settings.Current.LastExportAuthor,
            running: App.Get<GameRunner>().Find(instance) is not null, curseForge: content.Has(ContentPlatform.CurseForge), localOnly: settings.Current.ExportLocalOnly);
        var dialog = Dialogs.Create(Loc.Get("Export_DialogTitle"), Dialogs.Scroll(panel, 780), Loc.Get("Export_Button"));
        dialog.Resources["ContentDialogMaxWidth"] = 1000d; // The list beside the details (the panel narrows with the window).
        dialog.Resources["ContentDialogMaxHeight"] = 980d; // Room for Advanced opened; a short window still scrolls.
        dialog.Opened += (_, _) => panel.FocusFirst();
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
            return;

        var format = panel.Format;
        var state = panel.ToState();
        settings.Current.LastExportFormat = panel.FormatIndex;
        if (panel.UsesAuthor) // Emptied too, so it can be taken back; a format without one leaves it as it was.
            settings.Current.LastExportAuthor = panel.Author;
        settings.Save();
        await TrySaveAsync(state, instance);

        var packName = panel.PackName;
        var fileName = SafeFileName($"{packName} {panel.PackVersion}");
        string? output;
        if (format == PackFormat.Packwiz)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id);
            output = (await picker.PickSingleFolderAsync())?.Path is { } folder ? PackwizFolder(folder, SafeFileName(packName)) : null;
        }
        else
        {
            var ext = format == PackFormat.Modrinth ? ".mrpack" : ".zip";
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(window.AppWindow.Id)
            {
                SuggestedFileName = fileName,
                DefaultFileExtension = ext,
            };
            picker.FileTypeChoices.Add(Loc.Get(format switch { PackFormat.Modrinth => "Export_FormatModrinth", PackFormat.CurseForge => "Export_FormatCurseForge", _ => "Export_FormatPrism" }), new List<string> { ext });
            output = (await picker.PickSaveFileAsync())?.Path;
        }
        if (output is null)
            return;

        var job = jobs.Start(Loc.Format("Export_JobTitle", packName), instance, null);
        job.Status = Loc.Get("Export_Exporting");
        try
        {
            var options = new PackExportOptions
            {
                Format = format,
                Name = packName,
                Version = panel.PackVersion,
                Author = panel.Author,
                Summary = panel.Summary,
                Include = panel.Include,
                LocalOnly = panel.LocalOnly,
                KeepDisabled = panel.KeepDisabled,
            };
            var result = await Task.Run(() => packs.Exporter.ExportAsync(instance, options, output));
            ContentInstallService.Finish(job, result.Referenced == 0
                ? Loc.Format("Export_DoneCopied", result.Bundled)
                : Loc.Format("Export_Done", result.Referenced, result.Bundled));
            state.ExportedAt = DateTimeOffset.Now;
            state.ExportedFormat = format;
            state.ExportedVersion = options.Version;
            await TrySaveAsync(state, instance);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", format == PackFormat.Packwiz ? $"\"{output}\"" : $"/select,\"{output}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ContentInstallService.Finish(job, ex.Message, failed: true);
            await Dialogs.ShowMessageAsync(Loc.Get("Export_FailedTitle"), ex.Message);
        }
    }

    /// <summary>The export's choices for next time; not being able to keep them doesn't stop the export.</summary>
    private static async Task TrySaveAsync(PackExportState state, GameInstance instance)
    {
        try
        {
            await state.SaveAsync(instance.MetadataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Where a packwiz pack goes in the folder picked: right there when it's empty or already holds a packwiz pack (the
    /// pack's git checkout, exported into again); otherwise a new folder inside named after the pack, so its files
    /// don't spill into Downloads or Documents.
    /// </summary>
    private static string PackwizFolder(string picked, string name)
    {
        try
        {
            if (File.Exists(Path.Combine(picked, "pack.toml")) || !Directory.EnumerateFileSystemEntries(picked).Any())
                return picked;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return picked;
        }
        var folder = Path.Combine(picked, name);
        for (var n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
            folder = Path.Combine(picked, $"{name} ({n})");
        return folder;
    }

    /// <summary>
    /// The name as a file or folder name: any language, with only what Windows refuses replaced, and a "_" before a
    /// name it keeps for a device ("con" → "_con"); "modpack" when nothing's left.
    /// </summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "modpack" : PackPaths.IsReservedName(safe) ? "_" + safe : safe;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task ManualAsync(ContentManager manager, IReadOnlyList<PackFile> blocked)
    {
        var planned = blocked.Select(ModpackInstaller.ToPlannedFile).OfType<PlannedFile>().ToList();
        if (planned.Count > 0)
            await jobs.ManualDownloadsAsync(manager, planned);
    }

    private static Progress<DownloadProgress> Progress(ContentJob job) => new(p =>
    {
        job.Report(p);
        if (p.TotalFiles > 0 && !p.Checking)
            job.Status = DownloadText.DetailsEnabled ? Loc.Get("Modpack_DownloadingFiles") : Loc.Format("Modpack_Downloading", p.CompletedFiles, p.TotalFiles);
    });

    private async Task TryDownloadIconAsync(GameInstance instance, string? url)
    {
        if (instance.Manifest.Icon is not null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return;
        try
        {
            var bytes = await http.GetByteArrayAsync(uri);
            var path = Path.Combine(instance.Directory, "icon" + (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png"));
            await AtomicFile.WriteAllBytesAsync(path, bytes);
            instance.Manifest.Icon = path;
            await layout.SaveAsync(instance);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is HttpRequestException or DownloadFailedException or IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException or TaskCanceledException;

    private static void TryRemove(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
