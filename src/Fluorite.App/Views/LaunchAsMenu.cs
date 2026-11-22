using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Auth.Accounts;
using Fluorite.Core.Instances;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Views;

/// <summary>
/// "Launch as": one entry per account, filled each time the menu opens so it always matches the Accounts page.
/// The account a plain Play would use (the instance's default, else the selected one) is checked.
/// </summary>
internal static class LaunchAsMenu
{
    public static async Task FillAsync(IList<MenuFlyoutItemBase> items, GameInstance instance, Action<string> launch)
    {
        var service = App.Get<AccountService>();
        var accounts = await service.GetAccountsAsync();
        var preferred = instance.Manifest.Settings.DefaultAccountId is { } id && accounts.Any(a => a.Id == id)
            ? id
            : (await service.GetSelectedAsync())?.Id;

        items.Clear();
        foreach (var account in accounts)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"{account.PlayerName}  ·  {AccountItem.KindOf(account)}",
                Icon = account.Id == preferred ? new FontIcon { Glyph = "" } : null,
            };
            var accountId = account.Id;
            item.Click += (_, _) => launch(accountId);
            items.Add(item);
        }
        if (accounts.Count == 0)
            items.Add(new MenuFlyoutItem { Text = Loc.Get("Menu_NoAccounts"), IsEnabled = false });

        items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = Loc.Get("Menu_ManageAccounts"), Icon = new FontIcon { Glyph = "" } };
        manage.Click += (_, _) => App.Window?.NavigateTo("Accounts");
        items.Add(manage);
    }

    /// <summary>For an instance card's context menu: fills its (only) "Launch as" submenu, whose Tag is the card's item.</summary>
    public static async Task FillSubmenuAsync(object? flyout, Action<InstanceItem, string> launch)
    {
        if (flyout is MenuFlyout menu && menu.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault() is { Tag: InstanceItem item } sub)
            await FillAsync(sub.Items, item.Instance, accountId => launch(item, accountId));
    }
}
