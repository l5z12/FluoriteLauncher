using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

/// <summary>Launches in progress (downloads) and running games across all instances.</summary>
public sealed partial class ActivityPage : Page
{
    public ActivityPage()
    {
        InitializeComponent();
    }

    public GameRunner Runner { get; } = App.Get<GameRunner>();
    public ContentInstallService Installer { get; } = App.Get<ContentInstallService>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Runner.Games.CollectionChanged += OnGamesChanged;
        Installer.Jobs.CollectionChanged += OnGamesChanged;
        UpdateEmptyState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Runner.Games.CollectionChanged -= OnGamesChanged;
        Installer.Jobs.CollectionChanged -= OnGamesChanged;
    }

    private void OnGamesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() => EmptyState.Visibility = Runner.Games.Count == 0 && Installer.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        Runner.ClearFinished();
        foreach (var job in Installer.Jobs.Where(j => !j.IsActive).ToList())
            Installer.Jobs.Remove(job);
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ContentJob>(sender) is { CanCancel: true } job)
            job.Cancel();
    }

    /// <summary>The failed card makes way for the new attempt's.</summary>
    private async void RetryJob_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ContentJob>(sender) is { CanRetry: true, Retry: { } retry } job)
        {
            Installer.Jobs.Remove(job);
            await retry();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<RunningGame>(sender) is { } game)
            game.Stop();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<RunningGame>(sender) is { } game)
            App.Window?.NavigateToInstance(new InstanceItem(game.Instance) { IsRunning = game.IsActive });
    }
}
