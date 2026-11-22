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

    private void News_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<NewsCard>(sender) is { Link: { } link })
            _ = Windows.System.Launcher.LaunchUriAsync(link);
    }

    private void GoAccounts_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("Accounts");

    private void GoLibrary_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("Library");
}
