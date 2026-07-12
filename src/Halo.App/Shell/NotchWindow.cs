using System;
using System.Runtime.InteropServices;
using Halo.Interop;

namespace Halo.Shell;

internal sealed class NotchWindow
{
    private Win32.WndProc _wndProc = null!;

    public IntPtr Hwnd { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public int ExpandedWidth = 560;
    public int ExpandedHeight = 220;

    public void Show()
    {
        var hInstance = Win32.GetModuleHandle(null);
        _wndProc = WndProc;

        var wc = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = "HaloNotchWindow",
        };
        if (Win32.RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        var work = default(Win32.RECT);
        Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref work, 0);
        int workLeft = work.left, workTop = work.top, workWidth = work.right - work.left;

        var (x, y, w, h) = NotchGeometry.ExpandedRect(workLeft, workTop, workWidth, ExpandedWidth, ExpandedHeight);
        Width = w;
        Height = h;

        int exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOREDIRECTIONBITMAP;
        Hwnd = Win32.CreateWindowEx(exStyle, "HaloNotchWindow", "Halo", Win32.WS_POPUP,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
    }

    public void ShapeToPill(int left, int top, int w, int h, int radius)
    {
        var rgn = Win32.CreateRoundRectRgn(left, top, left + w, top + h, radius * 2, radius * 2);
        Win32.SetWindowRgn(Hwnd, rgn, true);
        Win32.EnableAcrylic(Hwnd, 0x400A0A0A);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_DESTROY)
        {
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
