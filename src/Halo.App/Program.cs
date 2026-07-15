using System;
using Halo.Interop;
using Halo.Shell;

namespace Halo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            var notch = new LayeredNotch();
            notch.Show();
            _ = new NotchController(notch);
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
