using System;
using System.Runtime.InteropServices;

namespace Halo.Interop;

[ComImport]
[Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICompositorDesktopInterop
{
    void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
    void EnsureOnThread(int threadId);
}

internal static class CompositionInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController);

    private static object? _controller;

    public static void EnsureDispatcherQueue()
    {
        if (_controller != null) return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,
            apartmentType = 2,
        };
        int hr = CreateDispatcherQueueController(options, out _controller);
        if (hr < 0)
            throw new InvalidOperationException($"CreateDispatcherQueueController failed 0x{hr:X8}");
    }
}
