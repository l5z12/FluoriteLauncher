using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

public sealed partial class AccountsPage : Page
{
    public AccountsPage()
    {
        InitializeComponent();
        // Also keys the row handled for itself; these act only on a focused card, not on a button inside it.
        AccountList.AddHandler(KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(AccountList_KeyDown), handledEventsToo: true);
    }

    /// <summary>Alt+Up / Alt+Down move the focused account, as the menu says; focus moves with it.</summary>
    private async void AccountList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (ItemKeys.FocusedRow(AccountList) is not { } row || Bind.ItemOf<AccountItem>(row) is not { } item)
            return;
        var delta = ItemKeys.Chord(e) switch
        {
            ItemKeys.MoveUp => -1,
            ItemKeys.MoveDown => +1,
            _ => 0,
        };
        if (delta == 0)
            return;
        e.Handled = true;
        await ViewModel.MoveAsync(item, delta);
        ItemKeys.FocusRow(AccountList, item);
    }

    public AccountsViewModel ViewModel { get; } = App.Get<AccountsViewModel>();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private async void YggSignIn_Click(object sender, RoutedEventArgs e)
    {
        // The password goes straight from the box to the server; the view model never holds it.
        if (await ViewModel.SignInYggdrasilAsync(YggPassword.Password))
            YggPassword.Password = "";
    }

    private void OfflineName_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ViewModel.AddOfflineCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.AddOfflineCommand.Execute(null);
        }
    }

    private void YggPassword_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !ViewModel.IsYggdrasilBusy)
        {
            e.Handled = true;
            YggSignIn_Click(sender, e);
        }
    }

    private async void YggRegister_Click(object sender, RoutedEventArgs e) => await ViewModel.OpenYggdrasilRegisterAsync();

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<AccountItem>(sender) is { } item)
            ViewModel.SelectCommand.Execute(item);
    }

    private async void Accounts_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            await ViewModel.SaveOrderAsync();
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<AccountItem>(sender) is { } item)
        {
            await ViewModel.MoveAsync(item, -1);
            ItemKeys.FocusRow(AccountList, item);
        }
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<AccountItem>(sender) is { } item)
        {
            await ViewModel.MoveAsync(item, +1);
            ItemKeys.FocusRow(AccountList, item);
        }
    }

    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<AccountItem>(sender) is { } item)
            App.Window?.NavigateToSkin(item.Account);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<AccountItem>(sender) is { } item)
            ViewModel.RemoveCommand.Execute(item);
    }
}
