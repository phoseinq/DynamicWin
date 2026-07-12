using System;
using Halo.Interop;
using Halo.Rendering;
using Halo.Shell;

namespace Halo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            var window = new NotchWindow();
            window.Show();

            var host = new CompositionHost(window.Hwnd);
            var pill = new GlassPill(host.Compositor);
            pill.SetSize(460, 140);
            pill.SetCornerRadius(30);
            pill.SetGlass(0.4f);
            host.Root.Children.InsertAtTop(pill.Visual);

            Win32.RunMessageLoop();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "halo-crash.log"),
                ex.ToString());
            throw;
        }
    }
}
