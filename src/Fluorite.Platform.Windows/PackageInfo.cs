using Fluorite.Platform.Windows.Interop;

namespace Fluorite.Platform.Windows;

/// <summary>Whether the current process runs with MSIX package identity.</summary>
public static class PackageInfo
{
    private static readonly Lazy<string?> s_fullName = new(Read);

    public static bool HasPackageIdentity => s_fullName.Value is not null;

    public static string? PackageFullName => s_fullName.Value;

    private static unsafe string? Read()
    {
        uint length = 0;
        var rc = NativeMethods.GetCurrentPackageFullName(ref length, null);
        if (rc == NativeMethods.APPMODEL_ERROR_NO_PACKAGE)
            return null;
        if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER || length == 0)
            return null;
        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            rc = NativeMethods.GetCurrentPackageFullName(ref length, p);
        }
        return rc == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }
}
