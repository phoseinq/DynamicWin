using System;
using System.Runtime.InteropServices;
using Halo.Interop;

namespace Halo.Shell;

internal sealed class NotchWindow
{
    private Win32.WndProc _wndProc = null!;
    private int _workLeft, _workTop, _workWidth;

    public IntPtr Hwnd { get; private set; }
    public int WorkLeft => _workLeft;
    public int WorkTop => _workTop;
    public int WorkWidth => _workWidth;

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
        _workLeft = work.left;
        _workTop = work.top;
        _workWidth = work.right - work.left;

        int exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOREDIRECTIONBITMAP;
        Hwnd = Win32.CreateWindowEx(exStyle, "HaloNotchWindow", "Halo", Win32.WS_POPUP,
            _workLeft, _workTop, 10, 10, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
    }

    public void EnableAcrylicGlass()
    {
        Win32.EnableAcrylic(Hwnd, 0x00000000);
    }

    public void SetBounds(int w, int h, int radius)
    {
        int x = _workLeft + (_workWidth - w) / 2;
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, _workTop, w, h, Win32.SWP_NOACTIVATE);

        var rgn = Win32.CreateRoundRectRgn(0, 0, w + 1, h + 1, radius * 2, radius * 2);
        var squareTop = Win32.CreateRectRgn(0, 0, w + 1, radius + 1);
        Win32.CombineRgn(rgn, rgn, squareTop, Win32.RGN_OR);
        Win32.DeleteObject(squareTop);
        Win32.SetWindowRgn(Hwnd, rgn, true);
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
