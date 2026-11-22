using Markdig;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Fluorite.App.Services;

/// <summary>
/// Renders third-party rich text (Mojang patch notes, Modrinth Markdown, CurseForge HTML) in a WebView2 with the app's
/// theme. Scripts are disabled and a CSP blocks everything but images and inline styles; links open in the browser.
/// </summary>
public static class HtmlContent
{
    private static readonly MarkdownPipeline s_markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string FromMarkdown(string markdown) => Markdown.ToHtml(markdown, s_markdown);

    public static string Page(string bodyHtml, bool dark)
    {
        var (fg, muted, link, code, border) = dark
            ? ("#FFFFFF", "#C5C5C5", "#B39DFF", "#2D2D2D", "#3D3D3D")
            : ("#1B1B1B", "#5C5C5C", "#6B3FD9", "#F0F0F0", "#E0E0E0");
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: data:; style-src 'unsafe-inline'">
            <style>
              html { color-scheme: {{(dark ? "dark" : "light")}}; }
              body { font-family: 'Segoe UI Variable Text', 'Segoe UI', 'Microsoft YaHei UI', sans-serif; font-size: 14px; line-height: 1.6;
                     color: {{fg}}; background: transparent; margin: 16px 24px; overflow-wrap: anywhere; }
              h1 { font-size: 22px; font-weight: 600; margin: 24px 0 8px; }
              h2 { font-size: 18px; font-weight: 600; margin: 20px 0 6px; }
              h3, h4 { font-size: 15px; font-weight: 600; margin: 16px 0 4px; }
              li { margin: 2px 0; }
              li, blockquote { color: {{muted}}; }
              a { color: {{link}}; }
              code { background: {{code}}; padding: 1px 4px; border-radius: 4px; font-family: 'Cascadia Mono', Consolas, monospace; }
              pre { background: {{code}}; padding: 8px 12px; border-radius: 6px; overflow-x: auto; }
              pre code { padding: 0; }
              img, video { max-width: 100%; height: auto; border-radius: 6px; }
              table { border-collapse: collapse; }
              td, th { border: 1px solid {{border}}; padding: 4px 8px; }
              blockquote { border-left: 3px solid {{border}}; margin: 8px 0; padding-left: 12px; }
              hr { border: none; border-top: 1px solid {{border}}; }
              iframe { display: none; }
            </style></head><body>{{bodyHtml}}</body></html>
            """;
    }

    /// <summary>
    /// Shows <paramref name="bodyHtml"/> in <paramref name="view"/> with safe settings. Never throws: false when the
    /// WebView2 runtime is missing or the view was closed meanwhile (its dialog or page went away while the text
    /// loaded). Callers run in async void handlers, where an escaping exception ends the app.
    /// </summary>
    public static async Task<bool> ShowAsync(WebView2 view, string bodyHtml)
    {
        try
        {
            await view.EnsureCoreWebView2Async();
            if (view.CoreWebView2 is not { } core)
                return false;
            if (view.Tag is not "configured")
            {
                view.Tag = "configured";
                view.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
                core.Settings.IsScriptEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += OnNewWindowRequested;
            }
            view.NavigateToString(Page(bodyHtml, view.ActualTheme == ElementTheme.Dark));
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.StartsWith("data:", StringComparison.Ordinal) || args.Uri.StartsWith("about:", StringComparison.Ordinal))
            return;
        args.Cancel = true;
        OpenExternal(args.Uri);
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        OpenExternal(args.Uri);
    }

    public static void OpenExternal(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
    }
}
