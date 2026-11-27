namespace Fluorite.Core.Platform;

/// <summary>Deleting for good, past the Recycle Bin (Shift+Del).</summary>
public static class PermanentDelete
{
    /// <summary>
    /// A file, or a folder with everything in it, read-only files too. A link (junction, symlink) inside is removed
    /// itself: what it points to, somewhere else, stays. Other folders marked as reparse points (OneDrive's
    /// online-only ones, say) are ordinary folders here and are emptied first.
    /// </summary>
    public static void Run(string path)
    {
        if (Directory.Exists(path))
            DeleteDirectory(new DirectoryInfo(path));
        else if (File.Exists(path))
            DeleteFile(new FileInfo(path));
    }

    private static void DeleteDirectory(DirectoryInfo dir)
    {
        if (dir.LinkTarget is null)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo sub)
                    DeleteDirectory(sub);
                else
                    DeleteFile((FileInfo)entry);
            }
        }
        ClearReadOnly(dir);
        dir.Delete();
    }

    private static void DeleteFile(FileInfo file)
    {
        ClearReadOnly(file);
        file.Delete();
    }

    private static void ClearReadOnly(FileSystemInfo entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            entry.Attributes &= ~FileAttributes.ReadOnly;
    }
}
