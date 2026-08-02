using System;
using System.Runtime.InteropServices;
using Halo.Interop;

namespace Halo.Shell;

// The only handle on Halo that is not the pill itself.
//
// Until now the single way to open settings was to click the shortcut a second time, and the only way to
// quit was Task Manager - a background app with no window and no tray entry has nowhere to be reached.
// Left click opens the panel, right click offers the same plus restart and quit.
//
// Hand-written Shell_NotifyIcon rather than WinForms' NotifyIcon: pulling in System.Windows.Forms for one
// icon would be a new dependency in a project whose entire interop surface is hand-written, and the
// WinForms message pump is not the one this app runs.
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_TRAY = 0x0400 + 1;    // WM_APP + 1, ours to define
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const int IdSettings = 1, IdRestart = 2, IdQuit = 3;

    private readonly Win32.WndProc _proc;       // held, or the GC collects the thunk the OS is calling
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

        // Explorer restarting destroys every tray icon and then broadcasts this; an app that does not
        // listen for it simply disappears from the tray until it is restarted.
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    // The app's own embedded icon at the shell's small-icon size, so it matches what the taskbar shows and
    // stays right on a 150% display, where a hardcoded 16 is a blurry half-size mark.
    private static IntPtr LoadAppIcon()
    {
        try
        {
            int size = Math.Max(16, Win32.GetSystemMetrics(49 /* SM_CXSMICON */));
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

            // version 4 is what makes the callback carry the cursor position in wParam, which is the only
            // way to place the menu correctly on a secondary monitor
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
                // with version 4 the event is in the LOW word of lParam and the cursor is in wParam
                uint evt = (uint)((long)lParam & 0xFFFF);
                if (evt == WM_LBUTTONUP) Program.OpenSettings();
                else if (evt is WM_RBUTTONUP or WM_CONTEXTMENU)
                    Menu((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF));
                return IntPtr.Zero;
            }
        }
        catch { }   // nothing the shell sends us may take the pill down
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

            // Both of these are load-bearing and both look like superstition: without the foreground call
            // the menu will not close when you click away from it, and without the trailing post it can
            // stay up after a selection. Documented shell behaviour since Win95, still true.
            Win32.SetForegroundWindow(_hwnd);
            int picked = Win32.TrackPopupMenuEx(menu,
                Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD, x, y, _hwnd, IntPtr.Zero);
            Win32.PostMessage(_hwnd, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

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
