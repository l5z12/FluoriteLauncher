using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Fluorite.App.Services;

/// <summary>Code-built ContentDialogs. WinUI allows one open dialog at a time, so they're serialised.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    public static ContentDialog Create(string title, object content, string? primary = null, string? close = null, string? secondary = null)
    {
        var root = App.Window?.Content as FrameworkElement;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary ?? "",
            SecondaryButtonText = secondary ?? "",
            CloseButtonText = close ?? Loc.Get("Common_CancelLabel"),
            DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = root?.XamlRoot,
            RequestedTheme = root?.ActualTheme ?? ElementTheme.Default,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        // The default 548px max width clips lists of mods/files.
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        return dialog;
    }

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await s_gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            s_gate.Release();
        }
    }

    public static Task<ContentDialogResult> ShowAsync(string title, object content, string? primary = null, string? close = null) =>
        ShowAsync(Create(title, content, primary, close));

    public static Task ShowMessageAsync(string title, string message) =>
        ShowAsync(Create(title, Text(message), close: Loc.Get("Common_Ok")));

    /// <summary>
    /// Asks for one line of text: it opens with <paramref name="initial"/> selected, ready to type over, and the
    /// primary button waits for text that isn't blank or unchanged; Enter confirms. Returns the trimmed text, or null.
    /// <paramref name="validate"/> says, as it's typed, why a text won't do (null: fine); the button waits for that too.
    /// </summary>
    public static async Task<string?> PromptAsync(string title, string primary, string label, string? initial = null, string? placeholder = null,
        Func<string, string?>? validate = null)
    {
        var box = new TextBox { Text = initial ?? "", PlaceholderText = placeholder ?? "", MinWidth = 360, MaxLength = 100 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
        var problem = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Visibility = Visibility.Collapsed,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 8, Children = { box, problem } };
        var dialog = Create(title, content, primary);
        void Update()
        {
            var text = box.Text.Trim();
            var why = text.Length > 0 && validate is not null ? validate(text) : null;
            problem.Text = why ?? "";
            problem.Visibility = why is null ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = text.Length > 0 && text != (initial ?? "").Trim() && why is null;
        }
        box.TextChanged += (_, _) => Update();
        Update();
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>
    /// Keyboard focus starts on the dialog's Close button, not on the first thing in its content that takes focus
    /// (a link, say, which Enter or Space would then open).
    /// </summary>
    public static void FocusCloseOnOpen(ContentDialog dialog) =>
        dialog.Opened += (_, _) =>
        {
            if (Named(dialog, "CloseButton") is Control close) // The part's name in ContentDialog's template.
                close.Focus(FocusState.Programmatic);
        };

    private static FrameworkElement? Named(DependencyObject root, string name)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } found && n == name)
                return found;
            if (Named(child, name) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>Scrolling dialog content. The scrollbar overlays the right edge, so a gutter keeps it off buttons there.</summary>
    public static ScrollViewer Scroll(UIElement content, double maxHeight) =>
        new() { Content = content, MaxHeight = maxHeight, Padding = new Thickness(0, 0, 16, 0) };

    public static TextBlock Text(string text, bool secondary = false)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        // Only override the brush when asked: a null Foreground renders nothing.
        if (secondary)
            tb.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        return tb;
    }

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>A bulleted list.</summary>
    public static TextBlock Bullets(IEnumerable<string> lines)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var line in lines)
        {
            if (tb.Inlines.Count > 0)
                tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(new Run { Text = "• " + line });
        }
        return tb;
    }

    /// <summary>A dialog showing rich text (changelogs) loaded after it opens.</summary>
    public static async Task ShowHtmlAsync(string title, Func<Task<string?>> load)
    {
        var web = new WebView2 { Width = 640, Height = 440 };
        var ring = new ProgressRing { IsActive = true, Width = 32, Height = 32 };
        var grid = new Grid();
        grid.Children.Add(web);
        grid.Children.Add(ring);
        var dialog = Create(title, grid, close: Loc.Get("Common_Close"));
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        var closed = false;
        // Closing while the text still loads closes the WebView2 under this handler: it must not touch it after.
        dialog.Opened += async (_, _) =>
        {
            string html;
            try
            {
                html = await load() ?? "";
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                html = $"<p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>";
            }
            if (!closed)
                await HtmlContent.ShowAsync(web, html);
            ring.IsActive = false;
            ring.Visibility = Visibility.Collapsed;
        };
        await ShowAsync(dialog);
        closed = true;
        web.Close();
    }

    /// <summary>
    /// Runs <paramref name="work"/> behind a "please wait" dialog that closes when it finishes. Cancel closes the
    /// dialog and cancels the token (throws <see cref="OperationCanceledException"/>).
    /// </summary>
    public static Task<T> BusyAsync<T>(string title, string message, Func<CancellationToken, Task<T>> work) =>
        BusyAsync(title, message, work, below: null);

    /// <summary>
    /// <see cref="BusyAsync{T}(string, string, Func{CancellationToken, Task{T}})"/> for work that downloads: a progress
    /// bar, plus the file and sizes when download details are on.
    /// </summary>
    public static Task<T> BusyAsync<T>(string title, string message, Func<IProgress<Core.Net.DownloadProgress>, CancellationToken, Task<T>> work)
    {
        var bar = new ProgressBar { Visibility = Visibility.Collapsed };
        var file = Caption();
        var transfer = Caption();
        var progress = new Progress<Core.Net.DownloadProgress>(p =>
        {
            var fraction = DownloadText.Fraction(p);
            bar.Visibility = fraction is null && p.TotalFiles == 0 ? Visibility.Collapsed : Visibility.Visible;
            bar.IsIndeterminate = fraction is null;
            bar.Value = 100 * (fraction ?? 0);
            var details = DownloadText.DetailsEnabled;
            file.Text = details && !p.Checking ? p.Current ?? "" : "";
            transfer.Text = details ? DownloadText.Summary(p) : "";
        });
        var below = new StackPanel { Spacing = 4, Children = { bar, file, transfer } };
        return BusyAsync(title, message, ct => work(progress, ct), below);

        static TextBlock Caption() => new()
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 520,
        };
    }

    private static async Task<T> BusyAsync<T>(string title, string message, Func<CancellationToken, Task<T>> work, UIElement? below)
    {
        using var cts = new CancellationTokenSource();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        row.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
        row.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 });
        var panel = new StackPanel { Spacing = 12, MinWidth = 360, Children = { row } };
        if (below is not null)
            panel.Children.Add(below);
        var dialog = Create(title, panel);
        Task<T>? task = null;
        dialog.Opened += (_, _) => task = RunAsync();
        async Task<T> RunAsync()
        {
            try
            {
                return await work(cts.Token);
            }
            finally
            {
                dialog.Hide();
            }
        }
        await ShowAsync(dialog);
        if (task is null)
            throw new OperationCanceledException();
        if (!task.IsCompleted)
            await cts.CancelAsync(); // The user pressed Cancel.
        return await task;
    }

    public static InfoBar Bar(string message, InfoBarSeverity severity, string? title = null) => new()
    {
        Title = title ?? "",
        Message = message,
        Severity = severity,
        IsOpen = true,
        IsClosable = false,
    };
}
