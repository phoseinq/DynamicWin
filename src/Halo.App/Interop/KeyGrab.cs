using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Halo.Interop;

// Keystrokes for the pill's answer field, without the pill ever taking focus.
//
// Taking focus was tried first and does not work here: Windows hands the foreground only to a process
// that already owns it, and Halo is a WS_EX_NOACTIVATE background window that never does - so
// SetForegroundWindow returned true, changed nothing, and every keystroke kept landing in the terminal
// behind. AttachThreadInput moved it no further. A low-level hook sidesteps the whole question: the
// user's app keeps the focus it had, and only the keys this field actually consumes are taken from it.
//
// Consumption is deliberately narrow. Alt and Win chords are never touched, so Alt+Tab still leaves; a
// Ctrl chord other than Ctrl+V goes through untouched. What is swallowed is what the field uses: text,
// Backspace, Enter, Escape, Ctrl+V - and the matching key-up of anything swallowed, or the app behind is
// left holding a key it never saw pressed.
internal sealed class KeyGrab
{
    private readonly HashSet<uint> _eaten = [];
    private Win32.HookProc? _proc;   // a field, because the OS calls this after the stack that made it is gone
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
        catch { }   // a throw here would propagate into a Windows callback; the key just passes through
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

    // The hook thread's own key state is not the typing thread's, so the modifiers that matter are rebuilt
    // by hand and the layout is taken from whatever window actually has the focus - otherwise a non-US
    // layout would be translated as US.
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
            // flag 4 is "do not disturb keyboard state": without it this call eats the dead-key the user
            // was composing in the app behind, which they would then be missing in their own editor
            int n = Win32.ToUnicodeEx(vk, scan, state, buf, cap, 4, layout);
            return n > 0 ? System.Text.Encoding.Unicode.GetString(buf, 0, Math.Min(n, cap) * 2) : "";
        }
        catch { return ""; }
    }
}
