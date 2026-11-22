using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ProjectCard>(sender) is { } card)
            await ViewModel.InstallAsync(card);
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (Scroller.ScrollableHeight - Scroller.VerticalOffset < 600 && ViewModel.HasMore && !ViewModel.IsLoading)
            ViewModel.LoadMoreCommand.Execute(null);
    }
}
