using System.Runtime.InteropServices;

namespace Fluorite.Platform.Windows.Diagnostics;

/// <summary>The process heap, where WinUI and the runtime keep their native objects.</summary>
public static partial class NativeHeap
{
    /// <summary>
    /// Coalesces free blocks and decommits the free memory it can, so memory freed after a page or list went away
    /// stops counting against the process instead of staying committed for reuse.
    /// </summary>
    public static void Compact() => _ = HeapCompact(GetProcessHeap(), 0);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetProcessHeap();

    [LibraryImport("kernel32.dll")]
    private static partial nuint HeapCompact(nint heap, uint flags);
}
