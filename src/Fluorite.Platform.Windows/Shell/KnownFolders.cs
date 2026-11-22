using System.Runtime.InteropServices;

namespace Fluorite.Platform.Windows.Shell;

public static partial class KnownFolders
{
    private static readonly Guid s_downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder (honours redirection), or <c>%USERPROFILE%\Downloads</c>.</summary>
    public static string Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(in s_downloads, 0, 0, out var ptr) == 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(ptr) is { Length: > 0 } path)
                        return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
