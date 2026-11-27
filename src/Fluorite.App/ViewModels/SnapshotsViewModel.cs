using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.ViewModels;

public sealed partial class SnapshotItem(InstanceSnapshot snapshot)
{
    public InstanceSnapshot Snapshot { get; } = snapshot;
    private SnapshotManifest Manifest => Snapshot.Manifest;

    /// <summary>The note it was taken with, or why Fluorite took it ("Before updating 3 mods").</summary>
    public string Title => Manifest.Label is { Length: > 0 } label ? label : Loc.Get("Snapshot_Untitled");

    /// <summary>"2 hours ago"; the exact time is in <see cref="WhenTooltip"/>.</summary>
    public string When => Formats.Ago(Manifest.Created);

    public string WhenTooltip => Formats.When(Manifest.Created);

    /// <summary>"1.21.1 · Fabric 0.16.10  ·  34 mods  ·  Automatic".</summary>
    public string Details
    {
        get
        {
            var parts = new List<string> { InstanceItem.DescribeVersion(new InstanceManifest { Components = Manifest.Components }) };
            if (Manifest.ModCount > 0)
                parts.Add(Manifest.ModCount == 1 ? Loc.Get("Snapshot_OneMod") : Loc.Format("Snapshot_Mods", Manifest.ModCount));
            if (Manifest.Automatic)
                parts.Add(Loc.Get("Snapshot_Automatic"));
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>An instance's snapshots: take one, go back to one, delete (with Undo).</summary>
public sealed partial class SnapshotsViewModel(SnapshotService snapshots, GameRunner runner, ContentInstallService installer) : ObservableObject
{
    public GameInstance? Instance { get; private set; }

    public ObservableCollection<SnapshotItem> Items { get; } = [];

    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>"Removed … Undo" after a snapshot was deleted.</summary>
    [ObservableProperty] public partial UndoOffer? PendingUndo { get; set; }

    /// <summary>A snapshot was restored: the page shows the instance's loader setup again, its lists rescan.</summary>
    public event EventHandler? Restored;

    public void Initialize(GameInstance instance)
    {
        Instance = instance;
        snapshots.Changed += OnChanged;
        var dir = snapshots.DirectoryFor(instance);
        _ = Task.Run(() => RemovalStash.CommitLeftovers(dir));
        Refresh();
    }

    public void Detach() => snapshots.Changed -= OnChanged;

    private void OnChanged(object? sender, string instanceId)
    {
        if (Instance?.Id == instanceId)
            Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        if (Instance is not { } instance)
            return;
        Items.Clear();
        try
        {
            foreach (var s in snapshots.List(instance))
                Items.Add(new SnapshotItem(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        IsEmpty = Items.Count == 0;
    }

    /// <summary>A note is optional ("Before adding shaders"); Enter takes it.</summary>
    [RelayCommand]
    private async Task TakeAsync()
    {
        if (Instance is not { } instance || IsBusy)
            return;
        var note = new TextBox { PlaceholderText = Loc.Get("Snapshot_NotePlaceholder"), MaxLength = 100, MinWidth = 380 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(note, Loc.Get("Snapshot_Note"));
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 10 };
        panel.Children.Add(Dialogs.Text(Loc.Get("Snapshot_TakeBody"), secondary: true));
        panel.Children.Add(note);
        var dialog = Dialogs.Create(Loc.Get("Snapshot_TakeTitle"), panel, Loc.Get("Snapshot_Take"));
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
            return;
        IsBusy = true;
        Error = null;
        try
        {
            var taken = await snapshots.TakeAsync(instance, string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim(), automatic: false);
            Status = Loc.Format("Snapshot_Taken", new SnapshotItem(taken).Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RestoreAsync(SnapshotItem item)
    {
        if (Instance is not { } instance || IsBusy)
            return;
        if (runner.Find(instance) is not null)
        {
            Error = Loc.Get("Snapshot_StopFirst");
            return;
        }
        if (await Dialogs.ShowAsync(Loc.Format("Snapshot_RestoreTitle", item.When), Dialogs.Text(Loc.Get("Snapshot_RestoreBody")), Loc.Get("Snapshot_Restore")) != ContentDialogResult.Primary)
            return;
        IsBusy = true;
        Error = null;
        Status = null;
        try
        {
            await snapshots.RestoreAsync(instance, item.Snapshot);
            installer.NotifyChanged(instance);
            Status = Loc.Format("Snapshot_Restored", item.Title);
            Restored?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Gone at once; Undo for a few seconds, then the Recycle Bin (<paramref name="permanent"/>: deleted for good).</summary>
    public async Task DeleteAsync(SnapshotItem item, bool permanent = false)
    {
        if (Instance is not { } instance)
            return;
        RemovalStash stash;
        try
        {
            var dir = snapshots.DirectoryFor(instance);
            stash = await Task.Run(() => RemovalStash.Create(dir, [item.Snapshot.Directory], permanent));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return;
        }
        Items.Remove(item);
        IsEmpty = Items.Count == 0;
        var offer = new UndoOffer(UndoOffer.RemovedText([item.Title], permanent), async () =>
        {
            var allBack = await Task.Run(stash.Restore);
            Refresh();
            if (!allBack)
                Error = Loc.Get("Undo_NotAllBack"); // After the reload, which clears it.
        }, stash.Commit);
        _ = PendingUndo?.CommitAsync();
        offer.Finished += (_, _) =>
        {
            if (PendingUndo == offer)
                PendingUndo = null;
        };
        PendingUndo = offer;
    }

    public static void ShowInFolder(SnapshotItem item) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{item.Snapshot.Directory}\"") { UseShellExecute = false });

    [RelayCommand]
    private void OpenFolder()
    {
        if (Instance is null)
            return;
        var dir = Directory.CreateDirectory(snapshots.DirectoryFor(Instance)).FullName;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
