using System.Runtime;
using Microsoft.UI.Dispatching;

namespace Fluorite.App.Services;

/// <summary>
/// Gives memory back once things settle. The app's .NET heap is small (~10 MB), so the GC seldom runs by itself, yet
/// pages that were left (lists, icons, images, all native WinUI memory) are only released once the GC finalizes their
/// managed wrappers; and after a burst of work (scanning mods) the GC keeps the freed space reserved for later. So:
/// collect, let finalizers release native objects, then collect aggressively (compacting, returning free memory to
/// Windows) and compact the native heap. Each request restarts a short countdown, so a burst of requests trims once.
/// </summary>
public static class MemoryTrim
{
    private static DispatcherQueueTimer? s_timer;
    private static int s_pass;

    /// <summary>Call on the UI thread.</summary>
    public static void Schedule()
    {
        if (s_timer is null)
        {
            if (DispatcherQueue.GetForCurrentThread() is not { } queue)
                return;
            s_timer = queue.CreateTimer();
            s_timer.Interval = TimeSpan.FromSeconds(1.5);
            s_timer.Tick += (t, _) =>
            {
                if (++s_pass == 1)
                {
                    GC.Collect(); // Finalizers now release the native objects of what was left behind.
                    return;
                }
                t.Stop();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                // What WinUI freed sits in the native heap; let it go too.
                Platform.Windows.Diagnostics.NativeHeap.Compact();
            };
        }
        s_pass = 0;
        s_timer.Stop();
        s_timer.Start();
    }
}
