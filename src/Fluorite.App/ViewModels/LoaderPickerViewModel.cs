using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Modding.Loaders;

namespace Fluorite.App.ViewModels;

/// <summary>A mod-loader choice; <see cref="Uid"/> null means vanilla.</summary>
public sealed partial class LoaderOption(string? uid, string name) : ObservableObject
{
    public string? Uid { get; } = uid;
    public string Name { get; } = name;

    /// <summary>Whether the loader has a build for the picker's Minecraft version; null = not checked or unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectable), nameof(Note))]
    public partial bool? IsAvailable { get; set; }

    /// <summary>Only loaders known to have no build are blocked; an unreachable source never hides a choice.</summary>
    public bool IsSelectable => IsAvailable != false;

    public string Note => IsAvailable == false ? Loc.Get("Loader_Unavailable") : "";

    /// <summary>Also the UI Automation name, so screen readers hear "Forge, not available".</summary>
    public override string ToString() => IsAvailable == false ? $"{Name}, {Note}" : Name;
}

/// <summary>
/// Shared "mod loader + version" picker (new-instance dialog and the instance Version tab). Reloads versions when the
/// Minecraft version or loader changes, cancelling stale requests, and preselects the recommended build.
/// </summary>
public sealed partial class LoaderPickerViewModel : ObservableObject
{
    private readonly LoaderService _loaders;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _check;

    /// <summary>The instance's current loader; its version is preselected only when that same loader is chosen.</summary>
    private (string Uid, string Version)? _current;

    public LoaderPickerViewModel(LoaderService loaders, SettingsService settings)
    {
        _loaders = loaders;
        _settings = settings;
        Options = [new LoaderOption(null, Loc.Get("Loader_None")), .. loaders.Providers.Select(p => new LoaderOption(p.Uid, p.DisplayName))];
    }

    /// <remarks>
    /// An ObservableCollection, like every list handed to WinUI: under Native AOT only concrete collection types
    /// CsWinRT saw at compile time can cross into XAML (an <c>IReadOnlyList</c> collection expression can't).
    /// </remarks>
    public ObservableCollection<LoaderOption> Options { get; }
    public ObservableCollection<LoaderVersion> Versions { get; } = [];

    [ObservableProperty] public partial string? MinecraftVersion { get; set; }
    [ObservableProperty] public partial LoaderVersion? SelectedVersion { get; set; }

    private int _selectedOptionIndex;

    /// <summary>
    /// Index into <see cref="Options"/>. A ComboBox reports -1 when its ItemsSource is (re)assigned during binding
    /// updates; that must never wipe the real selection, so negative values are ignored.
    /// </summary>
    public int SelectedOptionIndex
    {
        get => _selectedOptionIndex;
        set
        {
            if (value < 0 || value >= Options.Count || value == _selectedOptionIndex)
                return;
            _selectedOptionIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLoader));
            OnPropertyChanged(nameof(IsIncomplete));
            _ = ReloadAsync();
        }
    }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }

    public bool HasLoader => SelectedOptionIndex > 0;
    public string? SelectedUid => SelectedOptionIndex > 0 && SelectedOptionIndex < Options.Count ? Options[SelectedOptionIndex].Uid : null;

    /// <summary>True when a loader is chosen but no version is available/selected (block confirming).</summary>
    public bool IsIncomplete => HasLoader && SelectedVersion is null;

    partial void OnMinecraftVersionChanged(string? value)
    {
        _ = ReloadAsync();
        _ = CheckAvailabilityAsync();
    }

    partial void OnSelectedVersionChanged(LoaderVersion? value) => OnPropertyChanged(nameof(IsIncomplete));

    /// <summary>Initialise from an existing instance (Version tab).</summary>
    public void Load(InstanceManifest manifest)
    {
        var loader = ComponentResolver.PrimaryLoader(manifest);
        _current = loader is null ? null : (loader.Uid, loader.Version);
        var index = loader is null ? 0 : Options.ToList().FindIndex(o => o.Uid == loader.Uid);
        SelectedOptionIndex = Math.Max(index, 0);
        MinecraftVersion = manifest.MinecraftVersion;
        _ = ReloadAsync();
        _ = CheckAvailabilityAsync();
    }

    private bool ShowAvailability => _settings.Current.ShowLoaderAvailability;

    /// <summary>Marks every loader as usable or not for <see cref="MinecraftVersion"/> (answers are cached per session).</summary>
    private async Task CheckAvailabilityAsync()
    {
        _check?.Cancel();
        foreach (var option in Options)
            option.IsAvailable = null;
        var mc = MinecraftVersion;
        if (string.IsNullOrEmpty(mc) || !ShowAvailability)
            return;

        var cts = _check = new CancellationTokenSource();
        try
        {
            var result = await _loaders.GetAvailabilityAsync(mc, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            foreach (var option in Options)
            {
                if (option.Uid is { } uid && result.TryGetValue(uid, out var available))
                    option.IsAvailable = available;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReloadAsync()
    {
        _load?.Cancel();
        Versions.Clear();
        SelectedVersion = null;
        Message = null;
        var uid = SelectedUid;
        var mc = MinecraftVersion;
        if (uid is null || string.IsNullOrEmpty(mc))
            return;

        var cts = _load = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var list = await _loaders.Get(uid).GetVersionsAsync(mc, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            foreach (var v in list)
                Versions.Add(v);
            var preferred = _current is { } c && c.Uid == uid ? c.Version : null;
            SelectedVersion = list.FirstOrDefault(v => v.Version == preferred)
                ?? list.FirstOrDefault(v => v.Recommended)
                ?? (list.Count > 0 ? list[0] : null);
            if (list.Count == 0)
                Message = Loc.Format("Loader_NotAvailable", Options[SelectedOptionIndex].Name, mc);
            if (ShowAvailability)
                Options[SelectedOptionIndex].IsAvailable = list.Count > 0; // The full list is the authoritative answer.
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            Message = Loc.Format("Loader_ListFailed", ex.Message);
        }
        finally
        {
            if (_load == cts)
                IsLoading = false;
        }
    }

    /// <summary>Writes the chosen loader into the manifest (replacing any previous loader); clears installed profile ids.</summary>
    public void ApplyTo(InstanceManifest manifest, string minecraftVersion)
    {
        manifest.Components.RemoveAll(c => c.Uid != ComponentUids.Minecraft);
        var mc = manifest.Components.FirstOrDefault(c => c.Uid == ComponentUids.Minecraft);
        if (mc is null)
            manifest.Components.Insert(0, new ComponentRef { Uid = ComponentUids.Minecraft, Version = minecraftVersion });
        else
            mc.Version = minecraftVersion;

        if (SelectedUid is { } uid && SelectedVersion is { } v)
            manifest.Components.Add(new ComponentRef { Uid = uid, Version = v.Version });
    }
}
