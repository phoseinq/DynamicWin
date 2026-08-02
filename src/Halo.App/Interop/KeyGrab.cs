using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Halo.Interop;

internal sealed class KeyGrab
{
    private readonly HashSet<uint> _eaten = [];
    private Win32.HookProc? _proc;
    private IntPtr _hook;

    internal Action<char>? OnChar;
    internal Action<int>? OnKey;

    internal bool Active => _hook != IntPtr.Zero;

    internal void Start()
    {
        if (_hook != IntPtr.Zero) return;
        try
        {
            _proc = Hook;
            _hook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero) _proc = null;
        }
        catch { _proc = null; _hook = IntPtr.Zero; }
    }

    internal void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        try { Win32.UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        _proc = null;
        _eaten.Clear();
    }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == 0)
            {
                uint msg = (uint)wParam;
                var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
                {
                    if (Consume(info.vkCode, info.scanCode)) { _eaten.Add(info.vkCode); return new IntPtr(1); }
                    _eaten.Remove(info.vkCode);
                }
                else if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP && _eaten.Remove(info.vkCode))
                    return new IntPtr(1);
            }
        }
        catch { }
        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool Consume(uint vk, uint scan)
    {
        bool alt = Down(Win32.VK_MENU) || Down(Win32.VK_LWIN) || Down(Win32.VK_RWIN);
        if (alt) return false;
        bool ctrl = Down(Win32.VK_CONTROL);

        if (vk is Win32.VK_BACK or Win32.VK_RETURN or Win32.VK_ESCAPE
            || (ctrl && vk == Win32.VK_V))
        {
            OnKey?.Invoke((int)vk);
            return true;
        }
        if (ctrl) return false;

        string text = Translate(vk, scan);
        if (text.Length == 0) return false;
        bool any = false;
        foreach (char c in text)
            if (c >= ' ' && c != (char)0x7F) { OnChar?.Invoke(c); any = true; }
        return any;
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static string Translate(uint vk, uint scan)
    {
        try
        {
            var state = new byte[256];
            if (Down(Win32.VK_SHIFT)) state[Win32.VK_SHIFT] = 0x80;
            if ((Win32.GetKeyState(Win32.VK_CAPITAL) & 1) != 0) state[Win32.VK_CAPITAL] = 1;

            IntPtr layout = IntPtr.Zero;
            var fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero) layout = Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(fg, out _));

            const int cap = 8;
            var buf = new byte[cap * 2];

            int n = Win32.ToUnicodeEx(vk, scan, state, buf, cap, 4, layout);
            return n > 0 ? System.Text.Encoding.Unicode.GetString(buf, 0, Math.Min(n, cap) * 2) : "";
        }
        catch { return ""; }
    }
}
