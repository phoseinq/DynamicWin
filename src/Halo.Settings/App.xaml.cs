using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Halo.Settings;

public partial class App : Application
{
    private static Mutex? _instance;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    private const int Restore = 9;

    // Clicking the Halo icon twice must not open a second panel. The second launch hands focus to the
    // window already open and leaves - which is also what makes "the icon opens settings" safe to wire
    // to something as easy to double-press as a taskbar button.
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, "Halo.Settings.SingleInstance", out bool created);
        if (!created)
        {
            Surface();
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private static void Surface()
    {
        try
        {
            int self = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("Halo.Settings"))
            {
                using (process)
                {
                    if (process.Id == self || process.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(process.MainWindowHandle, Restore);
                    SetForegroundWindow(process.MainWindowHandle);
                    return;
                }
            }
        }
        catch { }
    }
}
