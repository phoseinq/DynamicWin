using System;
using System.Diagnostics;
using System.IO;

namespace Halo.ClaudeCode;

internal static class CcCancel
{
    public static void Request(int pid)
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Halo.Hooks.exe");
            if (!File.Exists(exe)) return;
            Process.Start(new ProcessStartInfo(exe, $"cancel {pid}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
        }
    }
}
