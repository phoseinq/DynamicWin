using System;
using Halo.Interop;
using Halo.Shell;

namespace Halo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var window = new NotchWindow();
        window.Show();
        Win32.RunMessageLoop();
    }
}
