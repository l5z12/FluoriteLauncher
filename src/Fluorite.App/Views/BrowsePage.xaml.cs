using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

/// <summary>Modrinth/CurseForge search with filters, installing into a chosen instance.</summary>
public sealed partial class BrowsePage : Page
{
    public BrowsePage()
    {
        InitializeComponent();
        ItemKeys.AddRefreshKey(this, () => ViewModel.SearchCommand); // F5: search again.
    }

    /// <summary>Ctrl+F.</summary>
    public void FocusSearch() => SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);

    public BrowseViewModel ViewModel { get; } = App.Get<BrowseViewModel>();

    protected override async void OnNavigatedTo(NavigationEventArgs e) =>
        await ViewModel.InitializeAsync(e.Parameter as BrowseContext);

    private void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        ViewModel.SearchCommand.Execute(null);

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ProjectCard>(sender) is { } card)
            App.Window?.NavigateToProject(ViewModel.NavigationFor(card));
    }

    private async void Install_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (Bind.ItemOf<ProjectCard>(sender) is { } card)
            await ViewModel.InstallAsync(card);
    }

    private async void VersionFlyout_Opening(object sender, object e)
    {
        if (sender is not FlyoutBase flyout)
            return;
        var target = flyout.Target;
        if (target is not null && Bind.ItemOf<ProjectCard>(target) is { } card)
            await ViewModel.LoadVersionsAsync(card);
    }

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<VersionChoice>(sender) is not { } choice)
            return;
        // Close the flyout, then find the card that owns this version.
        CloseFlyoutOf(sender);
        var card = ViewModel.Results.FirstOrDefault(c => c.Versions.Contains(choice));
        if (card is not null)
            await ViewModel.InstallAsync(card, choice.Version);
    }

    private void SeeAllVersions_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyoutOf(sender);
        if (Bind.ItemOf<ProjectCard>(sender) is { } card)
            App.Window?.NavigateToProject(ViewModel.NavigationFor(card));
    }

    private static void CloseFlyoutOf(object sender)
    {
        for (var el = sender as DependencyObject; el is not null; el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el))
        {
            if (el is FlyoutPresenter presenter)
            {
                (presenter.Parent as Popup)?.SetValue(Popup.IsOpenProperty, false);
                return;
            }
        }
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (Scroller.ScrollableHeight - Scroller.VerticalOffset < 600 && ViewModel.HasMore && !ViewModel.IsLoading)
            ViewModel.LoadMoreCommand.Execute(null);
    }
}
