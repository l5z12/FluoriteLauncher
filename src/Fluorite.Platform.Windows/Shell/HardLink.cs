using System.Runtime.InteropServices;

namespace Fluorite.Platform.Windows.Shell;

/// <summary>A second name for an existing file: instant and takes no space (snapshots of mod jars).</summary>
public static partial class HardLink
{
    /// <summary>
    /// Makes <paramref name="link"/> another name for the file <paramref name="existing"/>. False when the file system
    /// can't (another volume, FAT, a network share without support…): copy instead.
    /// </summary>
    public static bool TryCreate(string link, string existing) => CreateHardLinkW(link, existing, 0);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);
}
