using System.Diagnostics;
using Fluorite.Core.Platform;

namespace Fluorite.Core.Tests;

public class PermanentDeleteTests
{
    [Fact]
    public void DeletesReadOnlyFiles_AndLeavesWhatAJunctionPointsTo()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluorite-permdelete-" + Guid.NewGuid().ToString("N"));
        var doomed = Path.Combine(root, "world");
        var elsewhere = Path.Combine(root, "elsewhere");
        try
        {
            Directory.CreateDirectory(Path.Combine(doomed, "region"));
            var readOnly = Path.Combine(doomed, "region", "r.0.0.mca");
            File.WriteAllText(readOnly, "x");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            Directory.CreateDirectory(elsewhere);
            File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
            // A junction needs no admin rights or developer mode, unlike a symlink.
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(doomed, "link")}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                mklink.WaitForExit();
            Assert.True(Directory.Exists(Path.Combine(doomed, "link")));

            PermanentDelete.Run(doomed);

            Assert.False(Directory.Exists(doomed));
            Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>Whatever a failed test left, read-only files too, without hiding the failure behind its own.</summary>
    private static void Cleanup(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void DeletesASingleReadOnlyFile_AndIgnoresWhatIsGone()
    {
        var file = Path.Combine(Path.GetTempPath(), "fluorite-permdelete-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        PermanentDelete.Run(file);
        PermanentDelete.Run(file);

        Assert.False(File.Exists(file));
    }
}
