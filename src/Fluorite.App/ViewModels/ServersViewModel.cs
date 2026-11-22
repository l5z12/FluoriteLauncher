using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Core.Launch;
using Fluorite.Core.Servers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.ViewModels;

/// <summary>One server of the multiplayer list, with its live status.</summary>
public sealed partial class ServerItemViewModel : ObservableObject
{
    private string? _shownIcon;

    public ServerItemViewModel(ServerEntry entry)
    {
        Entry = entry;
        Name = entry.Name;
        Address = entry.Address;
    }

    public ServerEntry Entry { get; }

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string Address { get; set; }
    [ObservableProperty] public partial bool IsPinging { get; set; }
    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ChatSpan> Motd { get; set; } = [];
    [ObservableProperty] public partial string PlayersText { get; set; } = "";
    [ObservableProperty] public partial string PlayersTooltip { get; set; } = "";
    [ObservableProperty] public partial string LatencyText { get; set; } = "";
    [ObservableProperty] public partial string VersionText { get; set; } = "";
    [ObservableProperty] public partial ImageSource? Icon { get; set; }

    /// <summary>Signal-bar glyph for the latency (Segoe Fluent "network" bars), or the "no connection" glyph.</summary>
    [ObservableProperty] public partial string SignalGlyph { get; set; } = "";

    public void Sync()
    {
        Name = Entry.Name;
        Address = Entry.Address;
    }

    public async Task ShowIconAsync(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64 == _shownIcon)
            return;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return;
        }
        if (await ImageLoader.FromBytesAsync(bytes, 0) is { } image)
        {
            Icon = image;
            _shownIcon = base64;
        }
    }

    public void ShowStatus(ServerStatus s)
    {
        IsOnline = true;
        Motd = s.Motd;
        PlayersText = Loc.Format("Servers_Players", s.OnlinePlayers, s.MaxPlayers);
        PlayersTooltip = s.PlayerSample.Count > 0 ? string.Join("\n", s.PlayerSample) : PlayersText;
        var ms = (int)Math.Round(s.Latency.TotalMilliseconds);
        LatencyText = Loc.Format("Servers_Latency", ms);
        SignalGlyph = ms switch { < 150 => "", < 300 => "", < 600 => "", _ => "" };
        VersionText = s.VersionName ?? "";
    }

    public void ShowOffline(string reason)
    {
        IsOnline = false;
        Motd = [new ChatSpan(reason, "#FF5555")];
        PlayersText = "";
        PlayersTooltip = "";
        LatencyText = "";
        VersionText = "";
        SignalGlyph = "";
    }

    public override string ToString() => Name;
}

/// <summary>
/// An instance's multiplayer list (<c>servers.dat</c>): live status, join directly (Quick Play), add/edit/remove/reorder.
/// The game rewrites the file whenever its own list changes, so editing is off while the instance is running.
/// </summary>
public sealed partial class ServersViewModel(ServerPinger pinger, GameRunner runner, ShortcutService shortcuts) : ObservableObject
{
    private ServerList _list = new();
    private CancellationTokenSource? _ping;

    public GameInstance? Instance { get; private set; }
    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    /// <summary>Set by the page: launches the instance (so the page follows the new game).</summary>
    public Action<QuickPlay?>? Launch { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModify))]
    public partial bool IsGameRunning { get; set; }

    public bool CanModify => !IsGameRunning;

    public async Task InitializeAsync(GameInstance instance)
    {
        Instance = instance;
        runner.ActivityChanged += OnActivityChanged;
        OnActivityChanged(null, EventArgs.Empty);
        await RefreshAsync();
    }

    public void Detach()
    {
        runner.ActivityChanged -= OnActivityChanged;
        _ping?.Cancel();
    }

    private void OnActivityChanged(object? sender, EventArgs e)
    {
        var running = Instance is not null && runner.Find(Instance) is not null;
        if (IsGameRunning && !running)
            _ = RefreshAsync(); // The game may have changed the list.
        IsGameRunning = running;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Instance is null)
            return;
        IsLoading = true;
        Error = null;
        try
        {
            var dir = Instance.GameDirectory;
            _list = await Task.Run(() => ServerList.Load(dir));
            Servers.Clear();
            foreach (var s in _list.Servers.Where(s => !s.Hidden))
            {
                var item = new ServerItemViewModel(s);
                Servers.Add(item);
                _ = item.ShowIconAsync(s.IconBase64);
            }
            IsEmpty = Servers.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = Loc.Format("Servers_ReadFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
        PingAll();
    }

    [RelayCommand]
    private void PingAll()
    {
        _ping?.Cancel();
        var cts = _ping = new CancellationTokenSource();
        foreach (var item in Servers.ToList())
            _ = PingAsync(item, cts.Token);
    }

    private async Task PingAsync(ServerItemViewModel item, CancellationToken ct)
    {
        item.IsPinging = true;
        try
        {
            var status = await pinger.PingAsync(item.Address, ct);
            if (ct.IsCancellationRequested)
                return;
            item.ShowStatus(status);
            await item.ShowIconAsync(status.FaviconBase64);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            item.ShowOffline(ex is InvalidDataException or System.Text.Json.JsonException ? Loc.Get("Servers_BadResponse") : Loc.Get("Servers_Unreachable"));
        }
        finally
        {
            item.IsPinging = false;
        }
    }

    public void Join(ServerItemViewModel item) => Launch?.Invoke(new QuickPlay(QuickPlayKind.Multiplayer, item.Address));

    [RelayCommand]
    private async Task AddAsync()
    {
        if (await EditDialogAsync(Loc.Get("Servers_AddTitle"), new ServerEntry(Loc.Get("Servers_DefaultName"), "")) is not { } entry)
            return;
        var visible = _list.Servers.Where(s => !s.Hidden).ToList();
        _list.Servers.Insert(visible.Count == 0 ? 0 : _list.Servers.IndexOf(visible[^1]) + 1, entry);
        var item = new ServerItemViewModel(entry);
        Servers.Add(item);
        IsEmpty = false;
        if (Save())
            _ = PingAsync(item, _ping?.Token ?? CancellationToken.None);
    }

    private bool EnsureNotRunning()
    {
        if (!IsGameRunning)
            return true;
        Error = Loc.Get("Servers_GameRunning");
        return false;
    }

    public async Task EditAsync(ServerItemViewModel item)
    {
        if (!EnsureNotRunning())
            return;
        var addressBefore = item.Entry.Address;
        if (await EditDialogAsync(Loc.Get("Servers_EditTitle"), item.Entry) is null)
            return;
        item.Sync();
        if (Save() && item.Entry.Address != addressBefore)
            _ = PingAsync(item, _ping?.Token ?? CancellationToken.None);
    }

    /// <summary>"Removed … Undo" after the last removal.</summary>
    [ObservableProperty] public partial UndoOffer? PendingUndo { get; set; }

    /// <summary>Removed at once; Undo puts it back in its place.</summary>
    public void Delete(ServerItemViewModel item)
    {
        if (!EnsureNotRunning())
            return;
        var fileIndex = _list.Servers.IndexOf(item.Entry);
        var listIndex = Servers.IndexOf(item);
        _list.Servers.Remove(item.Entry);
        Servers.Remove(item);
        IsEmpty = Servers.Count == 0;
        if (!Save())
            return;
        var offer = new UndoOffer(Loc.Format("Undo_RemovedOne", item.Name), () =>
        {
            if (EnsureNotRunning())
            {
                _list.Servers.Insert(Math.Clamp(fileIndex, 0, _list.Servers.Count), item.Entry);
                Servers.Insert(Math.Clamp(listIndex, 0, Servers.Count), item);
                IsEmpty = false;
                Save();
            }
            return Task.CompletedTask;
        });
        _ = PendingUndo?.CommitAsync();
        offer.Finished += (_, _) =>
        {
            if (PendingUndo == offer)
                PendingUndo = null;
        };
        PendingUndo = offer;
    }

    /// <summary>After drag-reordering the list: same order in the file (hidden entries stay at the end).</summary>
    /// <summary>Move up or down one place: the keyboard's way to drag (Alt+Up, Alt+Down).</summary>
    public bool Move(ServerItemViewModel item, int delta)
    {
        var from = Servers.IndexOf(item);
        var to = from + delta;
        if (!CanModify || from < 0 || to < 0 || to >= Servers.Count)
            return false;
        Servers.Move(from, to);
        SaveOrder();
        return true;
    }

    public void SaveOrder()
    {
        var hidden = _list.Servers.Where(s => s.Hidden).ToList();
        _list.Servers.Clear();
        _list.Servers.AddRange(Servers.Select(s => s.Entry));
        _list.Servers.AddRange(hidden);
        Save();
    }

    private bool Save()
    {
        if (Instance is null || !EnsureNotRunning())
            return false;
        try
        {
            _list.Save(Instance.GameDirectory);
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Format("Servers_SaveFailed", ex.Message);
            return false;
        }
    }

    public void CopyAddress(ServerItemViewModel item)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(item.Address);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        Status = Loc.Format("Servers_AddressCopied", item.Address);
    }

    public void CreateShortcut(ServerItemViewModel item)
    {
        if (Instance is null)
            return;
        shortcuts.CreateDesktopShortcut(Instance, new QuickPlay(QuickPlayKind.Multiplayer, item.Address), $"{item.Name} - {Instance.Name}");
        Status = Loc.Format("Shortcut_Created", item.Name);
    }

    /// <summary>Name, address and resource-pack choice; edits <paramref name="entry"/> in place on OK.</summary>
    private static async Task<ServerEntry?> EditDialogAsync(string title, ServerEntry entry)
    {
        var name = new TextBox { Header = Loc.Get("Servers_Name"), Text = entry.Name, MaxLength = 100 };
        var address = new TextBox { Header = Loc.Get("Servers_Address"), Text = entry.Address, PlaceholderText = "play.example.net", MaxLength = 255 };
        var packs = new ComboBox { Header = Loc.Get("Servers_ResourcePacks"), HorizontalAlignment = HorizontalAlignment.Stretch };
        packs.Items.Add(Loc.Get("Servers_PacksPrompt"));
        packs.Items.Add(Loc.Get("Servers_PacksEnabled"));
        packs.Items.Add(Loc.Get("Servers_PacksDisabled"));
        packs.SelectedIndex = (int)entry.ResourcePacks;
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(name);
        panel.Children.Add(address);
        panel.Children.Add(packs);

        var dialog = Dialogs.Create(title, panel, Loc.Get("Common_Save/Content"));
        void Validate() => dialog.IsPrimaryButtonEnabled = address.Text.Trim().Length > 0;
        address.TextChanged += (_, _) => Validate();
        Validate();
        address.Loaded += (_, _) => (entry.Address.Length == 0 ? address : name).Focus(FocusState.Programmatic);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;
        entry.Name = name.Text.Trim() is { Length: > 0 } n ? n : Loc.Get("Servers_DefaultName");
        entry.Address = address.Text.Trim();
        entry.ResourcePacks = (ServerResourcePacks)Math.Max(0, packs.SelectedIndex);
        return entry;
    }
}
