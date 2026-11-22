using System.Globalization;
using Fluorite.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Fluorite.App;

public partial class App : Application
{
    private static IHost? s_host;

    public App()
    {
        s_host = AppHost.Build();
        ApplyLanguageOverride(StartupFlags.Current.Language ?? Get<SettingsService>().Current.Language);
        // Keep WebView2 profile data in the data root (the install folder of an MSIX/unpackaged app may be read-only).
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(AppPaths.DataRoot, "cache", "webview2"));
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TraceFirstChanceExceptions();
    }

    /// <summary>
    /// <c>--trace-exceptions</c> writes every thrown exception to <c>logs/exceptions.log</c>. Errors that escape async
    /// callbacks end the process from native code before any handler sees them; this keeps a record.
    /// </summary>
    private static void TraceFirstChanceExceptions()
    {
        if (!StartupFlags.Current.TraceExceptions)
            return;
        Directory.CreateDirectory(AppPaths.LogsDir);
        var path = Path.Combine(AppPaths.LogsDir, "exceptions.log");
        var gate = new object();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            lock (gate)
            {
                try
                {
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {e.Exception.GetType().FullName}: {e.Exception.Message}\n{Environment.StackTrace}\n\n");
                }
                catch (IOException)
                {
                }
            }
        };
    }

    public static IServiceProvider Services => s_host?.Services ?? throw new InvalidOperationException("Host not built.");

    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static MainWindow? Window { get; private set; }

    /// <summary>Must run before any XAML/resources load; changing it later requires a restart.</summary>
    private static void ApplyLanguageOverride(string? language)
    {
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language ?? "";
            if (language is not null)
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo(language);
        }
        catch (Exception ex) when (ex is ArgumentException or CultureNotFoundException or System.Runtime.InteropServices.COMException)
        {
            // Unknown tag in settings: fall back to the Windows language.
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Resolve on the UI thread so it captures the UI DispatcherQueue.
        _ = Get<GameRunner>();
        _ = Get<DiscordService>(); // Follows game starts and exits from now on.
        var notifications = Get<NotificationService>();
        notifications.Register();

        Window = new MainWindow();
        var current = AppInstance.GetCurrent();
        current.Activated += OnRedirectedActivation;

        var activation = current.GetActivatedEventArgs();
        await ShowMainWindowAsync(Window, plainLaunch: activation.Kind == ExtendedActivationKind.Launch);
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs toast)
            notifications.HandleArguments(toast.Arguments);
        else if (activation.Kind == ExtendedActivationKind.Launch)
            await Window.HandleActivationAsync(ActivationRequest.FromCommandLine(Environment.GetCommandLineArgs().Skip(1).ToList()));
        else
            await Window.HandleActivationAsync(ActivationRequest.FromAppActivation(activation));

        await Window.ShowWelcomeIfFirstRunAsync(force: StartupFlags.Current.ShowWelcome);
        if (StartupFlags.Current.ShowFlags)
            await Window.ShowFlagsAsync();
    }

    /// <summary>The longest the launch window waits for Home before the main window opens anyway (Home then shows rings).</summary>
    private static readonly TimeSpan LaunchWindowLimit = TimeSpan.FromSeconds(6);

    /// <summary>Shorter than this, the launch window would only flash, which reads as a glitch.</summary>
    private static readonly TimeSpan LaunchWindowMinimum = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Settings › While Home loads › Show a launch window: a plain launch shows the launch window first, and the main
    /// window once Home has loaded (or <see cref="LaunchWindowLimit"/> has passed), so it opens complete. A launch
    /// for something (a notification, a link, a file) opens the main window straight away.
    /// </summary>
    private static async Task ShowMainWindowAsync(MainWindow window, bool plainLaunch)
    {
        if (!plainLaunch || Get<SettingsService>().Current.HomeLoading != Core.Settings.HomeLoadingStyle.LaunchWindow)
        {
            window.Activate();
            return;
        }
        var launch = new Views.LaunchWindow(window.Theme, window.AppWindow);
        launch.Activate();
        var shown = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAny(Get<ViewModels.HomeViewModel>().FirstLoad, launch.Dismissed, Task.Delay(LaunchWindowLimit));
        if (LaunchWindowMinimum - shown.Elapsed is { Ticks: > 0 } rest && !launch.Dismissed.IsCompleted)
            await Task.Delay(rest);
        window.Activate();
        launch.CloseIfOpen();
    }

    /// <summary>A second launch (protocol, file, jump list, shortcut) was redirected to this instance.</summary>
    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        var request = ActivationRequest.FromAppActivation(e);
        var flags = e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? StartupFlags.Parse(Program.SplitArguments(launch.Arguments))
            : null;
        Window?.DispatcherQueue.TryEnqueue(async () =>
        {
            Window.BringToFront();
            await Window.HandleActivationAsync(request);
            if (flags is null)
                return;
            if (flags.ShowWelcome)
                await Window.ShowWelcomeIfFirstRunAsync(force: true);
            if (flags.ShowFlags || flags.HasStartupOnly)
                await Window.ShowFlagsAsync(startupOnlyIgnored: flags.HasStartupOnly);
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Services.GetRequiredService<ILogger<App>>().LogCritical(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }
}
