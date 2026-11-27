using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        ItemKeys.FocusOnRightPress(this); // Cards: their menus show keys for the focused one.
        ItemKeys.AddRefreshKey(this, () => ViewModel.RefreshCommand);
    }

    public HomeViewModel ViewModel { get; } = App.Get<HomeViewModel>();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item)
            App.Window?.NavigateToInstance(item);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<InstanceItem>(sender) is { } item)
            ViewModel.PlayCommand.Execute(item);
    }

    /// <summary>Ctrl+Enter plays the focused card (Enter is its own click: Open).</summary>
    private void Card_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is UIElement card && ItemKeys.HasFocus(card) && Bind.ItemOf<InstanceItem>(sender) is { IsRunning: false }
            && ItemKeys.Chord(e) == ItemKeys.Play)
        {
            e.Handled = true;
            Play_Click(sender, e);
        }
    }

    private async void InstanceMenu_Opening(object sender, object e) =>
        await LaunchAsMenu.FillSubmenuAsync(sender, ViewModel.PlayAs);

    private void PatchNote_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<NewsCard>(sender) is { PatchNote: { } note })
            Frame.Navigate(typeof(PatchNotesPage), note, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
    }

    /// <summary>The version a latest-version card (or an item of its menu) stands for: by its tag.</summary>
    private string? VersionOf(object sender) =>
        (sender as FrameworkElement)?.Tag as string == "snapshot" ? ViewModel.LatestSnapshot : ViewModel.LatestRelease;

    private Button CardOf(object sender) => (sender as FrameworkElement)?.Tag as string == "snapshot" ? SnapshotCard : ReleaseCard;

    /// <summary>A click on a latest-version card: what Settings › Clicking a version card says.</summary>
    private void VersionCard_Click(object sender, RoutedEventArgs e)
    {
        switch (ViewModel.CardAction)
        {
            case Core.Settings.VersionCardAction.PatchNotes:
                // Until the feed has them (Mojang posts them a little after the version), the menu offers the rest.
                if (!OpenPatchNotes(VersionOf(sender)))
                    CardOf(sender).ContextFlyout?.ShowAt(CardOf(sender));
                break;
            case Core.Settings.VersionCardAction.CopyVersion:
                CopyVersion(sender);
                break;
            default:
                NewInstance(sender);
                break;
        }
    }

    private void VersionNewInstance_Click(object sender, RoutedEventArgs e) => NewInstance(sender);

    /// <summary>The Library's new-instance dialog, with the card's version picked.</summary>
    private void NewInstance(object sender)
    {
        if (VersionOf(sender) is { } version)
            App.Window?.StartNewInstance(version);
    }

    private void VersionMenu_Opening(object sender, object e)
    {
        // Patch notes only when they're among those loaded: Mojang posts them a little after the version.
        ReleasePatchNotesItem.IsEnabled = ViewModel.PatchNoteFor(ViewModel.LatestRelease) is not null;
        SnapshotPatchNotesItem.IsEnabled = ViewModel.PatchNoteFor(ViewModel.LatestSnapshot) is not null;
    }

    private void VersionPatchNotes_Click(object sender, RoutedEventArgs e) => OpenPatchNotes(VersionOf(sender));

    /// <returns>False when the feed has no patch notes for <paramref name="version"/> (yet).</returns>
    private bool OpenPatchNotes(string? version)
    {
        if (ViewModel.PatchNoteFor(version) is not { } note)
            return false;
        Frame.Navigate(typeof(PatchNotesPage), note, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
        return true;
    }

    private void VersionCopy_Click(object sender, RoutedEventArgs e) => CopyVersion(sender);

    /// <summary>The card's version number to the clipboard, with "Copied" under the card.</summary>
    private void CopyVersion(object sender)
    {
        if (VersionOf(sender) is not { } version)
            return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(version);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        // Under the card, also when it came from the menu (which has closed).
        CopiedFeedback.Show(CardOf(sender));
    }

    /// <summary>A news card's click, and Open in browser on any article card: its minecraft.net article.</summary>
    private async void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<NewsCard>(sender) is { } card && await LinkOfAsync(card) is { } link)
            _ = Windows.System.Launcher.LaunchUriAsync(link);
    }

    /// <summary>A card's minecraft.net article: for patch notes, the address the site lists when it can be had.</summary>
    private async Task<Uri?> LinkOfAsync(NewsCard card) =>
        card.PatchNote is { } note ? await ViewModel.ArticleLinkAsync(note) : card.Link;

    /// <summary>The site's addresses are looked up while the menu is open, so they're in when an item is picked.</summary>
    private void PatchNoteMenu_Opening(object sender, object e) => ViewModel.LookUpArticles();

    private void OpenWiki_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<NewsCard>(sender) is { WikiLink: { } link })
            _ = Windows.System.Launcher.LaunchUriAsync(link);
    }

    private async void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<NewsCard>(sender) is { } card && await LinkOfAsync(card) is { } link)
            CopyLink(card, link);
    }

    private void CopyWikiLink_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<NewsCard>(sender) is { WikiLink: { } link } card)
            CopyLink(card, link);
    }

    /// <summary>
    /// Ctrl+C copies the focused patch-notes or news card's link. Enter is the card's own click: the patch notes, or
    /// the news article in the browser.
    /// </summary>
    private async void ArticleCard_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is UIElement element && ItemKeys.HasFocus(element) && Bind.ItemOf<NewsCard>(sender) is { HasLink: true } card
            && ItemKeys.Chord(e) == ItemKeys.Copy)
        {
            e.Handled = true;
            if (await LinkOfAsync(card) is { } link)
                CopyLink(card, link);
        }
    }

    /// <summary>An address to the clipboard (as text and as a link), with "Copied" under the card it's from.</summary>
    private void CopyLink(NewsCard card, Uri link)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(link.AbsoluteUri);
        dp.SetWebLink(link);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        // Under the card, also when it came from the menu (which has closed); not when F5 has replaced it meanwhile.
        var element = ViewModel.PatchNotes.IndexOf(card) is >= 0 and var note ? PatchNotesRepeater.TryGetElement(note)
            : ViewModel.News.IndexOf(card) is >= 0 and var news ? NewsRepeater.TryGetElement(news)
            : null;
        CopiedFeedback.Show(element);
    }

    private void GoAccounts_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("Accounts");

    private void GoLibrary_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("Library");
}
