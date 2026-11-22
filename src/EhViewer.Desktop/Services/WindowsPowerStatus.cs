using System.Runtime.InteropServices;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Reads Windows AC/battery and Battery Saver status without WinRT package identity assumptions.
/// </summary>
public static class WindowsPowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    /// <summary>
    /// Returns <see langword="true"/> when the device is confirmed running on battery power
    /// (<c>ACLineStatus == 0</c>) or Windows Battery Saver is enabled (<c>SystemStatusFlag != 0</c>).
    /// Desktop PCs without batteries report <c>ACLineStatus == 1</c> / <c>BatteryFlag == 128</c>, returning <see langword="false"/>.
    /// </summary>
    public static bool IsOnBatteryOrSaver()
    {
        try
        {
            if (!GetSystemPowerStatus(out SystemPowerStatus status))
            {
                return false;
            }

            return status.ACLineStatus == 0 || status.SystemStatusFlag != 0;
        }
        catch
        {
            return false;
        }
    }
}
