using System.Runtime.InteropServices;
using Fluorite.App.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.Views;

/// <summary>
/// Settings › While Home loads › Show a launch window: the logo, the name and a progress bar in a small window with
/// no title bar, as Microsoft 365 apps show while they start. The main window opens once Home has what it fetches.
/// </summary>
public sealed partial class LaunchWindow : Window
{
    private const int WidthDips = 420, HeightDips = 260;

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="theme">The main window's (Settings › Theme, or <c>--theme</c>).</param>
    /// <param name="over">Centred on the screen this window will open on.</param>
    public LaunchWindow(ElementTheme theme, AppWindow over)
    {
        Title = Loc.Get("AppTitle/Text");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }
        SystemBackdrop = new MicaBackdrop();

        var stack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Image
        {
            Width = 88,
            Height = 88,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/LogoMedium.png")) { DecodePixelWidth = 88, DecodePixelType = DecodePixelType.Logical },
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.Get("AppTitle/Text"),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var progress = new ProgressBar { IsIndeterminate = true, Width = 180, Margin = new Thickness(0, 6, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(progress, Loc.Get("LaunchWindow_Starting"));
        stack.Children.Add(progress);
        Content = new Grid { RequestedTheme = theme, Children = { stack } };

        Closed += (_, _) => _closed.TrySetResult();
        CenterOn(over);
    }

    /// <summary>Closed by the user (Alt+F4): the main window needn't wait any longer.</summary>
    public Task Dismissed => _closed.Task;

    /// <summary>Closes it unless the user already did.</summary>
    public void CloseIfOpen()
    {
        if (!_closed.Task.IsCompleted)
            Close();
    }

    private void CenterOn(AppWindow over)
    {
        var work = DisplayArea.GetFromWindowId(over.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(over.Id)) / 96.0;
        if (scale <= 0)
            scale = 1;
        var (w, h) = ((int)(WidthDips * scale), (int)(HeightDips * scale));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
