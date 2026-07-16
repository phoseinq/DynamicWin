using System;
using System.Runtime.InteropServices;

namespace Halo.Codex;

internal static class CodexDesktopCancel
{
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const int VkEscape = 0x1B;

    internal static IntPtr RootWindow(IntPtr handle) => GetAncestor(handle, 2);

    internal static bool Post(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) =>
        PostMessage(handle, message, wParam, lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
