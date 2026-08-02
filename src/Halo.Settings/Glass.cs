using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Halo.Settings;

internal static class Glass
{
    private const int Backdrop = 38;
    private const int DarkMode = 20;
    private const int CornerStyle = 33;

    private const int Acrylic = 3;
    private const int Mica = 2;
    private const int RoundCorners = 2;

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
