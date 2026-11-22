using Fluorite.App.ViewModels;
using Fluorite.Auth.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

/// <summary>An account's skin, cape and name, and the skin library.</summary>
public sealed partial class SkinPage : Page
{
    public SkinPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SkinViewModel.Preview))
                Model.SetSkin(ViewModel.Preview?.Skin, ViewModel.Preview?.Slim ?? false, ViewModel.Preview?.Cape);
        };
    }

    public SkinViewModel ViewModel { get; } = App.Get<SkinViewModel>();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is Account account)
            await ViewModel.InitializeAsync(account);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.Release();

    private void Walk_Click(object sender, RoutedEventArgs e) => Model.Animate = WalkToggle.IsChecked == true;

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is not { } window)
            return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add(".png");
        if ((await picker.PickSingleFileAsync())?.Path is { } path)
            await ViewModel.UseFileAsync(path);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e) => await ViewModel.ResetAsync();

    private async void ChangeName_Click(object sender, RoutedEventArgs e) => await ViewModel.ChangeNameAsync();

    private async void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(ViewModel.ManageUrl, UriKind.Absolute, out var uri))
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async void Library_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibrarySkinItem item && !ViewModel.IsBusy)
            await ViewModel.UseLibrarySkinAsync(item);
    }

    private void RemoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<LibrarySkinItem>(sender) is { } item)
            ViewModel.RemoveLibrarySkin(item);
    }
}
