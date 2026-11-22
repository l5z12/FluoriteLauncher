using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Core.Launch;
using Fluorite.Core.Worlds;
using Fluorite.Platform.Windows.Shell;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.ViewModels;

/// <summary>One world in an instance's Worlds tab.</summary>
public sealed partial class WorldItemViewModel(WorldInfo info) : ObservableObject
{
    private bool _iconRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Details), nameof(Played), nameof(FolderNote), nameof(HasError), nameof(HasSeed))]
    public partial WorldInfo Info { get; set; } = info;

    public string Name => Info.Name;
    public bool HasError => Info.Error is not null;

    /// <summary>Game mode · Hardcore · Cheats · version.</summary>
    public string Details
    {
        get
        {
            if (Info.Error is { } error)
                return Loc.Format("Worlds_Unreadable", error);
            var parts = new List<string>();
            if (Info.Hardcore)
                parts.Add(Loc.Get("Worlds_Hardcore"));
            else if (Info.GameMode is { } mode)
                parts.Add(Loc.Get("Worlds_Mode" + mode));
            if (Info.CheatsEnabled)
                parts.Add(Loc.Get("Worlds_Cheats"));
            if (Info.VersionName is { Length: > 0 } v)
                parts.Add(v);
            return string.Join("  ·  ", parts);
        }
    }

    public string Played => Info.LastPlayed is { } t ? Loc.Format("Worlds_LastPlayed", Formats.Ago(t)) : "";

    /// <summary>The folder name, shown when it differs from the world's name (that's what Quick Play and backups use).</summary>
    public string FolderNote => string.Equals(Info.FolderName, Info.Name, StringComparison.Ordinal) ? "" : Loc.Format("Worlds_Folder", Info.FolderName);

    [ObservableProperty] public partial string SizeText { get; set; } = "";
    [ObservableProperty] public partial ImageSource? Icon { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>The instance's Minecraft can open this world directly (and level.dat is readable).</summary>
    [ObservableProperty] public partial bool CanPlay { get; set; }

    public bool HasSeed => Info.Seed is not null;

    public async Task LoadIconAsync()
    {
        if (_iconRequested || Info.IconPath is not { } path)
            return;
        _iconRequested = true;
        Icon = await ImageLoader.LoadAsync(path, 96);
    }

    public void ResetIcon() => _iconRequested = false;

    public override string ToString() => Name;
}

/// <summary>A backup listed beside the worlds.</summary>
public sealed class WorldBackupItem(WorldBackup backup)
{
    public WorldBackup Backup { get; } = backup;
    public string Title => Backup.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Details => string.Join("  ·  ", new[] { Formats.Size(Backup.Size), Backup.IsAutomatic ? Loc.Get("Worlds_BackupAutomatic") : null }.OfType<string>());
    public override string ToString() => Title;
}

/// <summary>
/// An instance's singleplayer worlds: play one directly (Quick Play), back up/restore, import, copy, rename, delete,
/// shortcuts. Backups live in the game's own <c>backups/</c> folder.
/// </summary>
public sealed partial class WorldsViewModel(GameRunner runner, ShortcutService shortcuts, QuickPlaySupport quickPlay) : ObservableObject
{
    private WorldStore? _store;
    private List<WorldItemViewModel> _all = [];
    private CancellationTokenSource? _sizes;

    public GameInstance? Instance { get; private set; }
    public ObservableCollection<WorldItemViewModel> Worlds { get; } = [];
    public ObservableCollection<WorldBackupItem> Backups { get; } = [];

    /// <summary>Set by the page: launches the instance (so the page follows the new game).</summary>
    public Action<QuickPlay?>? Launch { get; set; }

    /// <summary>Set by the page: shows the Data packs tab for a world folder.</summary>
    public Action<string>? ShowDataPacks { get; set; }

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool CanOpenWorlds { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModify))]
    public partial bool IsGameRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(BackupsHeader))]
    public partial WorldItemViewModel? SelectedWorld { get; set; }

    [ObservableProperty] public partial bool HasNoBackups { get; set; }

    public bool CanModify => !IsGameRunning;
    public bool HasSelection => SelectedWorld is not null;
    public string BackupsHeader => SelectedWorld is { } w ? Loc.Format("Worlds_BackupsOf", w.Name) : Loc.Get("Worlds_Backups");

    public async Task InitializeAsync(GameInstance instance)
    {
        Instance = instance;
        _store = new WorldStore(instance.GameDirectory);
        runner.ActivityChanged += OnActivityChanged;
        OnActivityChanged(null, EventArgs.Empty);
        await RefreshAsync();
        CanOpenWorlds = await quickPlay.CanOpenWorldsAsync(instance);
    }

    public void Detach()
    {
        runner.ActivityChanged -= OnActivityChanged;
        _sizes?.Cancel();
    }

    private void OnActivityChanged(object? sender, EventArgs e) => IsGameRunning = Instance is not null && runner.Find(Instance) is not null;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnCanOpenWorldsChanged(bool value)
    {
        foreach (var w in _all)
            w.CanPlay = value && !w.HasError;
    }

    /// <summary>Renaming, deleting or replacing a world under the running game would lose or corrupt it.</summary>
    private bool EnsureNotRunning()
    {
        if (!IsGameRunning)
            return true;
        Error = Loc.Get("Worlds_StopFirst");
        return false;
    }

    partial void OnSelectedWorldChanged(WorldItemViewModel? value) => _ = LoadBackupsAsync();

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        Worlds.Clear();
        foreach (var w in _all.Where(w => f.Length == 0 || w.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || w.Info.FolderName.Contains(f, StringComparison.CurrentCultureIgnoreCase)))
            Worlds.Add(w);
        IsEmpty = _all.Count == 0 && !IsLoading;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_store is not { } store)
            return;
        IsLoading = true;
        Error = null;
        var selected = SelectedWorld?.Info.FolderName;
        try
        {
            var infos = await Task.Run(store.GetWorlds);
            var previous = _all.ToDictionary(w => w.Info.FolderName, StringComparer.OrdinalIgnoreCase);
            _all = infos.Select(i =>
            {
                if (!previous.TryGetValue(i.FolderName, out var existing))
                    return new WorldItemViewModel(i);
                existing.Info = i;
                existing.ResetIcon();
                return existing;
            }).ToList();
            foreach (var w in _all)
                w.CanPlay = CanOpenWorlds && !w.HasError;
            ApplyFilter();
            SelectedWorld = _all.FirstOrDefault(w => string.Equals(w.Info.FolderName, selected, StringComparison.OrdinalIgnoreCase));
            await LoadBackupsAsync();
            _ = ComputeSizesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            IsEmpty = _all.Count == 0;
        }
    }

    private async Task ComputeSizesAsync()
    {
        _sizes?.Cancel();
        var cts = _sizes = new CancellationTokenSource();
        foreach (var w in _all.ToList())
        {
            if (cts.IsCancellationRequested)
                return;
            var size = await Task.Run(() => WorldStore.GetSize(w.Info.Path));
            w.SizeText = Formats.Size(size);
        }
    }

    private async Task LoadBackupsAsync()
    {
        if (_store is not { } store)
            return;
        var folder = SelectedWorld?.Info.FolderName;
        var list = await Task.Run(() => store.GetBackups(folder));
        if (folder != SelectedWorld?.Info.FolderName)
            return; // The selection moved on meanwhile.
        Backups.Clear();
        foreach (var b in list)
            Backups.Add(new WorldBackupItem(b));
        HasNoBackups = Backups.Count == 0;
    }

    public void Play(WorldItemViewModel world) => Launch?.Invoke(new QuickPlay(QuickPlayKind.Singleplayer, world.Info.FolderName));

    public async Task BackupAsync(WorldItemViewModel world)
    {
        if (_store is not { } store)
            return;
        world.IsBusy = true;
        Status = null;
        Error = null;
        try
        {
            var info = world.Info;
            var backup = await Task.Run(() => store.Backup(info));
            Status = Loc.Format("Worlds_BackedUp", world.Name, backup.FileName);
            SelectedWorld = world;
            await LoadBackupsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Format("Worlds_BackupFailed", ex.Message);
        }
        finally
        {
            world.IsBusy = false;
        }
    }

    public async Task RestoreAsync(WorldBackupItem item)
    {
        if (_store is not { } store)
            return;
        var backup = item.Backup;
        var existing = store.Find(backup.WorldFolder);
        var asCopy = false;
        if (existing is not null)
        {
            var dialog = Dialogs.Create(
                Loc.Format("Worlds_RestoreTitle", existing.Name),
                Dialogs.Text(Loc.Format("Worlds_RestoreBody", item.Title)),
                primary: Loc.Get("Worlds_RestoreReplace"),
                secondary: Loc.Get("Worlds_RestoreAsCopy"));
            dialog.IsPrimaryButtonEnabled = CanModify;
            var result = await Dialogs.ShowAsync(dialog);
            if (result == ContentDialogResult.None)
                return;
            asCopy = result == ContentDialogResult.Secondary;
        }

        Status = null;
        Error = null;
        IsLoading = true;
        try
        {
            var folder = await Task.Run(() =>
            {
                if (existing is null)
                {
                    store.Restore(backup, backup.WorldFolder);
                    return backup.WorldFolder;
                }
                if (asCopy)
                {
                    var copyFolder = WorldStore.AvailableName(store.SavesDirectory, backup.WorldFolder, "");
                    var path = store.Restore(backup, copyFolder);
                    WorldStore.Rename(WorldInfo.Read(path), $"{existing.Name} ({backup.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})");
                    return copyFolder;
                }
                // Replace: extract next to it first, so a bad zip never costs the current world.
                var staging = WorldStore.AvailableName(store.SavesDirectory, backup.WorldFolder + " restoring", "");
                var staged = store.Restore(backup, staging);
                try
                {
                    RecycleBin.Send(existing.Path);
                }
                catch
                {
                    Directory.Delete(staged, recursive: true);
                    throw;
                }
                Directory.Move(staged, existing.Path);
                return backup.WorldFolder;
            });
            Status = Loc.Format("Worlds_Restored", item.Title);
            await RefreshAsync();
            SelectedWorld = _all.FirstOrDefault(w => string.Equals(w.Info.FolderName, folder, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Error = Loc.Format("Worlds_RestoreFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>"Removed … Undo" after the last removal (of a world or a backup).</summary>
    [ObservableProperty] public partial UndoOffer? PendingUndo { get; set; }

    private void OfferUndo(UndoOffer offer)
    {
        _ = PendingUndo?.CommitAsync();
        offer.Finished += (_, _) =>
        {
            if (PendingUndo == offer)
                PendingUndo = null;
        };
        PendingUndo = offer;
    }

    /// <summary>Stashed for Undo, then to the Recycle Bin; null (and the error shown) when it couldn't be moved.</summary>
    private async Task<RemovalStash?> StashAsync(string path)
    {
        if (Instance is not { } instance)
            return null;
        try
        {
            return await Task.Run(() => RemovalStash.Create(instance.GameDirectory, [path]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return null;
        }
    }

    public async Task DeleteBackupAsync(WorldBackupItem item)
    {
        if (await StashAsync(item.Backup.Path) is not { } stash)
            return;
        Backups.Remove(item);
        HasNoBackups = Backups.Count == 0;
        OfferUndo(new UndoOffer(Loc.Format("Undo_RemovedOne", item.Title), async () =>
        {
            await Task.Run(stash.Restore);
            await RefreshAsync();
        }, stash.Commit));
    }

    public async Task RenameAsync(WorldItemViewModel world)
    {
        if (!EnsureNotRunning())
            return;
        if (await AskNameAsync(Loc.Get("Worlds_RenameTitle"), world.Name, Loc.Get("Worlds_Rename")) is not { } name || name == world.Name)
            return;
        try
        {
            var info = world.Info;
            await Task.Run(() => WorldStore.Rename(info, name));
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = ex.Message;
        }
    }

    public async Task CopyAsync(WorldItemViewModel world)
    {
        if (_store is not { } store || await AskNameAsync(Loc.Get("Worlds_CopyTitle"), Loc.Format("Worlds_CopyName", world.Name), Loc.Get("Worlds_Copy")) is not { } name)
            return;
        world.IsBusy = true;
        try
        {
            var info = world.Info;
            var folder = await Task.Run(() => store.Copy(info, name));
            Status = Loc.Format("Worlds_Copied", name);
            await RefreshAsync();
            SelectedWorld = _all.FirstOrDefault(w => w.Info.FolderName == folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = ex.Message;
        }
        finally
        {
            world.IsBusy = false;
        }
    }

    /// <summary>Gone from the list (and the game's) at once; Undo for a few seconds, then the Recycle Bin.</summary>
    public async Task DeleteAsync(WorldItemViewModel world)
    {
        if (!EnsureNotRunning() || await StashAsync(world.Info.Path) is not { } stash)
            return;
        Status = null;
        await RefreshAsync();
        OfferUndo(new UndoOffer(Loc.Format("Undo_RemovedOne", world.Name), async () =>
        {
            await Task.Run(stash.Restore);
            await RefreshAsync();
        }, stash.Commit));
    }

    /// <summary>Worlds from zips (backups, downloaded maps) or world folders.</summary>
    public async Task ImportAsync(IEnumerable<string> paths)
    {
        if (_store is not { } store)
            return;
        Status = null;
        Error = null;
        var added = new List<string>();
        IsLoading = true;
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    added.Add(await Task.Run(() => store.Import(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Error = Loc.Format("Worlds_ImportFailed", Path.GetFileName(path), ex.Message);
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
        if (added.Count > 0)
        {
            Status = Loc.Format(added.Count == 1 ? "Worlds_ImportedOne" : "Worlds_Imported", added.Count);
            await RefreshAsync();
            SelectedWorld = _all.FirstOrDefault(w => w.Info.FolderName == added[^1]);
        }
    }

    public void CreateShortcut(WorldItemViewModel world)
    {
        if (Instance is null)
            return;
        shortcuts.CreateDesktopShortcut(Instance, new QuickPlay(QuickPlayKind.Singleplayer, world.Info.FolderName), $"{world.Name} - {Instance.Name}");
        Status = Loc.Format("Shortcut_Created", world.Name);
    }

    public void CopySeed(WorldItemViewModel world)
    {
        if (world.Info.Seed is not { } seed)
            return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(seed.ToString(CultureInfo.InvariantCulture));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        Status = Loc.Format("Worlds_SeedCopied", seed.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void OpenFolder() => OpenDirectory(_store?.SavesDirectory);

    [RelayCommand]
    private void OpenBackupsFolder() => OpenDirectory(_store?.BackupsDirectory);

    public static void ShowInFolder(WorldItemViewModel world) => OpenDirectory(world.Info.Path);

    public static void ShowBackupInFolder(WorldBackupItem item) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{item.Backup.Path}\"") { UseShellExecute = false });

    private static void OpenDirectory(string? path)
    {
        if (path is null)
            return;
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static async Task<string?> AskNameAsync(string title, string initial, string primary)
    {
        var box = new TextBox { Text = initial, MinWidth = 360, MaxLength = 120 };
        box.Loaded += (_, _) =>
        {
            box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            box.SelectAll();
        };
        var dialog = Dialogs.Create(title, box, primary);
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}

