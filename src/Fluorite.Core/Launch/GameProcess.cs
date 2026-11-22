using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Fluorite.Core.Launch;

public readonly record struct ProcessOutputLine(string Text, bool IsError, DateTimeOffset Timestamp);

/// <param name="Gpu">GPU to ask Windows for, for <paramref name="GpuExecutable"/> (the Java executable, which may differ from <paramref name="FileName"/> under a wrapper).</param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    GameProcessPriority Priority,
    GpuPreference Gpu = GpuPreference.Default,
    string? GpuExecutable = null);

/// <summary>A running game. Output is UTF-8 decoded line by line.</summary>
public interface IGameProcess : IDisposable
{
    int Id { get; }
    ChannelReader<ProcessOutputLine> Output { get; }
    Task<int> Exited { get; }

    /// <summary>Terminates the game (and, where supported, every child it spawned).</summary>
    void Kill();
}

public interface IGameProcessLauncher
{
    IGameProcess Start(ProcessSpec spec);
}

/// <summary>
/// Portable launcher based on <see cref="Process"/>. The Windows platform layer provides a richer one that breaks the
/// game away from the MSIX package and places it in a job object.
/// </summary>
public sealed class ManagedGameProcessLauncher : IGameProcessLauncher
{
    public IGameProcess Start(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in spec.Arguments)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment)
        {
            if (v is null)
                psi.Environment.Remove(k);
            else
                psi.Environment[k] = v;
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {spec.FileName}");
        try
        {
            process.PriorityClass = spec.Priority switch
            {
                GameProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
                GameProcessPriority.High => ProcessPriorityClass.High,
                GameProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
                GameProcessPriority.Idle => ProcessPriorityClass.Idle,
                _ => ProcessPriorityClass.Normal,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return new ManagedGameProcess(process);
    }

    private sealed class ManagedGameProcess : IGameProcess
    {
        private readonly Process _process;
        private readonly Channel<ProcessOutputLine> _output = Channel.CreateUnbounded<ProcessOutputLine>(new UnboundedChannelOptions { SingleReader = false });

        public ManagedGameProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            var stdout = PumpAsync(process.StandardOutput, isError: false);
            var stderr = PumpAsync(process.StandardError, isError: true);
            Exited = WaitAsync(stdout, stderr);
        }

        public int Id { get; }
        public ChannelReader<ProcessOutputLine> Output => _output.Reader;
        public Task<int> Exited { get; }

        private async Task PumpAsync(StreamReader reader, bool isError)
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                _output.Writer.TryWrite(new ProcessOutputLine(line, isError, DateTimeOffset.Now));
        }

        private async Task<int> WaitAsync(Task stdout, Task stderr)
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            _output.Writer.TryComplete();
            return _process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
