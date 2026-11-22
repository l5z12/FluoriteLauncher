using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.Core.Net;

namespace Fluorite.App.Services;

/// <summary>
/// Status line, progress bar and download details shared by launches and content jobs. Details (the file being
/// downloaded, counts, sizes, speed) follow Settings › Downloads › Show download details.
/// </summary>
public abstract partial class ProgressItem : ObservableObject
{
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;

    /// <summary>The file being downloaded (empty when there's none, or details are off).</summary>
    [ObservableProperty] public partial string CurrentFile { get; set; } = "";

    /// <summary>"12 of 40 files · 4.5 MB of 20 MB · 3.1 MB/s" (empty when nothing downloads, or details are off).</summary>
    [ObservableProperty] public partial string Transfer { get; set; } = "";

    /// <summary>Moves the bar and fills in the details from a download's progress.</summary>
    public void Report(DownloadProgress p)
    {
        if (DownloadText.Fraction(p) is { } fraction)
        {
            IsIndeterminate = false;
            Progress = 100 * fraction;
        }
        else
        {
            IsIndeterminate = true;
        }
        var details = DownloadText.DetailsEnabled;
        CurrentFile = details && !p.Checking ? p.Current ?? "" : "";
        Transfer = details ? DownloadText.Summary(p) : "";
    }

    /// <summary>Back to a plain status line (the step after a download, or the end).</summary>
    public void ClearDownload(bool indeterminate = true)
    {
        CurrentFile = "";
        Transfer = "";
        IsIndeterminate = indeterminate;
    }
}

/// <summary>Formats <see cref="DownloadProgress"/> for people.</summary>
public static class DownloadText
{
    private const string Separator = " · ";

    public static bool DetailsEnabled => App.Get<SettingsService>().Current.ShowDownloadDetails;

    /// <summary>0–1, by bytes when every size is known, else by files; null when there's nothing to measure yet.</summary>
    public static double? Fraction(DownloadProgress p)
    {
        if (p.Checking || p.TotalFiles == 0)
            return null;
        if (p.TotalBytes is long total && total > 0)
            return Math.Clamp((double)p.DownloadedBytes / total, 0, 1);
        return p.TotalFiles > 1 ? (double)p.CompletedFiles / p.TotalFiles : null;
    }

    /// <summary>"12 of 40 files · 4.5 MB of 20 MB · 3.1 MB/s"; "Checking 300 of 3,412 files…" before downloading.</summary>
    public static string Summary(DownloadProgress p)
    {
        if (p.Checking)
            return p.TotalFiles > 1 ? Loc.Format("Download_Checking", Count(p.CompletedFiles), Count(p.TotalFiles)) : "";
        if (p.TotalFiles == 0)
            return "";
        var parts = new List<string>(3);
        if (p.TotalFiles > 1)
            parts.Add(Loc.Format("Download_Files", Count(p.CompletedFiles), Count(p.TotalFiles)));
        if (p.TotalBytes is long total && total > 0)
            parts.Add(Loc.Format("Download_Of", Size(p.DownloadedBytes), Size(total)));
        else if (p.DownloadedBytes > 0)
            parts.Add(Size(p.DownloadedBytes));
        if (p.CompletedFiles < p.TotalFiles && p.BytesPerSecond >= 1)
            parts.Add(Loc.Format("Download_Speed", Size((long)p.BytesPerSecond)));
        return string.Join(Separator, parts);
    }

    public static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Size(long bytes)
    {
        var c = CultureInfo.CurrentCulture;
        return bytes switch
        {
            < 1024 => string.Format(c, "{0} B", bytes),
            < 1024 * 1024 => string.Format(c, "{0:0} KB", bytes / 1024.0),
            < 1024L * 1024 * 1024 => string.Format(c, "{0:0.0} MB", bytes / 1048576.0),
            _ => string.Format(c, "{0:0.00} GB", bytes / 1073741824.0),
        };
    }
}
