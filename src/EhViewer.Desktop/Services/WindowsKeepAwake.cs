using System.Runtime.InteropServices;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Ref-counted Windows display sleep prevention (<c>SetThreadExecutionState</c>) for active
/// reader views so reading does not dim or lock the screen when <c>KeepScreenOn</c> is enabled.
/// </summary>
public static class WindowsKeepAwake
{
    private const uint EsContinuous = 0x80000000u;
    private const uint EsSystemRequired = 0x00000001u;
    private const uint EsDisplayRequired = 0x00000002u;

    private static readonly object Gate = new();
    private static int _activeCount;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public static int ActiveLockCount
    {
        get
        {
            lock (Gate)
            {
                return _activeCount;
            }
        }
    }

    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            _activeCount++;
            if (_activeCount == 1)
            {
                TrySetState(EsContinuous | EsDisplayRequired | EsSystemRequired);
            }
        }

        return new LockHandle();
    }

    private static void Release()
    {
        lock (Gate)
        {
            if (_activeCount <= 0)
            {
                return;
            }

            _activeCount--;
            if (_activeCount == 0)
            {
                TrySetState(EsContinuous);
            }
        }
    }

    private static void TrySetState(uint flags)
    {
        try
        {
            _ = SetThreadExecutionState(flags);
        }
        catch
        {
        }
    }

    private sealed class LockHandle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release();
            }
        }
    }
}
