using System;
using System.Runtime.InteropServices;

namespace Halo.Interop;

// What the monitor under the pill actually is. Both numbers used to be assumed rather than read, and both
// assumptions were wrong in the same direction: the frame loop reached for a fixed 240 whatever the panel
// could show, and the pill was drawn in fixed physical pixels as though every display were 96dpi. Asking
// the display is the only way either can be honest, and on a multi-monitor machine the answer differs per
// monitor - a 280Hz laptop panel beside a 60Hz external is the normal case, not the exotic one.
internal static class Display
{
    internal readonly record struct Info(int Hz, float Dpi);

    private const int MONITOR_DEFAULTTOPRIMARY = 1;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CCHDEVICENAME = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Win32.RECT rcMonitor;
        public Win32.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string szDevice;
    }

    // The whole struct has to be declared even though only dmDisplayFrequency is read: EnumDisplaySettings
    // fills it by offset, and a short struct with the right dmSize still gets written past its end.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public uint dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    // Zero for either field means "could not read it", never a stand-in value - a made-up refresh rate
    // would silently pin the frame loop to a number no one chose, which is the same fault as a made-up
    // percentage on the pill.
    internal static Info Probe(IntPtr hwnd)
    {
        int hz = 0;
        float dpi = 0f;
        try
        {
            string? device = DeviceUnder(hwnd);
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            // null falls back to the primary display, which is the right answer when the window has no
            // monitor yet (boot, before the first Show).
            if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref mode)) hz = (int)mode.dmDisplayFrequency;
        }
        catch { }
        try
        {
            // the window's own monitor when there is one, the session's otherwise - at boot the pill has
            // not been shown yet, and answering 0 there would mean the first frames were laid out for a
            // display nobody has
            uint raw = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0;
            if (raw == 0) raw = GetDpiForSystem();
            if (raw > 0) dpi = raw / 96f;
        }
        catch { }
        return new Info(hz, dpi);
    }

    private static string? DeviceUnder(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return null;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
            if (mon == IntPtr.Zero) return null;
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            return GetMonitorInfo(mon, ref info) && info.szDevice.Length > 0 ? info.szDevice : null;
        }
        catch { return null; }
    }
}
