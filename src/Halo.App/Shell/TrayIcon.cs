using System;
using System.Runtime.InteropServices;
using Halo.Interop;

namespace Halo.Shell;

internal sealed class TrayIcon : IDisposable
{
    private const uint WM_TRAY = 0x0400 + 1;
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const int IdSettings = 1, IdRestart = 2, IdQuit = 3;

    private readonly Win32.WndProc _proc;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private bool _added;

    internal TrayIcon()
    {
        _proc = Handle;
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = Win32.GetModuleHandle(null),
            lpszClassName = "HaloTrayWindow",
        };
        Win32.RegisterClassEx(ref wc);
        _hwnd = Win32.CreateWindowEx(0, "HaloTrayWindow", "Halo", 0, 0, 0, 0, 0,
            Win32.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            int size = Math.Max(16, Win32.GetSystemMetrics(49 ));
            var handles = new IntPtr[1];
            var ids = new int[1];
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0 && Win32.PrivateExtractIcons(exe, 0, size, size, handles, ids, 1, 0) >= 1)
                return handles[0];
        }
        catch { }
        return IntPtr.Zero;
    }

    private void Add()
    {
        try
        {
            if (_icon == IntPtr.Zero) _icon = LoadAppIcon();
            var data = Data();
            data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP;
            data.uCallbackMessage = (int)WM_TRAY;
            data.hIcon = _icon;
            data.szTip = "Halo";
            _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);

            var version = Data();
            version.uVersion = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref version);
        }
        catch { }
    }

    private Win32.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr Handle(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == _taskbarCreated && _taskbarCreated != 0) { Add(); return IntPtr.Zero; }
            if (msg == WM_TRAY)
            {

                uint evt = (uint)((long)lParam & 0xFFFF);
                if (evt == WM_LBUTTONUP) Program.OpenSettings();
                else if (evt is WM_RBUTTONUP or WM_CONTEXTMENU)
                    Menu((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF));
                return IntPtr.Zero;
            }
        }
        catch { }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Menu(int x, int y)
    {
        IntPtr menu = IntPtr.Zero;
        try
        {
            menu = Win32.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            Win32.AppendMenu(menu, Win32.MF_STRING, IdSettings, "Open settings");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, IdRestart, "Restart Halo");
            Win32.AppendMenu(menu, Win32.MF_STRING, IdQuit, "Quit Halo");

            Win32.SetForegroundWindow(_hwnd);
            int picked = Win32.TrackPopupMenuEx(menu,
                Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD, x, y, _hwnd, IntPtr.Zero);
            Win32.PostMessage(_hwnd, 0x0000 , IntPtr.Zero, IntPtr.Zero);

            switch (picked)
            {
                case IdSettings: Program.OpenSettings(); break;
                case IdRestart: Program.Restart(); break;
                case IdQuit: Program.Quit(); break;
            }
        }
        catch { }
        finally { if (menu != IntPtr.Zero) Win32.DestroyMenu(menu); }
    }

    public void Dispose()
    {
        try
        {
            if (_added)
            {
                var data = Data();
                Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
                _added = false;
            }
            if (_icon != IntPtr.Zero) { Win32.DestroyIcon(_icon); _icon = IntPtr.Zero; }
            if (_hwnd != IntPtr.Zero) Win32.DestroyWindow(_hwnd);
        }
        catch { }
    }
}
