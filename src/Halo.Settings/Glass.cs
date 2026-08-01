using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Halo.Settings;

// The window's material, asked of the compositor rather than painted.
//
// This is the same path WinUI's DesktopAcrylicBackdrop takes - DWMWA_SYSTEMBACKDROP_TYPE - so the look
// does not depend on the Windows App SDK being present. The four things that have to be right together,
// and the four the earlier WPF attempt got wrong:
//
//   * the frame is extended on ALL FOUR margins (-1). Extending only the left is what produced the
//     "inset" down one side of that screenshot.
//   * the window's own background is transparent, or the backdrop is behind an opaque sheet and never
//     seen. Everything drawn on top is a low-alpha frost for the same reason.
//   * dark mode is set before the first paint, or the caption area flashes light and the system's own
//     rounding is drawn in the wrong colour.
//   * rounded corners are asked for explicitly, because a WS_POPUP-less window with a custom chrome
//     does not get them by default.
//
// Every call is best-effort: on a build that does not know an attribute, DwmSetWindowAttribute fails and
// the window is simply a dark panel, which is a worse look and not a broken one.
internal static class Glass
{
    private const int Backdrop = 38;      // DWMWA_SYSTEMBACKDROP_TYPE
    private const int DarkMode = 20;      // DWMWA_USE_IMMERSIVE_DARK_MODE
    private const int CornerStyle = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE

    private const int Acrylic = 3;        // DWMSBT_TRANSIENTWINDOW
    private const int Mica = 2;           // DWMSBT_MAINWINDOW, the fallback below 22H2
    private const int RoundCorners = 2;   // DWMWCP_ROUND

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    internal static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int dark = 1;
            DwmSetWindowAttribute(handle, DarkMode, ref dark, sizeof(int));

            int corner = RoundCorners;
            DwmSetWindowAttribute(handle, CornerStyle, ref corner, sizeof(int));

            // the HwndSource has to stop painting its own white before the backdrop can show through it
            var source = HwndSource.FromHwnd(handle);
            if (source is not null) source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;

            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);

            int backdrop = Acrylic;
            if (DwmSetWindowAttribute(handle, Backdrop, ref backdrop, sizeof(int)) != 0)
            {
                backdrop = Mica;
                DwmSetWindowAttribute(handle, Backdrop, ref backdrop, sizeof(int));
            }
        }
        catch { }
    }
}
