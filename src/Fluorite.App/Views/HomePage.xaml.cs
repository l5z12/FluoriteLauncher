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

    /// <summary>A new instance of the card's version: the Library's new-instance dialog, with it picked.</summary>
    private void VersionCard_Click(object sender, RoutedEventArgs e)
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

    private void VersionPatchNotes_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PatchNoteFor(VersionOf(sender)) is { } note)
            Frame.Navigate(typeof(PatchNotesPage), note, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
    }

    private void VersionCopy_Click(object sender, RoutedEventArgs e)
    {
        if (VersionOf(sender) is not { } version)
            return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(version);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        // Under the card: the menu has closed.
        CopiedFeedback.Show(CardOf(sender));
    }

    private void News_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<NewsCard>(sender) is { Link: { } link })
            _ = Windows.System.Launcher.LaunchUriAsync(link);
    }

    private void GoAccounts_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("Accounts");

    private void GoLibrary_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("Library");
}
