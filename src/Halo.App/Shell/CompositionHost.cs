using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Halo.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Halo.Shell;

internal sealed class CompositionHost
{
    private readonly DesktopWindowTarget _target;

    public Compositor Compositor { get; }
    public ContainerVisual Root { get; }

    public CompositionHost(IntPtr hwnd)
    {
        CompositionInterop.EnsureDispatcherQueue();
        Compositor = new Compositor();

        var inspectable = MarshalInspectable<Compositor>.FromManaged(Compositor);
        try
        {
            var iid = typeof(ICompositorDesktopInterop).GUID;
            int hr = Marshal.QueryInterface(inspectable, in iid, out var interopPtr);
            if (hr < 0)
                throw new InvalidOperationException($"QI ICompositorDesktopInterop failed 0x{hr:X8}");

            var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(interopPtr);
            Marshal.Release(interopPtr);

            interop.CreateDesktopWindowTarget(hwnd, true, out var raw);
            _target = DesktopWindowTarget.FromAbi(raw);
            Marshal.Release(raw);
        }
        finally
        {
            Marshal.Release(inspectable);
        }

        Root = Compositor.CreateContainerVisual();
        Root.RelativeSizeAdjustment = Vector2.One;
        _target.Root = Root;
    }
}
