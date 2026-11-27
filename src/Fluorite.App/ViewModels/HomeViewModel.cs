using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Auth.Accounts;
using Fluorite.Core.Instances;
using Fluorite.Core.News;
using Fluorite.Core.Versions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.ViewModels;

/// <summary>A patch-notes or news card. Images are created on the UI thread.</summary>
public sealed class NewsCard
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? Text { get; init; }

    /// <summary>Null when the entry has no picture.</summary>
    public BitmapImage? Image { get; init; }
    public PatchNoteEntry? PatchNote { get; init; }

    /// <summary>The article on minecraft.net: Open in browser, Copy link.</summary>
    public Uri? Link { get; init; }
    public bool HasLink => Link is not null;

    /// <summary>A patch note's version on the Minecraft Wiki: Copy wiki link.</summary>
    public Uri? WikiLink { get; init; }
    public bool HasWikiLink => WikiLink is not null;
}

public sealed partial class HomeViewModel(
    InstanceRegistry layout, VersionRepository versions, AccountService accounts, GameRunner runner, MojangNewsClient news, SettingsService settings,
    ILogger<HomeViewModel> logger) : ObservableObject
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

    /// <summary>
    /// "You don't have any instances yet": once the instances were read and none are there to show. Not while they're
    /// being read, nor when they couldn't be (that would tell someone with instances to make a first one).
    /// </summary>
    [ObservableProperty] public partial bool ShowNoInstances { get; set; }

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Thrown out of Home's OnNavigatedTo (async void), anything left would end the app: what's shown stays.
            logger.LogError(ex, "Loading Home failed");
        }
        finally
        {
            _firstLoad.TrySetResult();
        }
    }

    /// <summary>How long F5 waits for the network before keeping what's shown.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// F5: the latest versions and the news feeds fetched again, not this session's copy. What's shown stays until
    /// the new copy is in; offline, the cached one does.
    /// </summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadCoreAsync(refresh: true);

    private async Task LoadCoreAsync(bool refresh = false)
    {
        // A refresh gives up after a while (the HttpClient never does), so one stalled fetch can't keep F5 busy for
        // good. Not disposed: a news load it started may still be checking it after this returns.
        var ct = refresh ? new CancellationTokenSource(RefreshTimeout).Token : default;
        CardAction = settings.Current.VersionCardClick;
        // Started first: the network is the slow part, and it needn't wait for the local reads below.
        IsLoadingVersions = LatestRelease is null;
        var manifestTask = versions.GetManifestAsync(refresh, ct);

        // Each part on its own: one that can't be read leaves what it showed, and the rest still loads (the version
        // rings stop, the feeds come in).
        try
        {
            var account = await accounts.GetSelectedAsync();
            NeedsAccount = account is null;
            Greeting = account is null ? Loc.Get("Home_GreetingAnonymous") : Loc.Format("Home_Greeting", account.PlayerName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Reading the account for Home failed");
            if (Greeting.Length == 0)
                Greeting = Loc.Get("Home_GreetingAnonymous"); // Not a blank heading.
        }

        try
        {
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
            ShowNoInstances = !HasRecent;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Reading the instances for Home failed");
        }

        // Feeds change rarely: loaded once per session, again on F5, and again on the next visit when the last load
        // found nothing (offline with no cache). A load still running is shared, F5's included, so two never fetch
        // (and write the cache) at once. Started before the versions are awaited, so both are fetched at once.
        if (_newsLoad is { IsCompleted: true } && (refresh || !HasNews))
            _newsLoad = null;
        if (_newsLoad is null)
        {
            IsLoadingNews = !HasNews;
            _newsLoad = LoadNewsAsync(ct);
        }
        var newsLoad = _newsLoad; // Kept here: another load may replace the field while this one waits.

        try
        {
            var manifest = await manifestTask;
            LatestRelease = manifest.Latest?.Release;
            LatestSnapshot = manifest.Latest?.Snapshot;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline with nothing cached, a timeout, no source with a usable manifest: what an earlier visit found
            // stays; with nothing found yet, the cards go.
            logger.LogWarning(ex, "Loading the latest versions for Home failed");
        }
        finally
        {
            IsLoadingVersions = false;
        }

        try
        {
            await newsLoad.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // F5 waited long enough: what's shown stays. A load that was already running carries on.
        }
    }

    /// <summary>Never throws: when the feeds can't be had, what's shown stays.</summary>
    private async Task LoadNewsAsync(CancellationToken ct)
    {
        try
        {
            var notesTask = news.GetPatchNotesAsync(ct);
            var newsTask = news.GetJavaNewsAsync(ct);
            await Task.WhenAll(notesTask, newsTask);
            ShowNews(await notesTask, await newsTask);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Timed out (F5), a cache that can't be read or written: F5, or the next visit when nothing is shown,
            // tries again. Thrown from here it would end the app on the next visit, which awaits this same task.
            logger.LogWarning(ex, "Loading the news feeds failed");
        }
        finally
        {
            IsLoadingNews = false;
        }
    }

    private void ShowNews(IReadOnlyList<PatchNoteEntry> notes, IReadOnlyList<NewsEntry> articles)
    {
        // Offline with the cache gone since the cards were shown: nothing to show doesn't take them away.
        if (notes.Count == 0 && articles.Count == 0)
            return;

        // Refreshed (F5), the cards already shown keep their pictures instead of going blank while they load again.
        var noteImage = ImagesOf(PatchNotes);
        var newsImage = ImagesOf(News);

        // Every card made before any is shown: something failing partway leaves the cards from before, not half a list.
        var chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var noteCards = notes.DistinctBy(n => n.Id).Take(8).Select(n => new NewsCard
        {
            Title = n.Title,
            Subtitle = (n.Type == "snapshot" ? Loc.Get("News_Snapshot") : Loc.Get("News_Release"))
                + (n.Date is { } date ? " · " + date.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : ""),
            Text = n.ShortText,
            Image = noteImage(MojangNewsClient.ImageUri(n.Image), 280),
            PatchNote = n,
            Link = MojangNewsClient.ArticleUri(n),
            WikiLink = MojangNewsClient.WikiUri(n, chinese),
        }).ToList();
        var newsCards = articles.DistinctBy(n => n.Id).Take(6).Select(n => new NewsCard
        {
            Title = n.Title,
            Subtitle = n.Date ?? "",
            Text = n.Text,
            // The play-page picture when the news-page one is missing or has no address.
            Image = newsImage(MojangNewsClient.ImageUri(n.NewsPageImage) ?? MojangNewsClient.ImageUri(n.PlayPageImage), 400),
            Link = Uri.TryCreate(n.ReadMoreLink, UriKind.Absolute, out var link) ? link : null,
        }).ToList();

        _allPatchNotes = [.. notes];
        PatchNotes.Clear();
        foreach (var card in noteCards)
            PatchNotes.Add(card);
        News.Clear();
        foreach (var card in newsCards)
            News.Add(card);
        HasNews = PatchNotes.Count > 0 || News.Count > 0;
    }

    /// <summary>
    /// Makes a card's picture, reusing the one a card in <paramref name="shown"/> already has for that address. Only
    /// one that loaded: a picture that failed (offline) doesn't try again, and F5 is how it should.
    /// </summary>
    private static Func<Uri?, int, BitmapImage?> ImagesOf(IEnumerable<NewsCard> shown)
    {
        var images = new Dictionary<Uri, BitmapImage>();
        foreach (var card in shown)
        {
            if (card.Image is { UriSource: { } uri, PixelWidth: > 0 } image)
                images.TryAdd(uri, image);
        }
        return (uri, decodeWidth) => uri is null ? null
            : images.TryGetValue(uri, out var image) ? image
            : new(uri) { DecodePixelWidth = decodeWidth };
    }

    [RelayCommand]
    private void Play(InstanceItem item) => PlayAs(item, null);

    /// <summary>Plays as <paramref name="accountId"/> this once ("Launch as"), or as the usual account when null.</summary>
    public void PlayAs(InstanceItem item, string? accountId)
    {
        runner.Launch(item.Instance, accountId: accountId);
        item.IsRunning = true;
    }
}
