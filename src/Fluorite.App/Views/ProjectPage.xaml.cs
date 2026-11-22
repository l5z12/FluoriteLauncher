using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

/// <summary>A Modrinth/CurseForge project: description, versions (with changelogs) and install.</summary>
public sealed partial class ProjectPage : Page
{
    private bool _descriptionLoaded;
    private bool _closed;

    public ProjectPage()
    {
        InitializeComponent();
    }

    public ProjectViewModel ViewModel { get; } = App.Get<ProjectViewModel>();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not ProjectNavigation nav)
            return;
        await ViewModel.InitializeAsync(nav);
        await LoadDescriptionAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _closed = true;
        Description.Close();
    }

    /// <summary>
    /// Leaving the page closes the WebView2, possibly while the description is still downloading; don't use it after.
    /// Without the WebView2 runtime nothing shows, and the summary in the header still does.
    /// </summary>
    private async Task LoadDescriptionAsync()
    {
        if (_descriptionLoaded)
            return;
        _descriptionLoaded = true;
        var html = await ViewModel.GetDescriptionHtmlAsync() ?? "";
        if (!_closed)
            await HtmlContent.ShowAsync(Description, html);
        DescriptionRing.IsActive = false;
        DescriptionRing.Visibility = Visibility.Collapsed;
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        DescriptionPanel.Visibility = tag == "Description" ? Visibility.Visible : Visibility.Collapsed;
        VersionsPanel.Visibility = tag == "Versions" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var p = ViewModel.Project;
        HtmlContent.OpenExternal((sender as FrameworkElement)?.Tag switch
        {
            "web" => p?.WebUrl,
            "source" => p?.SourceUrl,
            "issues" => p?.IssuesUrl,
            "wiki" => p?.WikiUrl,
            _ => null,
        });
    }

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<VersionRow>(sender) is { } row)
            await ViewModel.InstallCommand.ExecuteAsync(row);
    }

    private async void Changelog_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<VersionRow>(sender) is not { } row)
            return;
        await Dialogs.ShowHtmlAsync(Loc.Format("Changelog_Title", row.Number), async () =>
        {
            var text = await ViewModel.GetChangelogAsync(row);
            if (string.IsNullOrWhiteSpace(text))
                return $"<p>{System.Net.WebUtility.HtmlEncode(Loc.Get("Content_NoChangelog"))}</p>";
            return ViewModel.TextFormat == Fluorite.Modding.Platforms.TextFormat.Markdown ? HtmlContent.FromMarkdown(text) : text;
        });
    }
}
