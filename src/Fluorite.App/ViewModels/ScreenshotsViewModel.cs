using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Core.Worlds;
using Fluorite.Platform.Windows.Shell;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Fluorite.App.ViewModels;

public sealed partial class ScreenshotItemViewModel(Screenshot shot) : ObservableObject
{
    private bool _requested;
    private int _generation;

    public Screenshot Shot { get; } = shot;
    public string FileName => Shot.FileName;
    public string Taken => Shot.Taken.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Loaded while the item is on screen (or near it) and released when the gallery recycles its tile.</summary>
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public async Task LoadThumbnailAsync()
    {
        if (_requested)
            return;
        _requested = true;
        var generation = _generation;
        // The tile's width; decoded at exactly that size, so a thumbnail is ~0.3 MB whatever the screenshot's size.
        var image = await ImageLoader.LoadAsync(Shot.Path, 240);
        if (generation != _generation)
        {
            ImageLoader.Release(image); // Scrolled away (or released) while it decoded.
            return;
        }
        Thumbnail = image;
    }

    public void ReleaseThumbnail()
    {
        _generation++;
        _requested = false;
        var image = Thumbnail;
        Thumbnail = null; // Off the Image first, then freed.
        ImageLoader.Release(image);
    }

    public override string ToString() => FileName;
}

/// <summary>An instance's <c>screenshots/</c> folder: gallery, preview, copy, share, delete.</summary>
public sealed partial class ScreenshotsViewModel : ObservableObject
{
    public GameInstance? Instance { get; private set; }
    public ObservableCollection<ScreenshotItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    public async Task InitializeAsync(GameInstance instance)
    {
        Instance = instance;
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Instance is null)
            return;
        IsLoading = true;
        try
        {
            var dir = Instance.GameDirectory;
            var shots = await Task.Run(() => ScreenshotLibrary.List(dir));
            ReleaseThumbnails();
            Items.Clear();
            foreach (var s in shots)
                Items.Add(new ScreenshotItemViewModel(s));
            UpdateSummary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Frees every loaded thumbnail (the page is going away, or the list is reloaded).</summary>
    public void ReleaseThumbnails()
    {
        foreach (var item in Items)
            item.ReleaseThumbnail();
    }

    private void UpdateSummary()
    {
        IsEmpty = Items.Count == 0;
        Summary = Items.Count == 0 ? "" : Loc.Format(Items.Count == 1 ? "Screenshots_SummaryOne" : "Screenshots_Summary", Items.Count, Formats.Size(Items.Sum(i => i.Shot.Size)));
    }

    public async Task CopyAsync(IReadOnlyList<ScreenshotItemViewModel> items)
    {
        if (items.Count == 0)
            return;
        try
        {
            var files = await FilesAsync(items);
            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetStorageItems(files);
            if (files.Count == 1)
                dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile((StorageFile)files[0]));
            Clipboard.SetContent(dp);
            Clipboard.Flush();
            Status = Loc.Format(items.Count == 1 ? "Screenshots_CopiedOne" : "Screenshots_Copied", items.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Error = ex.Message;
        }
    }

    /// <summary>The Windows share sheet (Mail, Nearby Share, apps…) for the selected images.</summary>
    public async Task ShareAsync(IReadOnlyList<ScreenshotItemViewModel> items)
    {
        if (items.Count == 0 || App.Window is not { } window)
            return;
        List<IStorageItem> files;
        try
        {
            files = await FilesAsync(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return;
        }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var manager = DataTransferManagerInterop.GetForWindow(hwnd);
        void OnRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            sender.DataRequested -= OnRequested;
            args.Request.Data.Properties.Title = items.Count == 1 ? items[0].FileName : Loc.Format("Screenshots_ShareTitle", items.Count);
            args.Request.Data.SetStorageItems(files);
            if (files.Count == 1)
                args.Request.Data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile((StorageFile)files[0]));
        }
        manager.DataRequested += OnRequested;
        try
        {
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            manager.DataRequested -= OnRequested;
            Error = ex.Message;
        }
    }

    /// <summary>A concrete <c>List&lt;IStorageItem&gt;</c>, the exact type Windows takes: under Native AOT a list crosses into
    /// WinRT only as a type CsWinRT generated code for.</summary>
    private static async Task<List<IStorageItem>> FilesAsync(IEnumerable<ScreenshotItemViewModel> items)
    {
        var files = new List<IStorageItem>();
        foreach (var i in items)
            files.Add(await StorageFile.GetFileFromPathAsync(i.Shot.Path));
        return files;
    }

    /// <summary>"Removed … Undo" after the last removal.</summary>
    [ObservableProperty] public partial UndoOffer? PendingUndo { get; set; }

    /// <summary>Removed at once: Undo for a few seconds, then the Recycle Bin. False when nothing was removed.</summary>
    public async Task<bool> DeleteAsync(IReadOnlyList<ScreenshotItemViewModel> items)
    {
        if (items.Count == 0 || Instance is not { } instance)
            return false;
        var shots = items.ToList();
        RemovalStash stash;
        try
        {
            stash = await Task.Run(() => RemovalStash.Create(instance.GameDirectory, shots.Select(s => s.Shot.Path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return false;
        }
        foreach (var item in shots)
        {
            Items.Remove(item);
            item.ReleaseThumbnail();
        }
        UpdateSummary();
        var message = shots.Count == 1 ? Loc.Format("Undo_RemovedOne", shots[0].FileName) : Loc.Format("Undo_RemovedMany", shots.Count);
        var offer = new UndoOffer(message, async () =>
        {
            await Task.Run(stash.Restore);
            await RefreshAsync();
        }, stash.Commit);
        _ = PendingUndo?.CommitAsync();
        offer.Finished += (_, _) =>
        {
            if (PendingUndo == offer)
                PendingUndo = null;
        };
        PendingUndo = offer;
        return true;
    }

    public static void Open(ScreenshotItemViewModel item) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Shot.Path) { UseShellExecute = true });

    public static void ShowInFolder(ScreenshotItemViewModel item) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{item.Shot.Path}\"") { UseShellExecute = false });

    [RelayCommand]
    private void OpenFolder()
    {
        if (Instance is null)
            return;
        var dir = ScreenshotLibrary.DirectoryOf(Instance.GameDirectory);
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
