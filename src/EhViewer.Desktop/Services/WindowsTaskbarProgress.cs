using System.Runtime.InteropServices;
using EhViewer.Desktop.Downloads;

namespace EhViewer.Desktop.Services;

public enum TaskbarProgressState
{
    NoProgress = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8
}

public readonly record struct TaskbarProgressReport(
    TaskbarProgressState State,
    ulong Completed,
    ulong Total);

/// <summary>
/// Pushes aggregate gallery and archiver download progress to the Windows 10/11 taskbar button
/// via COM <c>ITaskbarList3</c>. No-ops safely on headless test runners or unsupported shells.
/// </summary>
public sealed class WindowsTaskbarProgress : IDisposable
{
    private nint _hwnd;
    private ITaskbarList3? _taskbar;
    private bool _disposed;

    public bool IsInitialized => _taskbar is not null && _hwnd != 0;

    public void Initialize(nint hwnd)
    {
        if (_disposed || hwnd == 0)
        {
            return;
        }

        _hwnd = hwnd;
        try
        {
            var instance = (ITaskbarList3)(object)new CTaskbarList();
            if (instance.HrInit() == 0)
            {
                _taskbar = instance;
            }
            else
            {
                Marshal.ReleaseComObject(instance);
            }
        }
        catch
        {
            _taskbar = null;
        }
    }

    public static TaskbarProgressReport ComputeAggregateProgress(
        IReadOnlyList<DownloadProgress> tasks,
        IReadOnlyList<ArchiverTaskSnapshot> archivers)
    {
        bool anyRunning = false;
        bool anyPaused = false;
        bool anyError = false;
        bool anyIndeterminate = false;
        ulong completed = 0;
        ulong total = 0;

        foreach (DownloadProgress t in tasks)
        {
            switch (t.State)
            {
                case DownloadState.Wait:
                case DownloadState.Download:
                case DownloadState.Update:
                    if (t.IsPaused)
                    {
                        anyPaused = true;
                    }
                    else
                    {
                        anyRunning = true;
                    }

                    if (t.Total > 0)
                    {
                        completed += (ulong)Math.Clamp(t.Done, 0, t.Total);
                        total += (ulong)t.Total;
                    }
                    else
                    {
                        anyIndeterminate = true;
                    }

                    break;

                case DownloadState.None when t.IsPaused && t.Total > 0:
                    anyPaused = true;
                    completed += (ulong)Math.Clamp(t.Done, 0, t.Total);
                    total += (ulong)t.Total;
                    break;

                case DownloadState.Failed:
                    anyError = true;
                    break;
            }
        }

        foreach (ArchiverTaskSnapshot a in archivers)
        {
            switch (a.State)
            {
                case ArchiverTaskState.Queued:
                case ArchiverTaskState.Running:
                    anyRunning = true;
                    if (a.TotalBytes is > 0)
                    {
                        completed += (ulong)Math.Clamp(a.BytesReceived, 0L, a.TotalBytes.Value);
                        total += (ulong)a.TotalBytes.Value;
                    }
                    else
                    {
                        anyIndeterminate = true;
                    }

                    break;

                case ArchiverTaskState.Paused:
                    anyPaused = true;
                    if (a.TotalBytes is > 0)
                    {
                        completed += (ulong)Math.Clamp(a.BytesReceived, 0L, a.TotalBytes.Value);
                        total += (ulong)a.TotalBytes.Value;
                    }

                    break;

                case ArchiverTaskState.Failed:
                    anyError = true;
                    break;
            }
        }

        if (!anyRunning && !anyPaused && !anyError)
        {
            return new TaskbarProgressReport(TaskbarProgressState.NoProgress, 0, 0);
        }

        if (anyRunning)
        {
            if (total > 0)
            {
                return new TaskbarProgressReport(
                    anyError ? TaskbarProgressState.Error : TaskbarProgressState.Normal,
                    completed,
                    total);
            }

            return new TaskbarProgressReport(
                anyIndeterminate ? TaskbarProgressState.Indeterminate : TaskbarProgressState.Normal,
                completed,
                total);
        }

        if (anyPaused)
        {
            return total > 0
                ? new TaskbarProgressReport(TaskbarProgressState.Paused, completed, total)
                : new TaskbarProgressReport(TaskbarProgressState.Paused, 1, 1);
        }

        return new TaskbarProgressReport(TaskbarProgressState.NoProgress, 0, 0);
    }

    public void Update(TaskbarProgressReport report)
    {
        if (_disposed || _taskbar is null || _hwnd == 0)
        {
            return;
        }

        try
        {
            _taskbar.SetProgressState(_hwnd, (TBPFLAG)report.State);
            if (report.Total > 0 &&
                report.State is TaskbarProgressState.Normal or TaskbarProgressState.Paused or TaskbarProgressState.Error)
            {
                _taskbar.SetProgressValue(_hwnd, report.Completed, report.Total);
            }
        }
        catch
        {
            // Ignore transient shell errors (e.g. Explorer restart).
        }
    }

    public void Clear() => Update(new TaskbarProgressReport(TaskbarProgressState.NoProgress, 0, 0));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ITaskbarList3? taskbar = _taskbar;
        _taskbar = null;
        if (taskbar is not null)
        {
            try
            {
                if (_hwnd != 0)
                {
                    taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_NOPROGRESS);
                }

                Marshal.ReleaseComObject(taskbar);
            }
            catch
            {
            }
        }

        _hwnd = 0;
    }

    private enum TBPFLAG
    {
        TBPF_NOPROGRESS = 0,
        TBPF_INDETERMINATE = 0x1,
        TBPF_NORMAL = 0x2,
        TBPF_ERROR = 0x4,
        TBPF_PAUSED = 0x8
    }

    [ComImport]
    [Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CTaskbarList
    {
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);

        [PreserveSig] int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        [PreserveSig] int SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] int SetProgressState(nint hwnd, TBPFLAG tbpFlags);
    }
}
