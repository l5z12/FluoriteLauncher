using Fluorite.Auth.Accounts;
using Fluorite.Auth.Microsoft;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Services;

/// <summary>How to sign in to Microsoft: Windows' account picker (the browser when Windows can't), or one way only.</summary>
public enum MicrosoftSignInMethod
{
    Default,
    Windows,
    Browser,
    Code,
}

public enum SignInAgainResult
{
    Cancelled,
    SignedIn,

    /// <summary>The sign-in was for another player, who was added as an account of their own.</summary>
    AddedOther,
}

/// <summary>
/// Microsoft sign-in, for adding an account and for signing one in again; and the sign-in-again dialog, for Microsoft
/// and skin-site accounts. Signing in again keeps the account's entry and ID, so the instances that use it, its skin
/// and the selection stay as they are.
/// </summary>
public sealed class AccountSignIn(AccountService accounts, MicrosoftOAuthClient oauth, ILogger<AccountSignIn> logger)
{
    /// <summary>Signs in to Microsoft: the MSA token and, when Windows' broker signed in, its account ID.</summary>
    /// <param name="waiting">Called as it starts waiting for the user: how (Windows, Browser or Code), with the code for Code.</param>
    public async Task<(MsaTokenResponse Token, string? BrokerAccountId)> GetTokenAsync(
        MicrosoftSignInMethod method, Action<MicrosoftSignInMethod, DeviceCodeResponse?>? waiting, CancellationToken ct)
    {
        switch (method)
        {
            case MicrosoftSignInMethod.Default:
                try
                {
                    return await WithWindowsAsync(waiting, ct);
                }
                catch (AuthException ex) when (ex.Failure == AuthFailure.BrokerUnavailable)
                {
                    logger.LogWarning(ex, "Windows sign-in is unavailable; signing in with the browser");
                    return await WithBrowserAsync(waiting, ct);
                }
            case MicrosoftSignInMethod.Windows:
                return await WithWindowsAsync(waiting, ct);
            case MicrosoftSignInMethod.Browser:
                return await WithBrowserAsync(waiting, ct);
            default:
                return await WithCodeAsync(waiting, ct);
        }
    }

    private async Task<(MsaTokenResponse, string?)> WithWindowsAsync(Action<MicrosoftSignInMethod, DeviceCodeResponse?>? waiting, CancellationToken ct)
    {
        var broker = accounts.Broker ?? throw new AuthException(AuthFailure.BrokerUnavailable, "Windows sign-in isn't available.");
        waiting?.Invoke(MicrosoftSignInMethod.Windows, null);
        var token = await broker.SignInAsync(ct);
        return (new MsaTokenResponse { AccessToken = token.AccessToken }, token.AccountId);
    }

    private async Task<(MsaTokenResponse, string?)> WithBrowserAsync(Action<MicrosoftSignInMethod, DeviceCodeResponse?>? waiting, CancellationToken ct)
    {
        waiting?.Invoke(MicrosoftSignInMethod.Browser, null);
        return (await oauth.SignInWithBrowserAsync(async uri => await Windows.System.Launcher.LaunchUriAsync(uri), ct), null);
    }

    private async Task<(MsaTokenResponse, string?)> WithCodeAsync(Action<MicrosoftSignInMethod, DeviceCodeResponse?>? waiting, CancellationToken ct)
    {
        var code = await oauth.StartDeviceCodeAsync(ct);
        waiting?.Invoke(MicrosoftSignInMethod.Code, code);
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(code.UserCode);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        // Opened as given, with the code on the clipboard for a paste. Microsoft's page has an undocumented `?otc=<code>`
        // pre-fill, but it only accepts codes from some older app registrations (Prism's, MultiMC's, HMCL's). For
        // Fluorite's app, and even a bare new app made for the test, it fails with "The application is a first party
        // application…" (HR=0x8004A977). No app registration setting changes this (tested 2026-11).
        await Windows.System.Launcher.LaunchUriAsync(new Uri(code.VerificationUri));
        return (await oauth.PollDeviceCodeAsync(code, ct), null);
    }

    /// <summary>
    /// Signs an account in again, in place, in one dialog: it asks for what's needed (a Microsoft sign-in, or the skin
    /// site's password), waits for it, and checks it's the same player. A sign-in for another player is never written
    /// over the account: the dialog says who it was and offers to try again.
    /// </summary>
    /// <param name="forLaunch">A launch waits on it: the button says "Sign in and play", and another player isn't offered as a new account.</param>
    /// <param name="ct">Cancelled when the launch is stopped: the dialog closes.</param>
    public async Task<SignInAgainResult> SignInAgainAsync(Account account, bool forLaunch, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Offline || ct.IsCancellationRequested)
            return SignInAgainResult.Cancelled;
        var microsoft = account.Kind == AccountKind.Microsoft;
        var player = account.PlayerName;

        // What happened (when known), what to do, and that nothing else changes.
        var hint = microsoft
            ? Loc.Format("SignInAgain_MicrosoftHint", player)
            : Loc.Format("SignInAgain_YggdrasilHint", account.LoginName ?? player, account.YggdrasilServerName ?? account.YggdrasilServer ?? "");
        var panel = new StackPanel { Spacing = 12, MinWidth = 400, MaxWidth = 560 };
        panel.Children.Add(Dialogs.Text(account.NeedsSignIn ? $"{Loc.Format("SignInAgain_Expired", player)} {hint}" : hint));
        panel.Children.Add(Dialogs.Text(Loc.Get("SignInAgain_Kept"), secondary: true));

        var problem = new InfoBar { IsClosable = false, IsOpen = false };
        panel.Children.Add(problem);

        PasswordBox? password = null;
        if (!microsoft)
        {
            password = new PasswordBox { Header = Loc.Get("Accounts_YggPassword/Header") };
            panel.Children.Add(password);
        }

        // While it waits, on the user (Windows' window, the browser, the code) or on the servers.
        var waitText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(waitText, AutomationLiveSetting.Polite);
        var waiting = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children = { new ProgressRing { IsActive = true, Width = 20, Height = 20 }, waitText },
        };
        panel.Children.Add(waiting);

        var codeText = new TextBlock { FontSize = 28, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true };
        var codeUrl = Dialogs.Text("", secondary: true);
        codeUrl.IsTextSelectionEnabled = true;
        var codePanel = new StackPanel
        {
            Spacing = 4,
            Visibility = Visibility.Collapsed,
            Children = { Dialogs.Text(Loc.Get("Accounts_DeviceCodeHint/Text")), codeText, codeUrl },
        };
        panel.Children.Add(codePanel);

        // The other ways in, as on the Accounts page's sign-in button.
        var browser = new HyperlinkButton { Content = Loc.Get("Accounts_MethodBrowser/Text") };
        var code = new HyperlinkButton { Content = Loc.Get("Accounts_SignInCode/Content") };
        var others = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Visibility = microsoft ? Visibility.Visible : Visibility.Collapsed,
            Children = { browser, code },
        };
        panel.Children.Add(others);

        var dialog = Dialogs.Create(Loc.Format("SignInAgain_Title", player), panel, Loc.Get(forLaunch ? "SignInAgain_SignInAndPlay" : "SignInAgain_SignIn"));
        var result = SignInAgainResult.Cancelled;
        var closed = false;
        CancellationTokenSource? work = null;
        MicrosoftSignIn? other = null; // Another player's sign-in, offered as an account of its own.

        void UpdateButtons()
        {
            dialog.IsPrimaryButtonEnabled = work is null && password is not { Password.Length: 0 };
            dialog.IsSecondaryButtonEnabled = work is null;
        }

        // Null: waiting for nothing.
        void Busy(string? text)
        {
            waitText.Text = text ?? "";
            waiting.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            others.Visibility = microsoft && text is null ? Visibility.Visible : Visibility.Collapsed;
            password?.IsEnabled = text is null;
            UpdateButtons();
            if (text is not null)
                FrameworkElementAutomationPeer.CreatePeerForElement(waitText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        void Show(InfoBarSeverity severity, string message)
        {
            problem.Severity = severity;
            problem.Message = message;
            problem.IsOpen = true;
            dialog.PrimaryButtonText = Loc.Get("SignInAgain_TryAgain");
        }

        void Finish(SignInAgainResult outcome)
        {
            result = outcome;
            closed = true;
            dialog.Hide();
        }

        async Task MicrosoftAsync(MicrosoftSignInMethod method)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            work = cts;
            other = null;
            dialog.SecondaryButtonText = "";
            problem.IsOpen = false;
            Busy(Loc.Get("SignInAgain_Working"));
            try
            {
                var (token, brokerAccountId) = await GetTokenAsync(method, (how, deviceCode) =>
                {
                    Busy(Loc.Get(how switch
                    {
                        MicrosoftSignInMethod.Windows => "SignInAgain_WaitingWindows",
                        MicrosoftSignInMethod.Browser => "SignInAgain_WaitingBrowser",
                        _ => "SignInAgain_WaitingCode",
                    }));
                    if (deviceCode is not null)
                    {
                        codeText.Text = deviceCode.UserCode;
                        codeUrl.Text = deviceCode.VerificationUri;
                        codePanel.Visibility = Visibility.Visible;
                    }
                }, cts.Token);
                codePanel.Visibility = Visibility.Collapsed;
                Busy(Loc.Get("SignInAgain_Checking"));
                var signIn = await accounts.SignInMicrosoftAsync(token, brokerAccountId, cts.Token);
                if (!AccountService.IsSamePlayer(account, signIn.Profile.Id))
                {
                    Show(InfoBarSeverity.Warning, $"{Loc.Format("SignInAgain_DifferentPlayer", signIn.Profile.Name, player)} {hint}");
                    var listed = (await accounts.GetAccountsAsync(cts.Token)).Any(a => a.Kind == AccountKind.Microsoft && AccountService.IsSamePlayer(a, signIn.Profile.Id));
                    if (!forLaunch && !listed)
                    {
                        other = signIn;
                        dialog.SecondaryButtonText = Loc.Format("SignInAgain_AddOther", signIn.Profile.Name);
                    }
                    return;
                }
                await accounts.ReauthenticateMicrosoftAsync(account, signIn, cts.Token);
                Finish(SignInAgainResult.SignedIn);
            }
            catch (OperationCanceledException ex) when (!cts.IsCancellationRequested)
            {
                Show(InfoBarSeverity.Error, Loc.Format("Auth_Network", ex.Message)); // A request timed out.
            }
            catch (Exception ex) when (ex is OperationCanceledException or AuthException { Failure: AuthFailure.Cancelled })
            {
                // Closed, or cancelled in Windows' window or the browser: back to the start, to try again or another way.
            }
            catch (AuthException ex)
            {
                Show(InfoBarSeverity.Error, ViewModels.AccountsViewModel.DescribeFailure(ex));
            }
            catch (HttpRequestException ex)
            {
                Show(InfoBarSeverity.Error, Loc.Format("Auth_Network", ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Show(InfoBarSeverity.Error, ex.Message);
            }
            finally
            {
                work = null;
                if (!closed)
                {
                    codePanel.Visibility = Visibility.Collapsed;
                    Busy(null);
                }
            }
        }

        async Task YggdrasilAsync(PasswordBox box)
        {
            if (box.Password.Length == 0)
                return;
            using var cts = new CancellationTokenSource();
            work = cts;
            problem.IsOpen = false;
            Busy(Loc.Get("SignInAgain_Working"));
            try
            {
                // The password goes straight from the box to the server; it's never kept.
                await accounts.ReauthenticateYggdrasilAsync(account, box.Password, cts.Token);
                Finish(SignInAgainResult.SignedIn);
            }
            catch (OperationCanceledException ex) when (!cts.IsCancellationRequested)
            {
                Show(InfoBarSeverity.Error, Loc.Format("Auth_Network", ex.Message));
            }
            catch (OperationCanceledException)
            {
                // Closed.
            }
            catch (AuthException ex) when (ex.Failure == AuthFailure.DifferentPlayer)
            {
                Show(InfoBarSeverity.Warning, ex.Player is { } p
                    ? Loc.Format("SignInAgain_DifferentPlayer", p, player)
                    : Loc.Format("SignInAgain_PlayerGone", player));
            }
            catch (AuthException ex)
            {
                Show(InfoBarSeverity.Error, ViewModels.AccountsViewModel.DescribeFailure(ex));
            }
            catch (HttpRequestException ex)
            {
                Show(InfoBarSeverity.Error, Loc.Format("Auth_Network", ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Show(InfoBarSeverity.Error, ex.Message);
            }
            finally
            {
                work = null;
                if (!closed)
                {
                    Busy(null);
                    // Ready to type again: a wrong password is typed over, not added to.
                    box.Focus(FocusState.Programmatic);
                    box.SelectAll();
                }
            }
        }

        async Task AddOtherAsync(MicrosoftSignIn signIn)
        {
            using var cts = new CancellationTokenSource();
            work = cts;
            Busy(Loc.Get("SignInAgain_Working"));
            try
            {
                await accounts.AddMicrosoftAsync(signIn, cts.Token);
                Finish(SignInAgainResult.AddedOther);
            }
            catch (OperationCanceledException)
            {
                // Closed.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Show(InfoBarSeverity.Error, ex.Message);
            }
            finally
            {
                work = null;
                if (!closed)
                    Busy(null);
            }
        }

        // (sender, e), not (_, e): a lone _ is a parameter, and the discards below would assign to it.
        dialog.PrimaryButtonClick += (sender, e) =>
        {
            e.Cancel = true; // Open until signed in, or cancelled.
            if (work is null)
                _ = password is null ? MicrosoftAsync(MicrosoftSignInMethod.Default) : YggdrasilAsync(password);
        };
        dialog.SecondaryButtonClick += (sender, e) =>
        {
            e.Cancel = true;
            if (work is null && other is { } signIn)
                _ = AddOtherAsync(signIn);
        };
        // Cancel (or Esc) also ends a sign-in that's still waiting: the browser's, or the code's.
        dialog.Closing += (_, _) =>
        {
            closed = true;
            work?.Cancel();
        };
        browser.Click += (_, _) =>
        {
            if (work is null)
                _ = MicrosoftAsync(MicrosoftSignInMethod.Browser);
        };
        code.Click += (_, _) =>
        {
            if (work is null)
                _ = MicrosoftAsync(MicrosoftSignInMethod.Code);
        };
        if (password is not null)
        {
            password.PasswordChanged += (_, _) => UpdateButtons();
            dialog.Opened += (_, _) => password.Focus(FocusState.Programmatic);
        }
        else
        {
            Dialogs.FocusPrimaryOnOpen(dialog); // Enter signs in; not the first link.
        }
        UpdateButtons();

        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        using var stopped = ct.Register(() => ui.TryEnqueue(dialog.Hide));
        await Dialogs.ShowAsync(dialog);
        return result;
    }
}
