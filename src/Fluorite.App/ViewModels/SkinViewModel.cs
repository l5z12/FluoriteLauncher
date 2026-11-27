using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Auth.Accounts;
using Fluorite.Auth.Microsoft;
using Fluorite.Core.Skins;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.ViewModels;

/// <summary>A decoded skin (and cape) for the 3D view.</summary>
public sealed record PlayerPreview(SkinImage Skin, bool Slim, SkinImage? Cape);

/// <summary>A cape choice; <see cref="Id"/> null is "no cape".</summary>
public sealed record CapeOption(string? Id, string Name);

/// <summary>A skin from the library, with a small front view.</summary>
public sealed partial class LibrarySkinItem(LibrarySkin skin) : ObservableObject
{
    public LibrarySkin Skin { get; } = skin;
    public string ModelText => Loc.Get(Skin.Slim ? "Skin_ModelSlim" : "Skin_ModelClassic");
    [ObservableProperty] public partial ImageSource? Preview { get; set; }
}

/// <summary>The skin page of one account: preview, skin file and model, cape, name, and the skin library.</summary>
public sealed partial class SkinViewModel(AccountSkins skins) : ObservableObject
{
    private byte[]? _skinPng;
    private byte[]? _capePng;
    private bool _loading;

    public Account? Account { get; private set; }

    public string Title => Account?.PlayerName ?? "";

    public ObservableCollection<CapeOption> Capes { get; } = [];
    public ObservableCollection<LibrarySkinItem> Library { get; } = [];

    /// <summary>What the 3D view shows; null for the default skin (which isn't ours to ship).</summary>
    [ObservableProperty] public partial PlayerPreview? Preview { get; set; }

    [ObservableProperty] public partial bool IsDefaultSkin { get; set; }

    /// <summary>0 = classic (Steve, 4 px arms), 1 = slim (Alex, 3 px arms). Changing it re-applies the current skin.</summary>
    [ObservableProperty] public partial int ModelIndex { get; set; }

    [ObservableProperty] public partial int CapeIndex { get; set; }
    [ObservableProperty] public partial bool HasCapes { get; set; }
    [ObservableProperty] public partial bool CanChangeSkin { get; set; }
    [ObservableProperty] public partial bool IsMicrosoft { get; set; }
    [ObservableProperty] public partial string? ManageUrl { get; set; }
    [ObservableProperty] public partial string? ManageText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string? NameStatus { get; set; }

    public async Task InitializeAsync(Account account)
    {
        Account = account;
        OnPropertyChanged(nameof(Title));
        IsMicrosoft = account.Kind == AccountKind.Microsoft;
        CanChangeSkin = account.Kind != AccountKind.Yggdrasil;
        LoadLibrary();
        await RunAsync(async () => await Show(await skins.GetAsync(account)));
        if (IsMicrosoft)
            await RunAsync(LoadNameStatusAsync);
    }

    /// <summary>Frees the rendered images when the page is left.</summary>
    public void Release()
    {
        Preview = null;
        foreach (var item in Library)
            ImageLoader.Release(item.Preview);
        Library.Clear();
        _skinPng = _capePng = null;
    }

    private async Task Show(SkinState state)
    {
        _loading = true;
        try
        {
            CanChangeSkin = state.CanChangeSkin;
            ManageUrl = state.ManageUrl;
            ManageText = Account?.YggdrasilServerName is { } server ? Loc.Format("Skin_ManageOn", server) : null;
            ModelIndex = state.Slim ? 1 : 0;
            IsDefaultSkin = state.SkinUrl is null;

            Capes.Clear();
            Capes.Add(new CapeOption(null, Loc.Get("Skin_NoCape")));
            foreach (var cape in state.Capes)
                Capes.Add(new CapeOption(cape.Id, cape.Name));
            HasCapes = state.Capes.Count > 0;
            var active = state.Capes.ToList().FindIndex(c => c.Active);
            CapeIndex = active + 1;

            _skinPng = state.SkinUrl is { } url ? await skins.LoadTextureAsync(url) : null;
            _capePng = state.CapeUrl is { } capeUrl ? await skins.LoadTextureAsync(capeUrl) : null;
            await RenderAsync(state.Slim);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RenderAsync(bool slim)
    {
        if (_skinPng is null || await SkinImages.DecodeAsync(_skinPng) is not { IsSkin: true } skin)
        {
            Preview = null; // The default skin isn't ours to ship; the page says "default skin" instead.
            return;
        }
        var cape = _capePng is null ? null : await SkinImages.DecodeAsync(_capePng);
        Preview = new PlayerPreview(skin, slim, cape);
    }

    partial void OnModelIndexChanged(int value)
    {
        if (_loading || Account is null || _skinPng is null || !CanChangeSkin)
            return;
        var png = _skinPng;
        _ = RunAsync(async () =>
        {
            var path = skins.AddToLibrary(png, value == 1).Path;
            await Show(await skins.SetSkinAsync(Account, path, value == 1));
            LoadLibrary();
        });
    }

    partial void OnCapeIndexChanged(int value)
    {
        if (_loading || Account is null || !IsMicrosoft || value < 0 || value >= Capes.Count)
            return;
        var id = Capes[value].Id;
        _ = RunAsync(async () => await Show(await skins.SetCapeAsync(Account, id)));
    }

    /// <summary>Wears a PNG the user picked. Only 64×64 and legacy 64×32 skins (or HD multiples) are accepted.</summary>
    public async Task UseFileAsync(string path)
    {
        if (Account is null)
            return;
        var bytes = await File.ReadAllBytesAsync(path);
        if (await SkinImages.DecodeAsync(bytes) is not { IsSkin: true })
        {
            ErrorMessage = Loc.Get("Skin_NotASkin");
            return;
        }
        await RunAsync(async () =>
        {
            await Show(await skins.SetSkinAsync(Account, path, ModelIndex == 1));
            LoadLibrary();
        });
    }

    public Task UseLibrarySkinAsync(LibrarySkinItem item) =>
        Account is null ? Task.CompletedTask : RunAsync(async () => await Show(await skins.SetSkinAsync(Account, item.Skin.Path, item.Skin.Slim)));

    public void RemoveLibrarySkin(LibrarySkinItem item)
    {
        try
        {
            AccountSkins.RemoveFromLibrary(item.Skin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return;
        }
        ImageLoader.Release(item.Preview);
        Library.Remove(item);
    }

    public Task ResetAsync() =>
        Account is null ? Task.CompletedTask : RunAsync(async () => await Show(await skins.ResetSkinAsync(Account)));

    public async Task ChangeNameAsync()
    {
        var name = NewName.Trim();
        if (Account is null || !OfflineAccounts.IsValidName(name))
        {
            NameStatus = Loc.Get("Account_InvalidOfflineName");
            return;
        }
        await RunAsync(async () =>
        {
            switch (await skins.CheckNameAsync(Account, name))
            {
                case NameAvailability.Taken:
                    NameStatus = Loc.Format("Skin_NameTaken", name);
                    return;
                case NameAvailability.NotAllowed:
                    NameStatus = Loc.Format("Skin_NameNotAllowed", name);
                    return;
            }
            await skins.ChangeNameAsync(Account, name);
            NewName = "";
            OnPropertyChanged(nameof(Title));
            NameStatus = Loc.Format("Skin_NameChanged", name);
        });
    }

    private async Task LoadNameStatusAsync()
    {
        if (Account is null)
            return;
        NameStatus = await skins.NextNameChangeAsync(Account) is { } next
            ? Loc.Format("Skin_NameNextChange", next.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture))
            : null;
    }

    private void LoadLibrary()
    {
        foreach (var item in Library)
            ImageLoader.Release(item.Preview);
        Library.Clear();
        foreach (var skin in skins.Library())
            Library.Add(new LibrarySkinItem(skin));
        _ = LoadPreviewsAsync(Library.ToList());
    }

    private async Task LoadPreviewsAsync(IReadOnlyList<LibrarySkinItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                if (await SkinImages.DecodeAsync(await File.ReadAllBytesAsync(item.Skin.Path)) is not { IsSkin: true } skin)
                    continue;
                var preview = await SkinImages.ToSourceAsync(SkinRenderer.RenderBody(skin, item.Skin.Slim, back: false), 96);
                if (Library.Contains(item))
                    item.Preview = preview;
                else
                    ImageLoader.Release(preview);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The error is one only signing in again fixes: its bar has the button for it.</summary>
    [ObservableProperty] public partial bool CanSignInAgain { get; set; }

    /// <summary>Signs the account in again, then shows its skin as it is now.</summary>
    [RelayCommand]
    private async Task SignInAgainAsync()
    {
        if (Account is not { } account)
            return;
        if (await App.Get<AccountSignIn>().SignInAgainAsync(account, forLaunch: false) != SignInAgainResult.SignedIn)
            return;
        OnPropertyChanged(nameof(Title)); // Renamed since, perhaps.
        await RunAsync(async () => await Show(await skins.GetAsync(account)));
    }

    private async Task RunAsync(Func<Task> work)
    {
        ErrorMessage = null;
        CanSignInAgain = false;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (AuthException ex)
        {
            ErrorMessage = AccountsViewModel.DescribeFailure(ex);
            CanSignInAgain = ex.Failure == AuthFailure.SignInAgain;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            ErrorMessage = Loc.Format("Auth_Network", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
