using System.Globalization;
using System.IO.Compression;
using System.Text;
using Fluorite.Core;
using Fluorite.Core.Diagnostics;
using Fluorite.Platform.Windows.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Fluorite.App.Services;

/// <summary>A crash finding as the UI shows it.</summary>
public sealed record CrashFindingItem(string Title, string Advice, string? Details, bool IsPossible)
{
    public static CrashFindingItem From(CrashFinding f)
    {
        var args = f.Args.Cast<object?>().ToArray();
        string advice;
        try
        {
            advice = Loc.Format($"Crash_{f.Id}_Advice", args);
        }
        catch (FormatException)
        {
            advice = Loc.Get($"Crash_{f.Id}_Advice");
        }
        return new CrashFindingItem(Loc.Get($"Crash_{f.Id}_Title"), advice, f.Details, f.Confidence == CrashConfidence.Possible);
    }

    public bool HasDetails => !string.IsNullOrEmpty(Details);
}

/// <summary>Sharing a crash: the log on mclo.gs, or everything that helps in a zip.</summary>
public sealed class DiagnosticsService(MclogsClient mclogs)
{
    /// <summary>Uploads the session's log (redacted) after the user agrees, then offers the link.</summary>
    public async Task UploadLogAsync(RunningGame game)
    {
        var confirm = new StackPanel { Spacing = 8 };
        confirm.Children.Add(Dialogs.Text(Loc.Get("Diag_UploadConfirm")));
        confirm.Children.Add(Dialogs.Text(Loc.Get("Diag_UploadRedacted"), secondary: true));
        if (await Dialogs.ShowAsync(Loc.Get("Diag_UploadTitle"), confirm, Loc.Get("Diag_UploadButton")) != ContentDialogResult.Primary)
            return;

        var text = LogRedactor.Redact(game.Archive.ToText(), game.Secrets);
        Uri url;
        try
        {
            url = await Dialogs.BusyAsync(Loc.Get("Diag_UploadTitle"), Loc.Get("Diag_Uploading"), ct => mclogs.UploadAsync(text, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or MclogsException)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Diag_UploadTitle"), Loc.Format("Diag_UploadFailed", ex.Message));
            return;
        }

        CopyText(url.ToString()); // What the user wants next is to paste it.
        var done = new StackPanel { Spacing = 8 };
        done.Children.Add(Dialogs.Text(Loc.Get("Diag_Uploaded")));
        done.Children.Add(new HyperlinkButton { Content = url.ToString(), NavigateUri = url, Padding = new Thickness(0) });
        await Dialogs.ShowAsync(Dialogs.Create(Loc.Get("Diag_UploadTitle"), done, close: Loc.Get("Common_Close")));
    }

    /// <summary>Asks where to save, then writes the zip and shows it in Explorer.</summary>
    public async Task SaveBundleAsync(RunningGame game)
    {
        if (App.Window is not { } window)
            return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(window.AppWindow.Id)
        {
            SuggestedFileName = $"{SafeName(game.Name)} diagnostics {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}",
            DefaultFileExtension = ".zip",
        };
        picker.FileTypeChoices.Add(Loc.Get("Diag_ZipFiles"), new List<string> { ".zip" });
        if ((await picker.PickSaveFileAsync())?.Path is not { } path)
            return;
        try
        {
            await Task.Run(() => WriteBundle(path, game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Diag_SaveTitle"), Loc.Format("Diag_SaveFailed", ex.Message));
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    public static void OpenFile(string? path)
    {
        if (path is not null && File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void CopyText(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "Minecraft" : safe;
    }

    /// <summary>
    /// The session log, the game's own logs and crash reports from this session, Java's crash log, the launch
    /// command and a system summary, all with tokens and the Windows user name removed.
    /// </summary>
    private static void WriteBundle(string path, RunningGame game)
    {
        var secrets = game.Secrets;
        var since = game.StartedAt?.UtcDateTime.AddSeconds(-5);
        var temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Add(zip, "session.log", game.Archive.ToText());
            Add(zip, "system.txt", SystemSummary(game));
            if (game.Launched?.Prepared.Command is { } command)
                Add(zip, "launch-command.txt", command.ToDisplayString(secrets));

            var gameDir = game.Instance.GameDirectory;
            foreach (var log in new[] { "latest.log", "debug.log" })
                AddFile(zip, "logs/" + log, Path.Combine(gameDir, "logs", log), since, 16 * 1024 * 1024);
            AddFile(zip, "crash-report.txt", game.Diagnosis?.CrashReportFile, null, 4 * 1024 * 1024);
            AddFile(zip, Path.GetFileName(game.Diagnosis?.JvmCrashFile ?? "hs_err.log"), game.Diagnosis?.JvmCrashFile, null, 4 * 1024 * 1024);
        }
        File.Move(temp, path, overwrite: true);

        void Add(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(LogRedactor.Redact(text, secrets));
        }

        void AddFile(ZipArchive zip, string name, string? file, DateTime? newerThan, int maxBytes)
        {
            try
            {
                if (file is null || !File.Exists(file) || (newerThan is { } t && File.GetLastWriteTimeUtc(file) < t))
                    return;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Keep the end of an oversized log: that's where the crash is.
                if (stream.Length > maxBytes)
                    stream.Seek(-maxBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                Add(zip, name, reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string SystemSummary(RunningGame game)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"Fluorite {FluoriteInfo.Version}");
        sb.AppendLine(inv, $"Windows: {SystemInfo.WindowsVersion}");
        if (SystemInfo.ProcessorName is { } cpu)
            sb.AppendLine(inv, $"CPU: {cpu}");
        sb.AppendLine(inv, $"Memory: {Core.Launch.MemoryAdvisor.SystemMemoryMb / 1024.0:0.0} GB");
        foreach (var gpu in SystemInfo.GraphicsAdapters())
            sb.AppendLine(inv, $"Graphics: {gpu.Name} (driver {gpu.DriverVersion ?? "?"}{(gpu.DriverDate is { } d ? ", " + d : "")})");
        sb.AppendLine();

        if (game.Launched?.Prepared is { } prepared)
        {
            sb.AppendLine(inv, $"Java: {prepared.Java}");
            sb.AppendLine(inv, $"Version: {prepared.Version.Id} (needs Java {prepared.Version.JavaVersion?.MajorVersion.ToString(inv) ?? "8"})");
        }
        var manifest = game.Instance.Manifest;
        sb.AppendLine(inv, $"Instance: {game.Instance.Name}");
        foreach (var c in manifest.Components)
            sb.AppendLine(inv, $"  {c.Uid} {c.Version}{(c.ProfileId is { } p ? " (" + p + ")" : "")}");
        sb.AppendLine(inv, $"Exit code: {game.ExitCode?.ToString(inv) ?? "-"}");
        sb.AppendLine(inv, $"Played: {game.PlayTime}");
        sb.AppendLine();

        if (game.Diagnosis is { Findings.Count: > 0 } diagnosis)
        {
            sb.AppendLine("Analysis:");
            foreach (var f in diagnosis.Findings)
                sb.AppendLine(inv, $"  {f.Id}{(f.Args.Count > 0 ? ": " + string.Join(" | ", f.Args) : "")}");
            sb.AppendLine();
        }

        var mods = Path.Combine(game.Instance.GameDirectory, "mods");
        try
        {
            if (Directory.Exists(mods))
            {
                var files = new DirectoryInfo(mods).GetFiles().Where(f => f.Extension is ".jar" or ".disabled" or ".zip").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                sb.AppendLine(inv, $"Mods ({files.Count(f => f.Extension != ".disabled")} enabled):");
                foreach (var f in files)
                    sb.AppendLine(inv, $"  {f.Name}  {f.Length / 1024} KB");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return sb.ToString();
    }
}
