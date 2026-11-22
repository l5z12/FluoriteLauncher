using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Fluorite.App.Services;

/// <summary>Windows toast notifications. Clicking a toast opens the related instance's log.</summary>
public sealed class NotificationService(ILogger<NotificationService> logger)
{
    private bool _registered;

    /// <summary>Raised on the notification thread with the instance id to open.</summary>
    public event EventHandler<string>? OpenInstanceRequested;

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Notifications are optional (e.g. policy-disabled); never block startup on them.
            logger.LogWarning(ex, "App notifications unavailable");
        }
    }

    public void Unregister()
    {
        if (!_registered)
            return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unregister notifications failed");
        }
    }

    /// <param name="cause">What the crash analyzer found, if anything.</param>
    public void ShowGameCrashed(string instanceId, string instanceName, int exitCode, string? cause = null) =>
        Show(Loc.Format("Toast_CrashedTitle", instanceName),
            cause is null ? Loc.Format("Toast_CrashedBody", exitCode) : Loc.Format("Toast_CrashedCause", cause, exitCode),
            instanceId, Loc.Get("Toast_ViewLog"));

    public void ShowGameClosed(string instanceId, string instanceName, TimeSpan played) =>
        Show(Loc.Format("Toast_ClosedTitle", instanceName), Loc.Format("Toast_ClosedBody", (int)played.TotalMinutes), instanceId, null);

    private void Show(string title, string body, string instanceId, string? buttonText)
    {
        if (!_registered)
            return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddArgument("instance", instanceId)
                .AddText(title)
                .AddText(body);
            if (buttonText is not null)
                builder.AddButton(new AppNotificationButton(buttonText).AddArgument("action", "open").AddArgument("instance", instanceId));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to show notification");
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => HandleArguments(args.Arguments);

    /// <summary>Also used when the app is cold-started by clicking a toast.</summary>
    public void HandleArguments(IDictionary<string, string> arguments)
    {
        if (arguments.TryGetValue("action", out var action) && action == "open" && arguments.TryGetValue("instance", out var id))
            OpenInstanceRequested?.Invoke(this, id);
    }
}
