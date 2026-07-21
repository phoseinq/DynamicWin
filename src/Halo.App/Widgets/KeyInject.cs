using System;
using System.Threading;
using System.Threading.Tasks;
using Halo.Interop;

namespace Halo.Widgets;

// Robust per-app hotkey injection (subtitle/PiP/VLC transport). The old path fired keybd_event after
// ONE SetForegroundWindow with no verification — when Windows refused the focus change (foreground
// lock), the key landed in the wrong app and the button "didn't work". Now: attach to BOTH threads,
// retry the focus, and VERIFY the target actually owns the foreground before any key goes out.
internal static class KeyInject
{
    private const byte VK_MENU = 0x12;

    public static void Send(IntPtr hwnd, byte vk, bool alt = false)
        => SendSeq(hwnd, new[] { vk }, alt);

    // ONE focus dance, then the keys in guaranteed order (a per-key Task.Run raced: parallel tasks
    // scrambled multi-key sequences like the VLC speed cycle "=]]" so only "=" reliably landed).
    public static void SendSeq(IntPtr hwnd, byte[] vks, bool alt = false)
    {
        if (hwnd == IntPtr.Zero || vks.Length == 0) return;
        Task.Run(() =>
        {
            try
            {
                if (Win32.GetForegroundWindow() != hwnd)
                {
                    IntPtr fg = Win32.GetForegroundWindow();
                    uint me = Win32.GetCurrentThreadId();
                    uint fgT = Win32.GetWindowThreadProcessId(fg, out _);
                    uint tgT = Win32.GetWindowThreadProcessId(hwnd, out _);
                    Win32.AttachThreadInput(me, fgT, true);
                    Win32.AttachThreadInput(me, tgT, true);
                    for (int i = 0; i < 4 && Win32.GetForegroundWindow() != hwnd; i++)
                    {
                        Win32.SetForegroundWindow(hwnd);
                        Thread.Sleep(40);
                    }
                    Win32.AttachThreadInput(me, fgT, false);
                    Win32.AttachThreadInput(me, tgT, false);
                    Thread.Sleep(70); // let the focus land before the key hits the queue
                    if (Win32.GetForegroundWindow() != hwnd) return; // never type into the wrong app
                }
                if (alt) Win32.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                foreach (byte vk in vks)
                {
                    Win32.keybd_event(vk, 0, 0, UIntPtr.Zero);
                    Win32.keybd_event(vk, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Thread.Sleep(35); // VLC needs a beat between hotkeys to register each one
                }
                if (alt) Win32.keybd_event(VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        });
    }
}
