using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Fluorite.App;

/// <summary>
/// Custom entry point: enforces a single launcher instance and redirects protocol/file/jump-list
/// activations to the running instance.
/// </summary>
public static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WaitForRestartedInstance(args);

        if (RedirectedToExistingInstance())
            return 0;

        Application.Start(static _ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App().GetHashCode(); // Application.Current keeps the instance alive.
        });
        return 0;
    }

    /// <summary>
    /// One launcher per data root: the default root shares a single key; a custom root (FLUORITE_ROOT or a moved data
    /// folder) gets its own, so isolated test/portable setups don't hand activations to an unrelated window.
    /// </summary>
    private static string InstanceKey()
    {
        var root = Services.AppPaths.DataRoot;
        if (string.Equals(root, Services.AppPaths.DefaultRoot, StringComparison.OrdinalIgnoreCase))
            return "FluoriteLauncher.Main";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
        return "FluoriteLauncher.Main." + hash;
    }

    /// <summary>
    /// <c>--restarted &lt;pid&gt;</c>: started by a restart. The old process may still be ending; until it has, it holds
    /// the single-instance key and this launch would be handed to it (and lost).
    /// </summary>
    private static void WaitForRestartedInstance(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--restarted", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var pid))
            return;
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static bool RedirectedToExistingInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey());
        if (main.IsCurrent)
            return false;

        // Redirect on a worker thread while pumping COM on this STA thread (documented WinAppSDK pattern).
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEvent(false);
        _ = Task.Run(() =>
        {
            main.RedirectActivationToAsync(args).AsTask().Wait();
            done.Set();
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        return true;
    }

    /// <summary>Splits a launch-arguments string (jump list / shortcut) the way the C runtime would.</summary>
    public static IReadOnlyList<string> SplitArguments(string? arguments) => Core.Launch.CommandLine.Split(arguments);

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);
}
