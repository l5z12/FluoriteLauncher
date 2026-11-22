using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Fluorite.Platform.Windows.Shell;

public enum TaskbarProgressState
{
    None = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8,
}

/// <summary>Taskbar button progress (ITaskbarList3) via source-generated COM. Call from the UI thread.</summary>
public sealed partial class TaskbarProgress
{
    private static readonly Guid s_clsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private readonly ITaskbarList3? _taskbar;
    private readonly nint _hwnd;

    public TaskbarProgress(nint hwnd)
    {
        _hwnd = hwnd;
        try
        {
            var iid = typeof(ITaskbarList3).GUID;
            if (CoCreateInstance(s_clsidTaskbarList, 0, 1 /* CLSCTX_INPROC_SERVER */, iid, out var ptr) == 0)
            {
                _taskbar = (ITaskbarList3)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.UniqueInstance);
                Marshal.Release(ptr);
                _taskbar.HrInit();
            }
        }
        catch (COMException)
        {
            _taskbar = null; // Explorer not running (e.g. Windows Sandbox shells) — progress is cosmetic.
        }
    }

    public void SetState(TaskbarProgressState state) => _taskbar?.SetProgressState(_hwnd, state);

    /// <param name="fraction">0..1</param>
    public void SetValue(double fraction)
    {
        if (_taskbar is null)
            return;
        var completed = (ulong)(Math.Clamp(fraction, 0, 1) * 1000);
        _taskbar.SetProgressValue(_hwnd, completed, 1000);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}

/// <summary>ITaskbarList3 vtable up to SetProgressState (ITaskbarList → ITaskbarList2 → ITaskbarList3).</summary>
[GeneratedComInterface]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
internal partial interface ITaskbarList3
{
    void HrInit();
    void AddTab(nint hwnd);
    void DeleteTab(nint hwnd);
    void ActivateTab(nint hwnd);
    void SetActiveAlt(nint hwnd);
    void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    void SetProgressValue(nint hwnd, ulong completed, ulong total);
    void SetProgressState(nint hwnd, TaskbarProgressState state);
}
