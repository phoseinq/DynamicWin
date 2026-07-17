using System;
using Halo.Interop;
using Halo.Shell;
using Halo.Widgets;

namespace Halo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // dev hook: `Halo.App --render-widget <out.png> [media|claude|codex]`
        if (args.Length >= 2 && args[0] == "--render-widget") { RenderWidget(args[1], args.Length > 2 ? args[2] : "media"); return; }

        using var mutex = new System.Threading.Mutex(true, "Halo.Notch.SingleInstance", out bool created);
        if (!created) return;

        try
        {
            var notch = new LayeredNotch();
            notch.Show();
            Halo.ClaudeCode.Limits.Poke();  // prefetch usage so the panel opens with data ready
            Halo.ClaudeCode.NetMon.Poke();  // start the connectivity heartbeat (ring goes red on outage)
            Halo.Codex.CodexNetMon.Poke();  // prefetch the independent OpenAI connectivity heartbeat
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

    // dev-only: draw a widget's expanded content to a PNG (MTA so WinRT async completes without an STA pump)
    private static void RenderWidget(string outPath, string which)
    {
        var t = new System.Threading.Thread(() =>
        {
            IWidget w = which switch
            {
                "claude" => new ClaudeCodeWidget(new Halo.ClaudeCode.StatusStore(), 0, () => { }),
                "codex" => new CodexWidget(new Halo.Codex.CodexStatusStore(), Halo.Codex.CodexSurface.Cli, () => { }),
                _ => new MediaWidget(),
            };
            for (int i = 0; i < 100 && !w.IsActive; i++)
                System.Threading.Thread.Sleep(100);
            using var bmp = new System.Drawing.Bitmap(560, 220);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(System.Drawing.Color.FromArgb(20, 20, 22));
                w.DrawContent(g, 560, 220, 1f);
            }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        });
        t.SetApartmentState(System.Threading.ApartmentState.MTA);
        t.Start();
        t.Join();
    }
}
