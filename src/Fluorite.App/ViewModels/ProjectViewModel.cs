using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.ViewModels;

/// <summary>A row in a project's version list.</summary>
public sealed partial class VersionRow(RemoteVersion version, bool compatible) : ObservableObject
{
    public RemoteVersion Version { get; } = version;
    public string Number => Version.VersionNumber;
    public string Name => Version.Name == Version.VersionNumber ? "" : Version.Name;
    public bool IsCompatible { get; } = compatible;

    public string Channel => Version.Channel switch
    {
        ReleaseChannel.Beta => Loc.Get("Channel_Beta"),
        ReleaseChannel.Alpha => Loc.Get("Channel_Alpha"),
        _ => Loc.Get("Channel_Release"),
    };

    public string Details => string.Join("  ·  ", new[]
    {
        Summarise(Version.GameVersions),
        string.Join(", ", Version.Loaders.Select(Capitalise)),
        Version.Published.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture),
        Version.Downloads > 0 ? Loc.Format("Browse_Downloads", Formats.Count(Version.Downloads)) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty] public partial bool IsInstalled { get; set; }

    private static string Summarise(IReadOnlyList<string> versions) =>
        versions.Count <= 4 ? string.Join(", ", versions) : $"{string.Join(", ", versions.Take(3))} +{versions.Count - 3}";

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

public sealed partial class ProjectViewModel(ContentService content, ContentInstallService installer, InstanceRegistry layout) : ObservableObject
{
    private IReadOnlyList<RemoteVersion> _all = [];
    private ImageSource? _icon;

    public ObservableCollection<VersionRow> Versions { get; } = [];
    public ObservableCollection<InstanceChoice> Instances { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(Subtitle), nameof(Stats))]
    public partial RemoteProject? Project { get; set; }

    [ObservableProperty] public partial ContentKind Kind { get; set; }
    [ObservableProperty] public partial int InstanceIndex { get; set; }
    [ObservableProperty] public partial bool OnlyCompatible { get; set; } = true;
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsInstalling { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? InstalledVersion { get; set; }

    public GameInstance? TargetInstance => InstanceIndex > 0 && InstanceIndex < Instances.Count ? Instances[InstanceIndex].Instance : null;

    public ImageSource? Icon => _icon ??= Project?.IconUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? new BitmapImage(uri) { DecodePixelWidth = 128, DecodePixelType = DecodePixelType.Logical }
        : null;

    public string Subtitle => Project is null ? "" : string.Join("  ·  ", new[] { Project.Author is { Length: > 0 } a ? Loc.Format("Browse_ByAuthor", a) : null, Project.Platform.ToString() }.Where(s => s is not null));

    public string Stats => Project is null ? "" : string.Join("  ·  ", new[] { Loc.Format("Browse_Downloads", Formats.Count(Project.Downloads)), Project.Updated is null ? null : Loc.Format("Browse_Updated", Formats.Ago(Project.Updated)) }.Where(s => s is not null));

    private IContentPlatform Platform => content.Get(Project!.Platform);

    public async Task InitializeAsync(ProjectNavigation nav)
    {
        Project = nav.Project;
        Kind = nav.Kind;
        Instances.Clear();
        Instances.Add(new InstanceChoice(null, Loc.Get("Browse_NoInstance")));
        foreach (var i in (await layout.GetInstancesAsync()).OrderByDescending(i => i.Manifest.Stats.LastPlayed ?? i.Manifest.Created))
            Instances.Add(new InstanceChoice(i, $"{i.Name} ({InstanceItem.DescribeVersion(i.Manifest)})"));
        var index = nav.Instance is { } target ? Instances.ToList().FindIndex(c => c.Instance?.Directory == target.Directory) : -1;
        InstanceIndex = Math.Max(0, index);
        OnlyCompatible = TargetInstance is not null;

        // Search hits and installed-file stubs lack links/body: fetch the full project.
        try
        {
            var full = await Platform.GetProjectAsync(nav.Project.Id);
            _icon = null;
            Project = full with
            {
                Author = full.Author ?? nav.Project.Author,
                IconUrl = full.IconUrl ?? nav.Project.IconUrl,
            };
            if (Project.Kind != Kind && Project.Kind != ContentKind.Modpack)
                Kind = Project.Kind;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            Error = ex.Message;
        }
        await LoadVersionsAsync();
    }

    partial void OnInstanceIndexChanged(int value) => _ = RefreshAsync();

    partial void OnOnlyCompatibleChanged(bool value) => Filter();

    private async Task RefreshAsync()
    {
        await RefreshInstalledAsync();
        Filter();
    }

    public async Task<string?> GetDescriptionHtmlAsync()
    {
        if (Project is null)
            return null;
        try
        {
            var text = await Platform.GetDescriptionAsync(Project);
            if (string.IsNullOrWhiteSpace(text))
                return $"<p>{System.Net.WebUtility.HtmlEncode(Project.Summary ?? "")}</p>";
            return Platform.TextFormat == TextFormat.Markdown ? HtmlContent.FromMarkdown(text) : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            return $"<p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>"; // A timeout is a TaskCanceledException.
        }
    }

    private async Task LoadVersionsAsync()
    {
        if (Project is null)
            return;
        IsLoading = true;
        try
        {
            _all = await Platform.GetVersionsAsync(Project.Id, null);
            await RefreshInstalledAsync();
            Filter();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private ContentTarget? Target => TargetInstance is { } i && !IsModpack ? ContentService.TargetFor(i.Manifest, Kind) : null;

    /// <summary>Modpacks install as new instances, so there's no target instance to pick.</summary>
    public bool IsModpack => Kind == ContentKind.Modpack;

    partial void OnKindChanged(ContentKind value) => OnPropertyChanged(nameof(IsModpack));

    private void Filter()
    {
        Versions.Clear();
        var target = Target;
        foreach (var v in _all)
        {
            var compatible = target is null || IsCompatible(v, target);
            if (OnlyCompatible && target is not null && !compatible)
                continue;
            Versions.Add(new VersionRow(v, compatible) { IsInstalled = v.Id == _installedId });
        }
    }

    private static bool IsCompatible(RemoteVersion v, ContentTarget target) =>
        (target.MinecraftVersion is null || v.GameVersions.Count == 0 || v.GameVersions.Contains(target.MinecraftVersion))
        && (target.Kind != ContentKind.Mod || target.Loaders.Count == 0 || v.Loaders.Count == 0 || v.Loaders.Any(target.Loaders.Contains));

    private string? _installedId;

    private async Task RefreshInstalledAsync()
    {
        _installedId = null;
        InstalledVersion = null;
        if (TargetInstance is not { } instance || Project is null)
            return;
        var records = await content.For(instance).GetRecordsAsync();
        var record = records.Values.FirstOrDefault(r => r.Source is { } s && s.Platform == Project.Platform && s.ProjectId == Project.Id);
        _installedId = record?.Source?.VersionId;
        InstalledVersion = record?.Source?.VersionNumber is { } n ? Loc.Format("Project_InstalledVersion", n) : null;
        foreach (var row in Versions)
            row.IsInstalled = row.Version.Id == _installedId;
    }

    [RelayCommand]
    private async Task InstallAsync(VersionRow? row)
    {
        if (Project is null)
            return;
        if (IsModpack)
        {
            // A modpack becomes a new instance.
            IsInstalling = true;
            try
            {
                await App.Get<ModpackInstallService>().InstallProjectAsync(Project, row?.Version);
            }
            finally
            {
                IsInstalling = false;
            }
            return;
        }
        if (TargetInstance is not { } instance)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Browse_PickInstanceTitle"), Loc.Get("Browse_PickInstance"));
            return;
        }
        IsInstalling = true;
        try
        {
            await installer.InstallAsync(instance, Project, row?.Version, Kind);
            await RefreshInstalledAsync();
        }
        finally
        {
            IsInstalling = false;
        }
    }

    public Task<string?> GetChangelogAsync(VersionRow row) => Platform.GetChangelogAsync(row.Version);

    public TextFormat TextFormat => Platform.TextFormat;
}
