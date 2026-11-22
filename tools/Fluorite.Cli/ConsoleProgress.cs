using System.Globalization;
using Fluorite.Core.Launch;
using Fluorite.Core.Net;

namespace Fluorite.Cli;

/// <summary>Single-line console progress for launch stages and downloads.</summary>
internal sealed class ConsoleProgress : IProgress<LaunchProgress>, IProgress<DownloadProgress>
{
    /// <summary>Plain download progress (modpack installs) shows as the "Downloading" stage.</summary>
    public void Report(DownloadProgress value) => Report(new LaunchProgress(LaunchStage.DownloadingLibraries, "Downloading", value));

    private readonly object _gate = new();
    private LaunchStage? _lastStage;
    private readonly bool _interactive = !Console.IsOutputRedirected;

    public void Report(LaunchProgress value)
    {
        lock (_gate)
        {
            if (_lastStage != value.Stage)
            {
                if (_lastStage is not null && _interactive)
                    Console.WriteLine();
                _lastStage = value.Stage;
                if (!_interactive)
                    Console.WriteLine($"[{value.Stage}] {value.Detail}");
            }
            if (!_interactive)
                return;

            var line = value.Download is { } d ? $"[{value.Stage}] {Format(d)}" : $"[{value.Stage}] {value.Detail}";
            var width = Math.Max(Console.WindowWidth - 1, 40);
            Console.Write("\r" + (line.Length > width ? line[..width] : line.PadRight(width)));
        }
    }

    public static string Format(DownloadProgress d)
    {
        var mb = d.DownloadedBytes / 1048576.0;
        var total = d.TotalBytes is { } t ? $"/{t / 1048576.0:F1}" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{d.CompletedFiles}/{d.TotalFiles} files  {mb:F1}{total} MB  {d.BytesPerSecond / 1048576.0:F1} MB/s");
    }

    public void Done()
    {
        lock (_gate)
        {
            if (_interactive && _lastStage is not null)
                Console.WriteLine();
        }
    }
}
