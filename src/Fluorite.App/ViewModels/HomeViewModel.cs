using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Auth.Accounts;
using Fluorite.Core.Instances;
using Fluorite.Core.News;
using Fluorite.Core.Versions;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.ViewModels;

/// <summary>A patch-notes or news card. Images are created on the UI thread.</summary>
public sealed class NewsCard
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? Text { get; init; }
    public required BitmapImage Image { get; init; }
    public PatchNoteEntry? PatchNote { get; init; }
    public Uri? Link { get; init; }
}

public sealed partial class HomeViewModel(
    InstanceRegistry layout, VersionRepository versions, AccountService accounts, GameRunner runner, MojangNewsClient news, SettingsService settings) : ObservableObject
{
    public ObservableCollection<InstanceItem> Recent { get; } = [];
    public ObservableCollection<NewsCard> PatchNotes { get; } = [];
    public ObservableCollection<NewsCard> News { get; } = [];

    [ObservableProperty] public partial string Greeting { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVersionCards), nameof(HasLatestRelease), nameof(ReleaseCardTip), nameof(ReleaseCardName))]
    public partial string? LatestRelease { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatestSnapshot), nameof(SnapshotCardTip), nameof(SnapshotCardName))]
    public partial string? LatestSnapshot { get; set; }

    // A version card does something once its version is in: what Settings › Clicking a version card says.
    public bool HasLatestRelease => LatestRelease is not null;
    public bool HasLatestSnapshot => LatestSnapshot is not null;
    public string? ReleaseCardTip => CardTip(LatestRelease);
    public string? SnapshotCardTip => CardTip(LatestSnapshot);

    /// <summary>Settings › Clicking a version card, read again on each visit (Home stays cached).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseCardTip), nameof(SnapshotCardTip), nameof(CardActionGlyph))]
    public partial Core.Settings.VersionCardAction CardAction { get; set; } = settings.Current.VersionCardClick;

    /// <summary>The hint at the card's end: what a click does.</summary>
    public string CardActionGlyph => CardAction switch
    {
        Core.Settings.VersionCardAction.PatchNotes => "",
        Core.Settings.VersionCardAction.CopyVersion => "",
        _ => "",
    };

    private string? CardTip(string? version) => version is null ? null : Loc.Format(CardAction switch
    {
        Core.Settings.VersionCardAction.PatchNotes => "Home_VersionCardTipPatchNotes",
        Core.Settings.VersionCardAction.CopyVersion => "Home_VersionCardTipCopy",
        _ => "Home_VersionCardTip",
    }, version);
    public string ReleaseCardName => Loc.Format("Home_VersionCardName", Loc.Get("Home_LatestRelease/Text"), LatestRelease ?? "");
    public string SnapshotCardName => Loc.Format("Home_VersionCardName", Loc.Get("Home_LatestSnapshot/Text"), LatestSnapshot ?? "");

    /// <summary>The whole patch-notes feed: Home shows the newest few, but the latest release's can be older.</summary>
    private IReadOnlyList<PatchNoteEntry> _allPatchNotes = [];

    /// <summary>The patch notes of <paramref name="version"/>, once the feed has them.</summary>
    public PatchNoteEntry? PatchNoteFor(string? version) =>
        version is null ? null : _allPatchNotes.FirstOrDefault(n => n.Version == version);
    [ObservableProperty] public partial bool HasRecent { get; set; }
    [ObservableProperty] public partial bool NeedsAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewsHeaders), nameof(ShowNewsLoading))]
    public partial bool HasNews { get; set; }

    /// <summary>The latest versions are on their way (the first time, or again after failing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVersionCards))]
    public partial bool IsLoadingVersions { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewsHeaders), nameof(ShowNewsLoading))]
    public partial bool IsLoadingNews { get; set; } = true;

    /// <summary>
    /// The latest-version cards: once there's a version to show, and while loading unless the setting hides them.
    /// Never empty after loading: offline with nothing cached, they go.
    /// </summary>
    public bool ShowVersionCards => LatestRelease is not null || IsLoadingVersions && ShowsLoading;

    public bool ShowNewsHeaders => HasNews || IsLoadingNews && ShowsLoading;

    /// <summary>A progress ring under "Patch notes" while the feeds load.</summary>
    public bool ShowNewsLoading => !HasNews && IsLoadingNews && ShowsLoading;

    /// <summary>
    /// Waiting shows as a ring, except with "Hide the cards". With a launch window, Home is usually ready before it's
    /// seen; if it isn't (the window waits only so long), the rest shows as rings too.
    /// </summary>
    private bool ShowsLoading => settings.Current.HomeLoading != Core.Settings.HomeLoadingStyle.Hide;

    /// <summary>Completes after the first load (versions and the news feeds; not their images), however it ended.</summary>
    public Task FirstLoad => _firstLoad.Task;
    private readonly TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _newsLoad;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            await LoadCoreAsync();
        }
        finally
        {
            _firstLoad.TrySetResult();
        }
    }

    private async Task LoadCoreAsync()
    {
        CardAction = settings.Current.VersionCardClick;
        // Started first: the network is the slow part, and it needn't wait for the local reads below.
        IsLoadingVersions = LatestRelease is null;
        var manifestTask = versions.GetManifestAsync();
        var account = await accounts.GetSelectedAsync();
        NeedsAccount = account is null;
        Greeting = account is null ? Loc.Get("Home_GreetingAnonymous") : Loc.Format("Home_Greeting", account.PlayerName);

        // Fetch first, then clear+fill with no await in between, so overlapping loads can't interleave duplicates.
        var recent = (await layout.GetInstancesAsync())
            .Where(i => !settings.Current.IsHidden(i.Manifest))
            .OrderByDescending(i => i.Manifest.Stats.LastPlayed ?? i.Manifest.Created)
            .Take(6)
            .Select(i => new InstanceItem(i) { IsRunning = runner.Find(i) is not null })
            .ToList();
        Recent.Clear();
        foreach (var item in recent)
            Recent.Add(item);
        HasRecent = Recent.Count > 0;

        // Feeds change rarely: load once per session; concurrent callers share the same in-flight task. Started before
        // the versions are awaited, so both are fetched at once.
        IsLoadingNews = !HasNews;
        _newsLoad ??= LoadNewsAsync();

        try
        {
            var manifest = await manifestTask;
            LatestRelease = manifest.Latest?.Release;
            LatestSnapshot = manifest.Latest?.Snapshot;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline with nothing cached, a timeout, a garbled manifest: what an earlier visit found stays; with
            // nothing found yet, the cards go.
        }
        finally
        {
            IsLoadingVersions = false;
        }

        try
        {
            await _newsLoad;
        }
        finally
        {
            IsLoadingNews = false;
            if (!HasNews)
                _newsLoad = null; // Offline with no cache: retry on the next visit.
        }
    }

    private async Task LoadNewsAsync()
    {
        var notesTask = news.GetPatchNotesAsync();
        var newsTask = news.GetJavaNewsAsync();
        var notes = await notesTask;
        var articles = await newsTask;

        _allPatchNotes = [.. notes];
        PatchNotes.Clear();
        foreach (var n in notes.DistinctBy(n => n.Id).Take(8))
        {
            PatchNotes.Add(new NewsCard
            {
                Title = n.Title,
                Subtitle = (n.Type == "snapshot" ? Loc.Get("News_Snapshot") : Loc.Get("News_Release")) + " · " + n.Date?.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
                Text = n.ShortText,
                Image = Bitmap(MojangNewsClient.ImageUri(n.Image), 280),
                PatchNote = n,
            });
        }

        News.Clear();
        foreach (var n in articles.DistinctBy(n => n.Id).Take(6))
        {
            News.Add(new NewsCard
            {
                Title = n.Title,
                Subtitle = n.Date ?? "",
                Text = n.Text,
                Image = Bitmap(MojangNewsClient.ImageUri(n.NewsPageImage ?? n.PlayPageImage), 400),
                Link = Uri.TryCreate(n.ReadMoreLink, UriKind.Absolute, out var link) ? link : null,
            });
        }
        HasNews = PatchNotes.Count > 0 || News.Count > 0;
    }

    private static BitmapImage Bitmap(Uri uri, int decodeWidth) => new(uri) { DecodePixelWidth = decodeWidth };

    [RelayCommand]
    private void Play(InstanceItem item) => PlayAs(item, null);

    /// <summary>Plays as <paramref name="accountId"/> this once ("Launch as"), or as the usual account when null.</summary>
    public void PlayAs(InstanceItem item, string? accountId)
    {
        runner.Launch(item.Instance, accountId: accountId);
        item.IsRunning = true;
    }
}
