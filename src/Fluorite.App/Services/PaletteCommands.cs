using Fluorite.App.ViewModels;
using Fluorite.Auth.Accounts;
using Fluorite.Core.Instances;
using Fluorite.Modding.Content;

namespace Fluorite.App.Services;

/// <summary>What the command palette offers: instances, pages, browsing, actions, accounts and settings.</summary>
internal static class PaletteCommands
{
    /// <summary>Settings cards and expanders on the Settings page, by x:Uid (their header is the entry's title).</summary>
    private static readonly string[] s_settings =
    [
        "Settings_MemoryAuto", "Settings_Resolution", "Settings_JvmArgs", "Settings_JvmPreset", "Settings_Gpu",
        "Settings_Priority", "Settings_Java", "Settings_OtherLaunchers", "Settings_Source",
        "Settings_Parallel", "Settings_DownloadDetails", "Settings_OnGameStart", "Settings_CloseToTray",
        "Settings_ShowLogOnCrash", "Settings_NotifyExit", "Settings_DiscordPresence", "Settings_CheckMods",
        "Settings_LoaderAvailability", "Settings_AutoBackup", "Settings_Language", "Settings_Theme", "Settings_LauncherWindow",
        "Settings_DataRoot", "Settings_LogRetention", "Settings_About",
    ];

    private static readonly (string Tag, string Glyph)[] s_pages =
    [
        ("Home", ""), ("Library", ""), ("Browse", ""),
        ("Activity", ""), ("Accounts", ""), ("Settings", ""),
    ];

    private static readonly (ContentKind Kind, string Key)[] s_browse =
    [
        (ContentKind.Mod, "Palette_BrowseMods"), (ContentKind.Modpack, "Palette_BrowseModpacks"),
        (ContentKind.ResourcePack, "Palette_BrowseResourcePacks"), (ContentKind.ShaderPack, "Palette_BrowseShaders"),
        (ContentKind.DataPack, "Palette_BrowseDataPacks"),
    ];

    public static async Task<List<PaletteCommand>> BuildAsync(MainWindow window)
    {
        var list = new List<PaletteCommand>();
        var runner = App.Get<GameRunner>();
        var settings = App.Get<SettingsService>().Current;

        // Instances, most recently played first; the first few are suggested.
        IReadOnlyList<GameInstance> instances;
        try
        {
            instances = await App.Get<InstanceRegistry>().GetInstancesAsync();
        }
        catch (IOException)
        {
            instances = [];
        }
        var recent = 0;
        foreach (var instance in instances.OrderByDescending(i => i.Manifest.Stats.LastPlayed ?? i.Manifest.Created))
        {
            var version = InstanceItem.DescribeVersion(instance.Manifest);
            var id = instance.Id;
            if (runner.Find(instance) is { } game)
            {
                list.Add(new PaletteCommand("", Loc.Format("Palette_Stop", instance.Name), version, () =>
                {
                    game.Stop();
                    return Task.CompletedTask;
                }) { Keywords = instance.Name, Suggested = true });
            }
            else
            {
                list.Add(new PaletteCommand("", Loc.Format("Palette_Play", instance.Name), version,
                    () => window.OpenInstanceAsync(id, launch: true)) { Keywords = instance.Name, Suggested = !settings.IsHidden(instance.Manifest) && recent++ < 3 });
            }
            list.Add(new PaletteCommand("", Loc.Format("Palette_Open", instance.Name), version,
                () => window.OpenInstanceAsync(id, launch: false)) { Keywords = instance.Name });
            list.Add(new PaletteCommand("", Loc.Format("Palette_OpenFolder", instance.Name), version, () =>
            {
                OpenFolder(instance.GameDirectory);
                return Task.CompletedTask;
            }) { Keywords = instance.Name });
        }

        // Actions.
        list.Add(new PaletteCommand("", Loc.Get("Palette_NewInstance"), Loc.Get("Palette_Action"),
            () => Go(window, "Library", "NewInstance")) { Suggested = true });
        list.Add(new PaletteCommand("", Loc.Get("Palette_ImportFile"), Loc.Get("Palette_Action"),
            () => Go(window, "Library", "ImportFile")) { Keywords = ".mrpack zip modpack" });
        list.Add(new PaletteCommand("", Loc.Get("Palette_ImportLink"), Loc.Get("Palette_Action"),
            () => Go(window, "Library", "ImportLink")) { Keywords = "url modpack" });
        list.Add(new PaletteCommand("", Loc.Get("Palette_ImportLaunchers"), Loc.Get("Palette_Action"),
            () => App.Get<OtherLaunchersService>().ShowImportDialogAsync()) { Keywords = "Prism MultiMC HMCL PCL CurseForge Modrinth" });
        list.Add(new PaletteCommand("", Loc.Get("Palette_AddAccount"), Loc.Get("Palette_Action"),
            () => Go(window, "Accounts", null)) { Keywords = "Microsoft sign in login" });
        list.Add(new PaletteCommand("", Loc.Get("Palette_OpenDataFolder"), Loc.Get("Palette_Action"), () =>
        {
            OpenFolder(AppPaths.DataRoot);
            return Task.CompletedTask;
        }));
        list.Add(new PaletteCommand("", Loc.Get("Palette_Flags"), Loc.Get("Palette_Action"), () => window.ShowFlagsAsync())
        {
            Keywords = "--flags command line debug developer welcome fresh",
        });
        list.Add(new PaletteCommand("", Loc.Get("Palette_Welcome"), Loc.Get("Palette_Action"), () => window.ShowWelcomeIfFirstRunAsync(force: true))
        {
            Keywords = "first run start",
        });
        // Accounts to switch to.
        try
        {
            var accounts = App.Get<AccountService>();
            var selected = await accounts.GetSelectedAsync();
            foreach (var account in await accounts.GetAccountsAsync())
            {
                if (account.Id == selected?.Id)
                    continue;
                var accountId = account.Id;
                var kind = Loc.Get(account.Kind switch
                {
                    AccountKind.Microsoft => "Account_KindMicrosoft",
                    AccountKind.Yggdrasil => "Account_KindYggdrasil",
                    _ => "Account_KindOffline",
                });
                list.Add(new PaletteCommand("", Loc.Format("Palette_UseAccount", account.PlayerName), kind, async () =>
                {
                    await accounts.SelectAsync(accountId);
                    await App.Get<AccountsViewModel>().LoadAsync();
                }) { Keywords = account.PlayerName });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // The account store couldn't be read: no account entries, everything else still works.
        }

        // Pages, and browsing by kind.
        foreach (var (tag, glyph) in s_pages)
        {
            list.Add(new PaletteCommand(glyph, Loc.Get($"Nav{tag}/Content"), Loc.Get("Palette_Page"),
                () => Go(window, tag, null)) { Suggested = true });
        }
        foreach (var (kind, key) in s_browse)
            list.Add(new PaletteCommand("", Loc.Get(key), Loc.Get("NavBrowse/Content"), () => Go(window, "Browse", new BrowseContext(null, kind))));

        // Settings, each opened in place.
        foreach (var uid in s_settings)
        {
            var title = Loc.Get(uid + "/Header");
            var description = Loc.Get(uid + "/Description");
            list.Add(new PaletteCommand("", title, Loc.Get("NavSettings/Content"), () => Go(window, "Settings", "Setting:" + uid))
            {
                Keywords = description == uid + "/Description" ? null : description,
            });
        }
        return list;
    }

    /// <summary>Entries that use what was typed: search mods or modpacks for it.</summary>
    public static IEnumerable<PaletteCommand> ForQuery(MainWindow window, string query)
    {
        yield return new PaletteCommand("", Loc.Format("Palette_SearchMods", query), Loc.Get("NavBrowse/Content"),
            () => Go(window, "Browse", new BrowseContext(null, ContentKind.Mod, query)));
        yield return new PaletteCommand("", Loc.Format("Palette_SearchModpacks", query), Loc.Get("NavBrowse/Content"),
            () => Go(window, "Browse", new BrowseContext(null, ContentKind.Modpack, query)));
    }

    private static Task Go(MainWindow window, string tag, object? parameter)
    {
        window.NavigateTo(tag, parameter);
        return Task.CompletedTask;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The folder is gone (deleted, drive unplugged): nothing to open.
        }
    }
}
