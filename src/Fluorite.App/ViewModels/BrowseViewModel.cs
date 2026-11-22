using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Core.Versions;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.ViewModels;

/// <summary>Opens Browse for an instance and content kind (from an instance's content tab).</summary>
public sealed record BrowseContext(string? InstanceId, ContentKind Kind, string? Query = null);

/// <summary>Opens a project page; <see cref="Instance"/> is the install target, if any.</summary>
public sealed record ProjectNavigation(RemoteProject Project, GameInstance? Instance, ContentKind Kind);

public sealed record InstanceChoice(GameInstance? Instance, string Label)
{
    public override string ToString() => Label;
}

public sealed record NamedChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A search result card.</summary>
public sealed partial class ProjectCard(RemoteProject project) : ObservableObject
{
    private ImageSource? _icon;

    public RemoteProject Project { get; } = project;
    public string Title => Project.Title;
    public string Author => Project.Author is { Length: > 0 } a ? Loc.Format("Browse_ByAuthor", a) : "";
    public string Summary => Project.Summary ?? "";
    public string DownloadsShort => Formats.Count(Project.Downloads);
    public string DownloadsTooltip => Loc.Format("Browse_Downloads", Project.Downloads.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
    public string UpdatedShort => Formats.Ago(Project.Updated);
    public string? UpdatedTooltip => Project.Updated is { } t ? Loc.Format("Browse_Updated", t.LocalDateTime.ToString("D", System.Globalization.CultureInfo.CurrentCulture)) : null;
    private IEnumerable<string> OrderedTags => Project.Categories.OrderBy(Formats.IsLoaderTag).Select(Formats.Tag); // Stable: categories, then loaders.
    public string TagsText => string.Join(", ", OrderedTags.Take(4));
    public string? TagsTooltip => Project.Categories.Count > 0 ? string.Join(", ", OrderedTags) : null;
    public string Key => ContentInstallService.Key(Project.Platform, Project.Id);

    /// <summary>Created on first binding (UI thread); WinUI downloads and caches it.</summary>
    public ImageSource? Icon => _icon ??= Project.IconUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? new BitmapImage(uri) { DecodePixelWidth = 96, DecodePixelType = DecodePixelType.Logical }
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsInstalling { get; set; }

    public bool CanInstall => !IsInstalled && !IsInstalling;
}

public sealed partial class BrowseViewModel(
    ContentService content, InstanceRegistry layout, SettingsService settings, ContentInstallService installer, VersionRepository versions) : ObservableObject
{
    private const int PageSize = 30;
    private CancellationTokenSource? _search;
    private HashSet<string> _installed = [];
    private bool _initializing;
    private bool _initialized;

    public static IReadOnlyList<ContentKind> Kinds { get; } = [ContentKind.Mod, ContentKind.ResourcePack, ContentKind.ShaderPack, ContentKind.DataPack, ContentKind.Modpack];

    public ObservableCollection<ProjectCard> Results { get; } = [];
    public ObservableCollection<InstanceChoice> Instances { get; } = [];
    public ObservableCollection<NamedChoice> Categories { get; } = [];
    public ObservableCollection<NamedChoice> GameVersions { get; } = [];

    /// <remarks>An ObservableCollection: the only kind of list that reliably crosses into XAML under Native AOT.</remarks>
    public ObservableCollection<NamedChoice> Loaders { get; } =
    [
        new(null, Loc.Get("Browse_AnyLoader")),
        new("fabric", "Fabric"),
        new("quilt", "Quilt"),
        new("forge", "Forge"),
        new("neoforge", "NeoForge"),
        new("liteloader", "LiteLoader"),
    ];

    [ObservableProperty] public partial int PlatformIndex { get; set; }
    [ObservableProperty] public partial int KindIndex { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial int CategoryIndex { get; set; }
    [ObservableProperty] public partial int GameVersionIndex { get; set; }
    [ObservableProperty] public partial int LoaderIndex { get; set; }
    [ObservableProperty] public partial int InstanceIndex { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial string? ResultSummary { get; set; }
    [ObservableProperty] public partial string? TargetHint { get; set; }

    /// <summary>A search came back empty: say so, say why it may be, and offer to clear what can be cleared.</summary>
    [ObservableProperty] public partial bool IsNoResults { get; set; }
    [ObservableProperty] public partial string? NoResultsText { get; set; }
    [ObservableProperty] public partial string? NoResultsHint { get; set; }
    [ObservableProperty] public partial bool HasClearableFilters { get; set; }

    /// <summary>The search text and category; the version and loader too unless they come from the chosen instance.</summary>
    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        _initializing = true;
        try
        {
            Query = "";
            CategoryIndex = 0;
            if (TargetInstance is null)
            {
                GameVersionIndex = 0;
                LoaderIndex = 0;
            }
        }
        finally
        {
            _initializing = false;
        }
        await SearchAsync();
    }

    private void UpdateNoResults(bool empty)
    {
        IsNoResults = empty;
        if (!empty)
            return;
        NoResultsText = Query.Trim() is { Length: > 0 } q ? Loc.Format("Browse_NoResultsFor", q) : Loc.Get("Browse_NoResults");
        NoResultsHint = TargetInstance is { } i ? Loc.Format("Browse_NoResultsForInstance", i.Name) : null;
        HasClearableFilters = Query.Trim().Length > 0 || CategoryIndex > 0 || (TargetInstance is null && (GameVersionIndex > 0 || LoaderIndex > 0));
    }

    /// <summary>Kinds with a loader filter.</summary>
    public bool IsModKind => Kind is ContentKind.Mod or ContentKind.Modpack;

    /// <summary>Modpacks install as new instances; there's no target instance.</summary>
    public bool IsModpackKind => Kind == ContentKind.Modpack;
    public ContentKind Kind => Kinds[Math.Clamp(KindIndex, 0, Kinds.Count - 1)];
    public GameInstance? TargetInstance => InstanceIndex > 0 && InstanceIndex < Instances.Count ? Instances[InstanceIndex].Instance : null;
    private IContentPlatform Platform => content.Platforms[Math.Clamp(PlatformIndex, 0, content.Platforms.Count - 1)];

    public async Task InitializeAsync(BrowseContext? context)
    {
        if (_initialized && context is null)
        {
            await RefreshInstalledAsync();
            return;
        }
        _initializing = true;
        try
        {
            PlatformIndex = settings.Current.BrowsePlatform == nameof(ContentPlatform.CurseForge) ? 1 : 0;
            if (context is not null)
            {
                KindIndex = Math.Max(0, Kinds.ToList().IndexOf(context.Kind));
                Query = context.Query ?? "";
            }

            // Clearing makes the ComboBox push SelectedIndex = -1 back through the TwoWay binding, so remember the
            // target by id and restore it (or fall back to "no instance") instead of trusting InstanceIndex.
            var wanted = context?.InstanceId ?? TargetInstance?.Id;
            Instances.Clear();
            Instances.Add(new InstanceChoice(null, Loc.Get("Browse_NoInstance")));
            foreach (var i in (await layout.GetInstancesAsync()).OrderByDescending(i => i.Manifest.Stats.LastPlayed ?? i.Manifest.Created))
                Instances.Add(new InstanceChoice(i, $"{i.Name} ({InstanceItem.DescribeVersion(i.Manifest)})"));
            var index = wanted is null ? -1 : Instances.ToList().FindIndex(c => c.Instance?.Id == wanted);
            InstanceIndex = Math.Max(index, 0);
            OnPropertyChanged(nameof(InstanceIndex)); // Re-sync the ComboBox even when the value did not change.

            if (GameVersions.Count == 0)
            {
                GameVersions.Add(new NamedChoice(null, Loc.Get("Browse_AnyVersion")));
                try
                {
                    var manifest = await versions.GetManifestAsync();
                    foreach (var v in manifest.Versions.Where(v => v.Type == VersionTypes.Release).OrderByDescending(v => v.ParsedReleaseTime))
                        GameVersions.Add(new NamedChoice(v.Id, v.Id));
                }
                catch (HttpRequestException)
                {
                }
            }
            ApplyInstanceFilters();
        }
        finally
        {
            _initializing = false;
            _initialized = true;
        }
        await LoadCategoriesAsync();
        await RefreshInstalledAsync();
        await SearchAsync();
    }

    /// <summary>Filters follow the target instance: its Minecraft version and loader.</summary>
    private void ApplyInstanceFilters()
    {
        var instance = TargetInstance;
        if (instance is null)
        {
            TargetHint = null;
            return;
        }
        var mc = instance.Manifest.MinecraftVersion;
        var vi = GameVersions.ToList().FindIndex(v => v.Id == mc);
        if (vi < 0 && mc is not null)
        {
            GameVersions.Insert(1, new NamedChoice(mc, mc));
            vi = 1;
        }
        GameVersionIndex = Math.Max(0, vi);
        var loader = ContentService.ModLoaderNames(instance.Manifest) is [var first, ..] ? first : null;
        LoaderIndex = Math.Max(0, Loaders.IndexOf(Loaders.FirstOrDefault(l => l.Id == loader) ?? Loaders[0]));
        TargetHint = Loc.Format("Browse_TargetHint", instance.Name);
    }

    partial void OnPlatformIndexChanged(int value)
    {
        settings.Current.BrowsePlatform = Platform.Platform.ToString();
        settings.Save();
        if (!_initializing)
            _ = ReloadAllAsync();
    }

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsModKind));
        OnPropertyChanged(nameof(IsModpackKind));
        if (!_initializing)
            _ = ReloadAllAsync();
    }

    partial void OnInstanceIndexChanged(int value)
    {
        if (_initializing)
            return;
        _initializing = true;
        ApplyInstanceFilters();
        _initializing = false;
        _ = RefreshInstalledAsync().ContinueWith(_ => SearchAsync(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    partial void OnQueryChanged(string value)
    {
        if (!_initializing)
            _ = DebouncedSearchAsync();
    }

    partial void OnSortIndexChanged(int value) => SearchIfReady();
    partial void OnCategoryIndexChanged(int value) => SearchIfReady();
    partial void OnGameVersionIndexChanged(int value) => SearchIfReady();
    partial void OnLoaderIndexChanged(int value) => SearchIfReady();

    private void SearchIfReady()
    {
        if (!_initializing)
            _ = SearchAsync();
    }

    private async Task ReloadAllAsync()
    {
        await LoadCategoriesAsync();
        await RefreshInstalledAsync();
        await SearchAsync();
    }

    private async Task DebouncedSearchAsync()
    {
        var query = Query;
        await Task.Delay(350);
        if (query == Query)
            await SearchAsync();
    }

    private async Task LoadCategoriesAsync()
    {
        var wasInitializing = _initializing;
        _initializing = true;
        try
        {
            Categories.Clear();
            Categories.Add(new NamedChoice(null, Loc.Get("Browse_AllCategories")));
            CategoryIndex = 0;
            try
            {
                foreach (var c in await Platform.GetCategoriesAsync(Kind))
                    Categories.Add(new NamedChoice(c.Id, c.Name));
            }
            catch (HttpRequestException)
            {
            }
        }
        finally
        {
            _initializing = wasInitializing;
        }
    }

    public async Task RefreshInstalledAsync()
    {
        _installed = TargetInstance is { } instance ? await installer.InstalledProjectsAsync(instance, Kind) : [];
        foreach (var card in Results)
        {
            card.IsInstalled = _installed.Contains(card.Key);
            card.IsInstalling = TargetInstance is { } i && installer.ActiveJob(card.Key, i) is not null;
        }
    }

    private ContentTarget Target
    {
        get
        {
            var mc = GameVersionIndex > 0 && GameVersionIndex < GameVersions.Count ? GameVersions[GameVersionIndex].Id : null;
            IReadOnlyList<string> loaders = [];
            if (IsModKind && LoaderIndex > 0)
            {
                var chosen = Loaders[LoaderIndex].Id!;
                // The instance's own loader stands for everything it can run (Quilt runs Fabric mods…).
                loaders = TargetInstance is { } i && ContentService.ModLoaderNames(i.Manifest) is [var first, ..] all && first == chosen ? all : [chosen];
            }
            return new ContentTarget(Kind, mc, loaders);
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        Results.Clear();
        HasMore = false;
        await LoadPageAsync(0);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (HasMore && !IsLoading)
            await LoadPageAsync(Results.Count);
    }

    private async Task LoadPageAsync(int offset)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        if (offset == 0)
            UpdateNoResults(false);
        try
        {
            var sort = (ContentSort)Math.Clamp(SortIndex, 0, 4);
            var query = new ContentSearchQuery(Target, Query, sort, offset, PageSize)
            {
                Category = CategoryIndex > 0 && CategoryIndex < Categories.Count ? Categories[CategoryIndex].Id : null,
            };
            var result = await Platform.SearchAsync(query, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            foreach (var p in result.Hits.Where(p => !Results.Any(r => r.Project.Id == p.Id)))
            {
                var card = new ProjectCard(p) { IsInstalled = _installed.Contains(ContentInstallService.Key(p.Platform, p.Id)) };
                Results.Add(card);
            }
            HasMore = result.Hits.Count > 0 && Results.Count < result.TotalHits;
            ResultSummary = Loc.Format("Browse_ResultCount", Formats.Count(result.TotalHits));
            UpdateNoResults(Results.Count == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (!cts.IsCancellationRequested)
                Error = Loc.Format("Browse_Failed", ex.Message);
        }
        finally
        {
            if (_search == cts)
                IsLoading = false;
        }
    }

    public async Task InstallAsync(ProjectCard card)
    {
        if (IsModpackKind)
        {
            card.IsInstalling = true;
            try
            {
                await App.Get<ModpackInstallService>().InstallProjectAsync(card.Project, null);
            }
            finally
            {
                card.IsInstalling = false;
            }
            return;
        }
        if (TargetInstance is not { } instance)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Browse_PickInstanceTitle"), Loc.Get("Browse_PickInstance"));
            return;
        }
        card.IsInstalling = true;
        try
        {
            if (await installer.InstallAsync(instance, card.Project, null, Kind))
                card.IsInstalled = true;
        }
        finally
        {
            card.IsInstalling = false;
        }
    }

    public ProjectNavigation NavigationFor(ProjectCard card) => new(card.Project, TargetInstance, Kind);
}
