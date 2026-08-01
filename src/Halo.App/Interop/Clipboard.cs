using System;
using System.Runtime.InteropServices;

namespace Halo.Interop;

// Minimal Win32 clipboard writer (WinForms isn't referenced). Copies UTF-16 text via a moveable global
// buffer, the standard OpenClipboard → EmptyClipboard → SetClipboardData(CF_UNICODETEXT) dance. Best-effort:
// if the clipboard is momentarily locked by another app it just returns false.
internal static class Clipboard
{
    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!Win32.OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            Win32.EmptyClipboard();
            int bytes = (text.Length + 1) * 2; // + null terminator, 2 bytes per UTF-16 char
            IntPtr hGlobal = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hGlobal == IntPtr.Zero) return false;
            IntPtr target = Win32.GlobalLock(hGlobal);
            if (target == IntPtr.Zero) return false;
            try { Marshal.Copy((text + '\0').ToCharArray(), 0, target, text.Length + 1); }
            finally { Win32.GlobalUnlock(hGlobal); }
            // ownership of hGlobal passes to the clipboard on success — don't free it
            return Win32.SetClipboardData(Win32.CF_UNICODETEXT, hGlobal) != IntPtr.Zero;
        }
        finally { Win32.CloseClipboard(); }
    }

    // The read half, for Ctrl+V into the ask banner's answer field. Same best-effort contract: a clipboard
    // held by another app for the moment we asked is normal and returns null rather than throwing.
    public static string? Text()
    {
        if (!Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT)) return null;
        if (!Win32.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr h = Win32.GetClipboardData(Win32.CF_UNICODETEXT);   // owned by the clipboard, don't free
            if (h == IntPtr.Zero) return null;
            IntPtr p = Win32.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Win32.GlobalUnlock(h); }
        }
        catch { return null; }
        finally { Win32.CloseClipboard(); }
    }
}
