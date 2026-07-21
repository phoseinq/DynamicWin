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
        // uninstall/disable hook: restore every app's native banner that Halo silenced, apply it live, and
        // forget the learned set. Run this from the uninstaller (or by hand) to leave the machine as found.
        if (args.Length >= 1 && args[0] == "--restore-notifications") { Halo.Notifications.BannerGate.Uninstall(); return; }
        // dev hook: `Halo.App --render-widget <out.png> [media|claude|codex]`
        if (args.Length >= 2 && args[0] == "--render-widget") { RenderWidget(args[1], args.Length > 2 ? args[2] : "media"); return; }
        // dev hook: `Halo.App --render-pin <out.png>` — the pushpin states in isolation
        if (args.Length >= 2 && args[0] == "--render-pin") { RenderPin(args[1]); return; }
        // dev hook: `Halo.App --render-notif <out.png>` — the notification banner (real shape path) on a
        // colourful backdrop so edge fringes show, with mixed Persian+English text to eyeball the font/RTL
        if (args.Length >= 2 && args[0] == "--render-notif") { RenderNotif(args[1]); return; }
        // dev hook: `Halo.App --render-badges <out.png>` — the 5 generated notif badges, to catch tofu glyphs
        if (args.Length >= 2 && args[0] == "--render-badges") { RenderBadges(args[1]); return; }
        // dev hook: `Halo.App --probe-icon <aumid>` — what the notif icon resolvers return for an app id
        if (args.Length >= 2 && args[0] == "--probe-icon") { ProbeIcon(args[1]); return; }
        // dev hook: `Halo.App --probe-tree <pid>` — the process's ancestor chain via Toolhelp
        if (args.Length >= 2 && args[0] == "--probe-tree") { ProbeTree(int.Parse(args[1])); return; }
        // dev hook: `Halo.App --probe-spectrum` — 6s of loopback band values (play audio meanwhile)
        if (args.Length >= 1 && args[0] == "--probe-spectrum")
        {
            for (int i = 0; i < 20; i++)
            {
                var b = Halo.Widgets.AudioSpectrum.Bands();
                Console.WriteLine($"avail={Halo.Widgets.AudioSpectrum.Available} " +
                    string.Join(" ", Array.ConvertAll(b, v => v.ToString("0.00"))));
                System.Threading.Thread.Sleep(300);
            }
            return;
        }

        using var mutex = new System.Threading.Mutex(true, "Halo.Notch.SingleInstance", out bool created);
        if (!created) return;

        try
        {
            Win32.OleInitialize(IntPtr.Zero); // STA OLE, so the notch can RegisterDragDrop for the File Tray
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

    private static void ProbeTree(int pid)
    {
        var map = new System.Collections.Generic.Dictionary<int, int>();
        var snap = Halo.Interop.Win32.CreateToolhelp32Snapshot(Halo.Interop.Win32.TH32CS_SNAPPROCESS, 0);
        var pe = new Halo.Interop.Win32.PROCESSENTRY32W
        { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Halo.Interop.Win32.PROCESSENTRY32W>() };
        if (Halo.Interop.Win32.Process32FirstW(snap, ref pe))
            do { map[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; }
            while (Halo.Interop.Win32.Process32NextW(snap, ref pe));
        Halo.Interop.Win32.CloseHandle(snap);
        Console.WriteLine($"snapshot has {map.Count} processes");
        int p = pid, guard = 0;
        while (p > 4 && guard++ < 20) { Console.WriteLine($"  {p}"); if (!map.TryGetValue(p, out p)) break; }
    }

    private static void ProbeIcon(string aumid)
    {
        var tmp = System.IO.Path.GetTempPath();
        var s = Halo.Notifications.ShellIcon.ForAumid(aumid);
        Console.WriteLine($"ShellIcon: {(s == null ? "NULL" : $"{s.Width}x{s.Height} -> probe_shell.png")}");
        s?.Save(System.IO.Path.Combine(tmp, "probe_shell.png"));
        var a = Halo.Widgets.AppIcon.ForAumid(aumid);
        Console.WriteLine($"AppIcon:   {(a == null ? "NULL" : $"{a.Width}x{a.Height} -> probe_app.png")}");
        a?.Save(System.IO.Path.Combine(tmp, "probe_app.png"));
    }

    // dev-only: draw the pushpin (pinned / unpinned / unpinned-hover) big on a dark bg to eyeball it
    private static void RenderPin(string outPath)
    {
        using var bmp = new System.Drawing.Bitmap(360, 130);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.FromArgb(28, 28, 32));
            void Cell(float ox, bool pinned, float hover)
            {
                var st = g.Save();
                g.TranslateTransform(ox, 18);
                g.ScaleTransform(3.2f, 3.2f);
                Halo.Shell.NotchController.DrawPushpin(g, new System.Drawing.RectangleF(0, 0, 24, 24), pinned, hover, 1f);
                g.Restore(st);
            }
            Cell(20, true, 0f);   // pinned
            Cell(140, false, 0f); // unpinned
            Cell(260, false, 1f); // unpinned + hover
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    // dev-only: the notification banner, drawn through the REAL shape path (LayeredNotch.DrawShape) on a
    // colourful backdrop, so any edge fringe and the Persian/English text rendering are visible.
    private static void RenderNotif(string outPath)
    {
        int W = Halo.Widgets.NotifBanner.W, H = Halo.Widgets.NotifBanner.SummaryH, pad = 24;
        using var bmp = new System.Drawing.Bitmap(W + pad * 2, H + pad * 2);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, W + pad * 2, H + pad * 2),
                System.Drawing.Color.FromArgb(70, 150, 210), System.Drawing.Color.FromArgb(210, 110, 70), 35f))
                g.FillRectangle(lg, 0, 0, W + pad * 2, H + pad * 2);
            g.TranslateTransform(pad, pad);
            // a round-ish test icon so accent/glow kick in
            using var icon = new System.Drawing.Bitmap(64, 64);
            using (var ig = System.Drawing.Graphics.FromImage(icon))
            {
                ig.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                ig.Clear(System.Drawing.Color.Transparent);
                using var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 40, 150, 235));
                ig.FillEllipse(b, 2, 2, 60, 60);
            }
            // a fake wide "screenshot" for the preview thumbnail
            using var shot = new System.Drawing.Bitmap(1920, 1080);
            using (var sgg = System.Drawing.Graphics.FromImage(shot))
            {
                using var lg2 = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(0, 0, 1920, 1080),
                    System.Drawing.Color.FromArgb(30, 30, 40), System.Drawing.Color.FromArgb(90, 60, 120), 45f);
                sgg.FillRectangle(lg2, 0, 0, 1920, 1080);
                using var wf = new System.Drawing.Font("Segoe UI", 120f);
                sgg.DrawString("desktop", wf, System.Drawing.Brushes.White, 500, 450);
            }
            new Halo.Shell.LayeredNotch().DrawShape(g, W, H, 26, 245, glass: false);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            _ = icon;
            var n = new Halo.Notifications.NotifItem
            {
                App = "Screenshot",
                Title = "Screenshot captured",
                Preview = shot,
            };
            Halo.Widgets.NotifBanner.Draw(g, W, H, 1f, n, 0f, false);
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    // dev-only: the 5 generated notif badges (battery / net / limit / clock / cpu) on a dark strip, so
    // a bad Fluent code point shows up as tofu instead of shipping invisible.
    private static void RenderBadges(string outPath)
    {
        var badges = Halo.Shell.NotchController.AllLocalBadges();
        using var bmp = new System.Drawing.Bitmap(badges.Length * 84 + 20, 104);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.FromArgb(28, 28, 32));
            for (int i = 0; i < badges.Length; i++)
                g.DrawImage(badges[i], 10 + i * 84, 20, 64, 64);
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    // dev-only: draw a widget's expanded content to a PNG (MTA so WinRT async completes without an STA pump)
    private static void RenderWidget(string outPath, string which)
    {
        var t = new System.Threading.Thread(() =>
        {
            if (which == "download") // inject a sample download so the read-only widget has something to draw
            {
                Halo.Widgets.Downloads.Name = "Source.Code.2011.1080p.BluRay.10bit.x265.Farsi.Dubbed.mkv";
                Halo.Widgets.Downloads.Percent = 36;
                Halo.Widgets.Downloads.ExePath = @"C:\Windows\explorer.exe";
                Halo.Widgets.Downloads.Hwnd = new IntPtr(1); // non-zero → the Stop button renders
            }
            if (which == "download-install") // Store install (indeterminate) sample
            {
                Halo.Widgets.Downloads.Name = "Microsoft Store";
                Halo.Widgets.Downloads.ExePath = "Microsoft.WindowsStore_8wekyb3d8bbwe!App";
                Halo.Widgets.Downloads.IsStore = true;
                Halo.Widgets.Downloads.Installing = true;
                which = "download";
            }
            IWidget w = which switch
            {
                "claude" => new ClaudeCodeWidget(new Halo.ClaudeCode.StatusStore(), 0, () => { }),
                "codex" => new CodexWidget(new Halo.Codex.CodexStatusStore(), Halo.Codex.CodexSurface.Cli, () => { }),
                "download" => new DownloadWidget(),
                _ => new MediaWidget(new MediaSessions(), 0),
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
