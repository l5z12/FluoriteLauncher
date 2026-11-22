using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Fluorite.Core.Launch;
using Fluorite.Platform.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using static Fluorite.Platform.Windows.Interop.NativeMethods;

namespace Fluorite.Platform.Windows.Processes;

/// <summary>
/// Starts the game with <c>CreateProcessW</c> so that:
/// <list type="bullet">
/// <item>under MSIX it <b>breaks away from the package</b> (no file-system/registry virtualization for mods, and it
/// survives launcher updates/exit) via <c>PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY</c>;</item>
/// <item>it is placed in its own job object (no kill-on-close) so "Stop" terminates the whole tree;</item>
/// <item>only the stdio pipe handles are inherited (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>);</item>
/// <item>EcoQoS power throttling is disabled for it.</item>
/// </list>
/// </summary>
public sealed class BreakawayProcessLauncher : IGameProcessLauncher
{
    public bool BreakAwayFromPackage { get; init; } = true;
    public bool DisablePowerThrottling { get; init; } = true;

    public IGameProcess Start(ProcessSpec spec)
    {
        var commandLine = CommandLine.Quote(spec.FileName) + (spec.Arguments.Count > 0 ? " " + CommandLine.Join(spec.Arguments) : "");
        if (commandLine.Length >= 32767)
            throw new ArgumentException($"Command line is {commandLine.Length} characters; Windows allows 32766.");
        // Read by Windows when the process creates its graphics device, so it has to be in place before the start.
        Diagnostics.GpuPreferences.Apply(spec.GpuExecutable ?? spec.FileName, spec.Gpu);

        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        if (!CreatePipe(out var outRead, out var outWrite, ref sa, 0))
            throw new Win32Exception();
        if (!CreatePipe(out var errRead, out var errWrite, ref sa, 0))
            throw new Win32Exception();
        SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);

        var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        SetHandleInformation(nul, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception();

        PROCESS_INFORMATION pi;
        try
        {
            var breakaway = BreakAwayFromPackage && PackageInfo.HasPackageIdentity;
            pi = CreateWithFallback(spec, commandLine, nul, outWrite, errWrite, breakaway);
        }
        finally
        {
            // The child owns its copies now.
            outWrite.Dispose();
            errWrite.Dispose();
            nul.Dispose();
        }

        var processHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
        var inJob = AssignProcessToJobObject(job, pi.hProcess);

        if (DisablePowerThrottling)
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0,
            };
            SetProcessInformation(processHandle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }

        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);

        return new NativeGameProcess(pi.dwProcessId, processHandle, inJob ? job : DisposeAndNull(job), outRead, errRead);
    }

    private static SafeJobHandle? DisposeAndNull(SafeJobHandle job)
    {
        job.Dispose();
        return null;
    }

    private static PROCESS_INFORMATION CreateWithFallback(ProcessSpec spec, string commandLine, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr, bool breakaway)
    {
        // Try the most isolated configuration first, then degrade (e.g. a parent job that forbids breakaway).
        var attempts = new List<(bool PackageBreakaway, bool JobBreakaway)>
        {
            (breakaway, true),
            (breakaway, false),
        };
        if (breakaway)
            attempts.Add((false, false));

        Win32Exception? last = null;
        foreach (var (pkg, jobBreak) in attempts)
        {
            if (TryCreate(spec, commandLine, stdin, stdout, stderr, pkg, jobBreak, out var pi, out var error))
                return pi;
            last = new Win32Exception(error);
            if (error != ERROR_ACCESS_DENIED && error != 87 /* ERROR_INVALID_PARAMETER */)
                break;
        }
        throw new InvalidOperationException($"Failed to start '{spec.FileName}': {last?.Message}", last);
    }

    private static unsafe bool TryCreate(ProcessSpec spec, string commandLine, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr,
        bool packageBreakaway, bool jobBreakaway, out PROCESS_INFORMATION pi, out int error)
    {
        var attributeCount = packageBreakaway ? 2 : 1;
        IntPtr size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
        var attrList = Marshal.AllocHGlobal(size);
        var handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
        var policy = Marshal.AllocHGlobal(sizeof(uint));
        var envBlock = IntPtr.Zero;
        var listInitialized = false;
        var added = new List<SafeHandle> { stdin, stdout, stderr };
        foreach (var h in added)
        {
            var ok = false;
            h.DangerousAddRef(ref ok);
        }

        try
        {
            if (!InitializeProcThreadAttributeList(attrList, attributeCount, 0, ref size))
                throw new Win32Exception();
            listInitialized = true;

            Marshal.WriteIntPtr(handles, 0, stdin.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, stdout.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, stderr.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, IntPtr.Size * 3, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            if (packageBreakaway)
            {
                // ENABLE_PROCESS_TREE, despite the docs describing it as only about the new process's children: tested
                // inside the package (Windows 11, 2026-11), the game itself starts outside with it, and so do its
                // children. OVERRIDE (0x4) does the opposite and keeps the game in the package, so its windows group
                // under Fluorite's taskbar button.
                Marshal.WriteInt32(policy, (int)PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE);
                if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY, policy, sizeof(uint), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
            }

            var si = new STARTUPINFOEXW();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = stdin.DangerousGetHandle();
            si.StartupInfo.hStdOutput = stdout.DangerousGetHandle();
            si.StartupInfo.hStdError = stderr.DangerousGetHandle();
            si.lpAttributeList = attrList;

            var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | CREATE_NO_WINDOW | PriorityFlag(spec.Priority);
            if (jobBreakaway)
                flags |= CREATE_BREAKAWAY_FROM_JOB;

            envBlock = BuildEnvironmentBlock(spec.Environment);
            var cmd = (commandLine + '\0').ToCharArray(); // CreateProcessW may modify the buffer.
            fixed (char* pCmd = cmd)
            {
                if (CreateProcess(null, pCmd, IntPtr.Zero, IntPtr.Zero, true, flags, envBlock, spec.WorkingDirectory, ref si, out pi))
                {
                    error = 0;
                    return true;
                }
            }
            error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            if (listInitialized)
                DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(policy);
            if (envBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(envBlock);
            foreach (var h in added)
                h.DangerousRelease();
        }
    }

    private static uint PriorityFlag(GameProcessPriority p) => p switch
    {
        GameProcessPriority.AboveNormal => ABOVE_NORMAL_PRIORITY_CLASS,
        GameProcessPriority.High => HIGH_PRIORITY_CLASS,
        GameProcessPriority.BelowNormal => BELOW_NORMAL_PRIORITY_CLASS,
        GameProcessPriority.Idle => IDLE_PRIORITY_CLASS,
        _ => NORMAL_PRIORITY_CLASS,
    };

    /// <summary>Current environment plus overrides (null value removes), sorted case-insensitively as Windows requires.</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (k, v) in overrides)
        {
            if (v is null)
                env.Remove(k);
            else
                env[k] = v;
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in env)
            sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private sealed class NativeGameProcess : IGameProcess
    {
        private readonly SafeProcessHandle _process;
        private readonly SafeJobHandle? _job;
        private readonly Channel<ProcessOutputLine> _output = Channel.CreateUnbounded<ProcessOutputLine>();

        public NativeGameProcess(int pid, SafeProcessHandle process, SafeJobHandle? job, SafeFileHandle stdout, SafeFileHandle stderr)
        {
            Id = pid;
            _process = process;
            _job = job;
            var o = Task.Factory.StartNew(() => Pump(stdout, false), TaskCreationOptions.LongRunning);
            var e = Task.Factory.StartNew(() => Pump(stderr, true), TaskCreationOptions.LongRunning);
            Exited = Task.Factory.StartNew(() =>
            {
                WaitForSingleObject(_process, INFINITE);
                GetExitCodeProcess(_process, out var code);
                // Pipes close once every process holding the write ends exits; don't hang on grandchildren.
                Task.WaitAll([o, e], TimeSpan.FromSeconds(5));
                _output.Writer.TryComplete();
                return unchecked((int)code);
            }, TaskCreationOptions.LongRunning);
        }

        public int Id { get; }
        public ChannelReader<ProcessOutputLine> Output => _output.Reader;
        public Task<int> Exited { get; }

        private void Pump(SafeFileHandle handle, bool isError)
        {
            using var fs = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(fs, new UTF8Encoding(false));
            try
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                    _output.Writer.TryWrite(new ProcessOutputLine(line, isError, DateTimeOffset.Now));
            }
            catch (IOException)
            {
                // Broken pipe on exit.
            }
        }

        public void Kill()
        {
            if (_job is not null && !_job.IsClosed && TerminateJobObject(_job, 1))
                return;
            TerminateProcess(_process, 1);
        }

        public void Dispose()
        {
            _job?.Dispose();
            _process.Dispose();
        }
    }
}
