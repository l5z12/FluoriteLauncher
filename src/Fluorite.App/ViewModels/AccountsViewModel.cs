using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Auth.Accounts;
using Fluorite.Auth.Microsoft;
using Fluorite.Auth.Yggdrasil;

namespace Fluorite.App.ViewModels;

public sealed partial class AccountItem(Account account, bool selected) : ObservableObject
{
    public Account Account { get; } = account;
    public string Name => Account.PlayerName;
    public string Initial => string.IsNullOrEmpty(Account.PlayerName) ? "?" : Account.PlayerName[..1].ToUpperInvariant();

    public string KindText => KindOf(Account);

    /// <summary>"Microsoft account", "LittleSkin account", "Offline"…</summary>
    public static string KindOf(Account account) => account.Kind switch
    {
        AccountKind.Microsoft => Loc.Get("Account_KindMicrosoft"),
        AccountKind.Yggdrasil when account.YggdrasilServerName is { Length: > 0 } server => Loc.Format("Account_KindYggdrasilServer", server),
        AccountKind.Yggdrasil => Loc.Get("Account_KindYggdrasil"),
        _ => Loc.Get("Account_KindOffline"),
    };

    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;

    /// <summary>Only signing in again fixes it (its last refresh said so): the card says so, with the button to do it.</summary>
    [ObservableProperty] public partial bool NeedsSignIn { get; set; } = account.NeedsSignIn;

    /// <summary>Microsoft and skin-site accounts; offline ones have no sign-in.</summary>
    public bool CanSignInAgain => Account.Kind != AccountKind.Offline;

    /// <summary>The skin's face; until it loads (or for the default skin) the initial shows.</summary>
    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.ImageSource? Avatar { get; set; }

    public bool HasAvatar => Avatar is not null;

    partial void OnAvatarChanged(Microsoft.UI.Xaml.Media.ImageSource? value) => OnPropertyChanged(nameof(HasAvatar));

    /// <summary>Also the UI Automation name, so screen readers hear "Steve, Microsoft account, sign-in expired".</summary>
    public override string ToString() => NeedsSignIn ? $"{Name}, {KindText}, {Loc.Get("Accounts_NeedsSignIn/Text")}" : $"{Name}, {KindText}";
}

public sealed partial class AccountsViewModel(
    AccountService accounts, AccountSignIn signIn, YggdrasilClient yggdrasil, AccountSkins skins) : ObservableObject
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _signIn;
    private bool _following;

    public ObservableCollection<AccountItem> Accounts { get; } = [];

    /// <summary>Faces from each account's skin: stored URL (Microsoft), skin site lookup (Yggdrasil) or local file.</summary>
    private async Task LoadAvatarsAsync(IReadOnlyList<AccountItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                var a = item.Account;
                var source = a.Kind switch
                {
                    AccountKind.Microsoft => a.SkinUrl,
                    AccountKind.Offline => a.SkinFile,
                    _ => (await skins.GetAsync(a)).SkinUrl,
                };
                if (source is null || await skins.LoadTextureAsync(source) is not { } png)
                    continue;
                var head = await SkinImages.HeadAsync(png, 40);
                if (Accounts.Contains(item))
                    item.Avatar = head;
                else
                    ImageLoader.Release(head);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or AuthException or IOException)
            {
                // The initial stays.
            }
        }
    }

    // ── authlib-injector (Yggdrasil) ─────────────────────────────────────
    /// <summary>Preset servers, then "Other server…". A concrete collection: AOT can only hand those to XAML.</summary>
    public ObservableCollection<string> YggdrasilChoices { get; } =
        [.. YggdrasilPresets.All.Select(p => p.Name), Loc.Get("Accounts_YggCustom")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomYggdrasil))]
    public partial int YggdrasilChoice { get; set; }

    public bool IsCustomYggdrasil => YggdrasilChoice >= YggdrasilPresets.All.Count;

    [ObservableProperty] public partial string YggdrasilAddress { get; set; } = "";
    [ObservableProperty] public partial string YggdrasilLogin { get; set; } = "";
    [ObservableProperty] public partial bool IsYggdrasilBusy { get; set; }

    private string SelectedYggdrasilAddress =>
        IsCustomYggdrasil ? YggdrasilAddress.Trim() : YggdrasilPresets.All[Math.Max(0, YggdrasilChoice)].Address;

    /// <summary>Signs in, lets the user pick a character when there are several, and adds the account. True when added.</summary>
    public async Task<bool> SignInYggdrasilAsync(string password)
    {
        ErrorMessage = null;
        var address = SelectedYggdrasilAddress;
        var login = YggdrasilLogin.Trim();
        if (address.Length == 0 || login.Length == 0 || password.Length == 0)
            return false;
        IsYggdrasilBusy = true;
        try
        {
            var server = await yggdrasil.DiscoverAsync(address);
            var signIn = await accounts.SignInYggdrasilAsync(server, login, password);
            var profile = signIn.Profiles.Count == 1 ? signIn.Profiles[0] : await PickProfileAsync(signIn.Profiles);
            if (profile is null)
                return false;
            await accounts.AddYggdrasilAsync(signIn, profile);
            YggdrasilLogin = "";
            await LoadAsync();
            return true;
        }
        catch (AuthException ex)
        {
            ErrorMessage = DescribeFailure(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            ErrorMessage = Loc.Format("Auth_Network", ex.Message);
        }
        finally
        {
            IsYggdrasilBusy = false;
        }
        return false;
    }

    /// <summary>Opens the registration page of the chosen preset server.</summary>
    public async Task OpenYggdrasilRegisterAsync()
    {
        try
        {
            var server = await yggdrasil.DiscoverAsync(SelectedYggdrasilAddress);
            if ((server.Register ?? server.Homepage) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex) when (ex is AuthException or HttpRequestException or OperationCanceledException)
        {
            ErrorMessage = ex is AuthException a ? DescribeFailure(a) : Loc.Format("Auth_Network", ex.Message);
        }
    }

    private static async Task<YggdrasilProfile?> PickProfileAsync(IReadOnlyList<YggdrasilProfile> profiles)
    {
        var list = new Microsoft.UI.Xaml.Controls.ListView
        {
            ItemsSource = new ObservableCollection<string>(profiles.Select(p => p.Name)),
            SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Single,
            SelectedIndex = 0,
            MaxHeight = 320,
        };
        var content = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 12 };
        content.Children.Add(Dialogs.Text(Loc.Get("Accounts_PickProfileHint"), secondary: true));
        content.Children.Add(list);
        var result = await Dialogs.ShowAsync(Loc.Get("Accounts_PickProfileTitle"), content, Loc.Get("Accounts_PickProfileButton"));
        return result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && list.SelectedIndex >= 0 ? profiles[list.SelectedIndex] : null;
    }

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool OfflineUnlocked { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    /// <summary>What just worked, when nothing else on the page shows it ("Signed in to Steve again.").</summary>
    [ObservableProperty] public partial string? StatusMessage { get; set; }

    [ObservableProperty] public partial string? DeviceCode { get; set; }
    [ObservableProperty] public partial string? DeviceCodeUrl { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineNameProblem))]
    [NotifyCanExecuteChangedFor(nameof(AddOfflineCommand))]
    public partial string OfflineName { get; set; } = "";

    private bool CanAddOffline => OfflineAccounts.IsValidName(OfflineName.Trim());

    /// <summary>
    /// Said as soon as a character that can't work is typed. A name that's only too short so far gets nothing: the
    /// rule is under the box and Add stays off until it fits.
    /// </summary>
    public string? OfflineNameProblem
    {
        get
        {
            var bad = OfflineName.Trim().FirstOrDefault(c => !char.IsAsciiLetterOrDigit(c) && c != '_');
            return bad switch
            {
                '\0' => null,
                ' ' => Loc.Get("Account_OfflineNoSpaces"),
                _ => Loc.Format("Account_OfflineBadChar", bad),
            };
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_following)
        {
            // Lives as long as the app: once is enough, and nothing to let go of.
            accounts.SignInStateChanged += OnSignInStateChanged;
            _following = true;
        }
        StatusMessage = null; // About the list as it was.
        var selected = await accounts.GetSelectedAsync();
        var list = (await accounts.GetAccountsAsync()).Select(a => new AccountItem(a, a.Id == selected?.Id)).ToList();
        foreach (var old in Accounts)
            ImageLoader.Release(old.Avatar);
        Accounts.Clear();
        foreach (var item in list)
            Accounts.Add(item);
        OfflineUnlocked = await accounts.AreOfflineAccountsUnlockedAsync();
        _ = LoadAvatarsAsync(list);
    }

    /// <summary>
    /// Windows' account picker: the Microsoft accounts signed in to Windows, or another one. When Windows can't sign
    /// in for this app, the browser does instead.
    /// </summary>
    [RelayCommand]
    private Task SignInWithMicrosoftAsync() => RunSignInAsync(MicrosoftSignInMethod.Default);

    // The sign-in button's right-click menu: one method each, no fallback.

    [RelayCommand]
    private Task SignInWithWindowsAsync() => RunSignInAsync(MicrosoftSignInMethod.Windows);

    [RelayCommand]
    private Task SignInWithBrowserAsync() => RunSignInAsync(MicrosoftSignInMethod.Browser);

    [RelayCommand]
    private Task SignInWithCodeAsync() => RunSignInAsync(MicrosoftSignInMethod.Code);

    [RelayCommand]
    private void CancelSignIn() => _signIn?.Cancel();

    private async Task RunSignInAsync(MicrosoftSignInMethod method)
    {
        if (IsBusy)
            return; // One sign-in at a time (Cancel ends the current one).
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        _signIn = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var (msa, brokerAccountId) = await signIn.GetTokenAsync(method, (_, code) =>
            {
                if (code is not null)
                {
                    DeviceCode = code.UserCode;
                    DeviceCodeUrl = code.VerificationUri;
                }
            }, _signIn.Token);
            await accounts.AddMicrosoftAsync(msa, brokerAccountId, _signIn.Token);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (AuthException ex)
        {
            ErrorMessage = DescribeFailure(ex);
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = Loc.Format("Auth_Network", ex.Message);
        }
        finally
        {
            IsBusy = false;
            DeviceCode = null;
            _signIn.Dispose();
            _signIn = null;
        }
    }

    public static string DescribeFailure(AuthException ex) => ex.Failure switch
    {
        AuthFailure.InvalidCredentials => Loc.Get("Auth_InvalidCredentials"),
        AuthFailure.NotYggdrasilServer => Loc.Get("Auth_NotYggdrasil"),
        AuthFailure.InsecureServer => Loc.Get("Auth_Insecure"),
        AuthFailure.NoYggdrasilProfiles => Loc.Get("Auth_NoYggdrasilProfiles"),
        AuthFailure.RateLimited => Loc.Get("Auth_RateLimited"),
        AuthFailure.SignInAgain => Loc.Get("Auth_SignInAgain"),
        AuthFailure.NoXboxAccount => Loc.Get("Auth_NoXbox"),
        AuthFailure.XboxUnavailableInRegion => Loc.Get("Auth_Region"),
        AuthFailure.AdultVerificationRequired => Loc.Get("Auth_AdultVerification"),
        AuthFailure.ChildAccountNeedsFamily => Loc.Get("Auth_Child"),
        AuthFailure.DoesNotOwnGame => Loc.Get("Auth_NotOwned"),
        AuthFailure.NoMinecraftProfile => Loc.Get("Auth_NoProfile"),
        AuthFailure.AppNotApprovedForMinecraft => Loc.Get("Auth_AppNotApproved"),
        AuthFailure.BrokerUnavailable => Loc.Format("Auth_BrokerUnavailable", ex.Message),
        AuthFailure.DifferentPlayer => Loc.Get("Auth_DifferentPlayer"),
        _ => ex.Message,
    };

    /// <summary>
    /// Signs the account in again, in place (a Microsoft sign-in, or the skin site's password): its instances, skin
    /// and selection stay. Offline accounts have nothing to sign in.
    /// </summary>
    [RelayCommand]
    private async Task SignInAgainAsync(AccountItem item)
    {
        if (!item.CanSignInAgain)
            return;
        ErrorMessage = null;
        StatusMessage = null;
        var name = item.Account.PlayerName;
        switch (await signIn.SignInAgainAsync(item.Account, forLaunch: false))
        {
            case SignInAgainResult.SignedIn:
                item.NeedsSignIn = false;
                if (item.Account.PlayerName != name)
                    await LoadAsync(); // Renamed since it was listed.
                StatusMessage = Loc.Format("Accounts_SignedInAgain", item.Account.PlayerName);
                break;
            case SignInAgainResult.AddedOther:
                await LoadAsync();
                break;
        }
    }

    /// <summary>A launch (or the Skin page) found an account's sign-in expired, or a sign-in fixed it: the card follows.</summary>
    private void OnSignInStateChanged(object? sender, Account account) => _ui.TryEnqueue(() =>
    {
        if (Accounts.FirstOrDefault(i => i.Account.Id == account.Id) is { } item)
            item.NeedsSignIn = account.NeedsSignIn;
    });

    [RelayCommand(CanExecute = nameof(CanAddOffline))]
    private async Task AddOfflineAsync()
    {
        ErrorMessage = null;
        var name = OfflineName.Trim();
        if (!OfflineAccounts.IsValidName(name))
        {
            ErrorMessage = Loc.Get("Account_InvalidOfflineName");
            return;
        }
        try
        {
            await accounts.AddOfflineAsync(name);
            OfflineName = "";
            await LoadAsync();
        }
        catch (AuthException ex)
        {
            ErrorMessage = DescribeFailure(ex);
        }
    }

    [RelayCommand]
    private async Task SelectAsync(AccountItem item)
    {
        await accounts.SelectAsync(item.Account.Id);
        foreach (var a in Accounts)
            a.IsSelected = a == item;
    }

    /// <summary>Saves the order after the list was rearranged by dragging.</summary>
    public Task SaveOrderAsync() => accounts.ReorderAsync(Accounts.Select(a => a.Account.Id).ToList());

    /// <summary>Moves an account one place up (−1) or down (+1): the keyboard/menu way to reorder.</summary>
    public async Task MoveAsync(AccountItem item, int delta)
    {
        var from = Accounts.IndexOf(item);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Accounts.Count)
            return;
        Accounts.Move(from, to);
        await SaveOrderAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(AccountItem item)
    {
        await accounts.RemoveAsync(item.Account.Id);
        await LoadAsync();
    }
}
