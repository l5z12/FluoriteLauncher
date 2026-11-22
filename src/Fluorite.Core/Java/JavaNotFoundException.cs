namespace Fluorite.Core.Java;

public sealed class JavaNotFoundException(int major, Exception? installError)
    : Exception($"No usable Java {major} runtime is installed" + (installError is null ? "." : $" and installing one failed: {installError.Message}"), installError)
{
    public int RequiredMajor { get; } = major;
}
