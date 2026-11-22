using System.Globalization;
using Fluorite.App.Services;
using Fluorite.Core.News;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fluorite.App.Views;

/// <summary>Renders Mojang's Java patch notes HTML with the app's theme. Links open in the default browser.</summary>
public sealed partial class PatchNotesPage : Page
{
    public PatchNotesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not PatchNoteEntry entry)
            return;
        TitleText.Text = entry.Title;
        DateText.Text = entry.Date?.LocalDateTime.ToString("D", CultureInfo.CurrentCulture) ?? "";

        try
        {
            var content = await App.Get<MojangNewsClient>().GetPatchNoteAsync(entry);
            await HtmlContent.ShowAsync(Viewer, content?.Body ?? entry.ShortText ?? "");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Runtime.InteropServices.COMException)
        {
            Viewer.Visibility = Visibility.Collapsed;
            DateText.Text = ex.Message;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }
}
