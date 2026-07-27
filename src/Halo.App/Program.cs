using System;
using System.Linq;
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
        // dev hook: `Halo.App --render-widget <out.png> [media|claude|claude-demo|codex|download] [scale]`
        // claude-demo / claude-idle render a synthetic session instead of the live one — for docs and blog images, where
        // the author's real context and real spend have no business appearing.
        if (args.Length >= 2 && args[0] == "--render-widget")
        {
            RenderWidget(args[1], args.Length > 2 ? args[2] : "media",
                args.Length > 3 && int.TryParse(args[3], out int sc) ? sc : 1);
            return;
        }
        // dev hook: `Halo.App --render-pin <out.png>` — the pushpin states in isolation
        if (args.Length >= 2 && args[0] == "--render-pin") { RenderPin(args[1]); return; }
        // dev hook: `Halo.App --render-notif <out.png>` — the notification banner (real shape path) on a
        // colourful backdrop so edge fringes show, with mixed Persian+English text to eyeball the font/RTL
        if (args.Length >= 2 && args[0] == "--render-notif") { RenderNotif(args[1]); return; }
        // dev hook: `Halo.App --render-badges <out.png>` — the generated notif badges, to catch tofu glyphs
        if (args.Length >= 2 && args[0] == "--render-badges") { RenderBadges(args[1]); return; }
        // dev hook: `Halo.App --render-local <out.png>` — the banners Halo raises ITSELF, stacked. Every
        // other hook renders a mirrored toast, which always has a body; the body-less ones are our own.
        if (args.Length >= 2 && args[0] == "--render-local") { RenderLocal(args[1]); return; }
        // dev hook: `Halo.App --render-copy <out.png>` — the copy-code pill in both states, 4x, with a
        // centre guide. Its glyph and label are 11-12px, so being 1px out is visible and unmeasurable by eye.
        if (args.Length >= 2 && args[0] == "--render-copy") { RenderCopy(args[1]); return; }
        // dev hook: `Halo.App --probe-downloads <out.txt>` — every download each source can see, plus the
        // raw rows out of Chromium's in-progress store. Written to a file because this is a WinExe.
        if (args.Length >= 2 && args[0] == "--probe-downloads") { ProbeDownloads(args[1]); return; }
        // dev hook: `Halo.App --cancel-download` — scan, then run the pill's own Cancel on what it found.
        // The cancel path has broken more than once and cannot be reached from a screenshot, so it needs a
        // way to be exercised without a real click on an invisible window.
        if (args.Length >= 1 && args[0] == "--cancel-download") { CancelDownload(); return; }
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
            Halo.Update.AutoUpdate.Start(); // daily silent update check; schedule lives on disk, not here
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

    // dev-only: the pill's Cancel, without the pill. Cancel runs on a background task and the browser needs
    // a moment to react, so this waits and then reports whether the partial actually stopped growing —
    // which is the only answer that counts. See dl-cancel.txt for the step-by-step.
    private static void CancelDownload()
    {
        Halo.Widgets.Downloads.Scan();
        if (Halo.Widgets.Downloads.Count == 0) { Console.WriteLine("nothing downloading"); return; }
        string? file = Halo.Widgets.Downloads.FilePath;
        Console.WriteLine($"cancelling '{Halo.Widgets.Downloads.Name}' file='{file}'");
        long before = -1;
        try { if (file != null) before = new System.IO.FileInfo(file).Length; } catch { }

        Halo.Widgets.Downloads.CancelDownload();
        System.Threading.Thread.Sleep(14000);

        long after = -1;
        try { if (file != null && System.IO.File.Exists(file)) after = new System.IO.FileInfo(file).Length; } catch { }
        Console.WriteLine(after < 0 ? "partial is gone -> stopped"
            : after == before ? $"partial held at {before:n0} -> stopped"
            : $"partial grew {before:n0} -> {after:n0} -> STILL RUNNING");
    }

    // dev-only: what the download sources actually see right now. Edge gives nothing in its History until a
    // download ENDS and never renames its partial, so "is it even detected, and under what name" was a
    // question we kept answering by squinting at dl-debug.txt after the fact.
    private static void ProbeDownloads(string outPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{DateTime.Now:HH:mm:ss}  probe-downloads");

        sb.AppendLine("\nChromiumProgress.Live():");
        var live = Halo.Widgets.ChromiumProgress.Live();
        if (live.Length == 0) sb.AppendLine("  (nothing in progress)");
        foreach (var e in live)
            sb.AppendLine($"  name='{e.Name}' received={e.Received:n0} total={e.Total:n0}"
                        + $" pct={(e.Total > 0 ? 100.0 * e.Received / e.Total : 0):0.0}");

        sb.AppendLine("\nChromiumProgress.DumpFields():");
        sb.AppendLine(Halo.Widgets.ChromiumProgress.DumpFields());

        sb.AppendLine("\nPartialFiles.All():");
        foreach (var p in Halo.Widgets.PartialFiles.All())
            sb.AppendLine($"  {p}");

        Halo.Widgets.Downloads.Scan();
        sb.AppendLine($"\nDownloads.Scan() -> {Halo.Widgets.Downloads.Count} item(s), selected="
                    + Halo.Widgets.Downloads.SelectedIndex);
        foreach (var d in Halo.Widgets.Downloads.Items)
            sb.AppendLine($"  key='{d.Key}' name='{d.Name}' pct={d.Percent} got={d.Downloaded:n0}"
                        + $" total={d.Total:n0} noPct={d.NoPct} noBytes={d.NoBytes} pid={d.OwnerPid}"
                        + $" exe='{d.ExePath}' file='{d.FilePath}'");

        System.IO.File.WriteAllText(outPath, sb.ToString());
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
        // Five cells now, because the pushpin carries two settings and the only way a user can tell them
        // apart is by shape. Whether "lit head, outline needle" actually reads as different from "all lit"
        // at 24px is exactly the kind of thing that cannot be judged from the code.
        using var bmp = new System.Drawing.Bitmap(620, 150);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.FromArgb(28, 28, 32));
            using var lf = new System.Drawing.Font("Segoe UI", 11f);
            using var lb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(170, 235, 235, 235));
            void Cell(float ox, bool pinned, float hover, string label, bool rec = false, float hold = 0f)
            {
                var st = g.Save();
                g.TranslateTransform(ox, 14);
                g.ScaleTransform(3.2f, 3.2f);
                Halo.Shell.NotchController.DrawPushpin(
                    g, new System.Drawing.RectangleF(0, 0, 24, 24), pinned, hover, 1f, rec, hold);
                g.Restore(st);
                g.DrawString(label, lf, lb, ox - 4, 108);
            }
            Cell(20, false, 0f, "off");
            Cell(140, true, 0f, "pinned");
            Cell(260, false, 0f, "in capture", rec: true);
            Cell(380, true, 0f, "pinned+cap", rec: true);
            Cell(500, false, 1f, "mid-hold", hold: 1f);
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
            var n = new Halo.Notifications.NotifItem
            {
                Icon = icon,       // with a Preview this lands as the thumb's corner badge

                App = Halo.Notifications.NotifItem.ScreenshotApp,
                Title = Halo.Notifications.NotifItem.ScreenshotTitle,
                // deliberately longer than two lines, so the hook shows where the summary wraps and
                // where its ellipsis actually lands
                Body = "Saved to the clipboard. Click the banner to edit it, or press Ctrl+V in any "
                     + "app to paste it straight in.",
                Code = "482913",   // so the hook also shows the copy pill, which has its own alignment
                Preview = shot,
            };
            Halo.Widgets.NotifBanner.Draw(g, W, H, 1f, n, 0f, false);
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    // dev-only: the copy-code pill, magnified, in both of its states, with a line through the true centre of
    // the pill. Drawn by rendering the WHOLE banner through the real path and cropping to CopyRect, so what
    // is magnified is exactly what ships -- and at 11-12px a one-pixel error is what the eye actually reads
    // as "the icon sits high", which no amount of squinting at a 1x screenshot can settle.
    private static void RenderCopy(string outPath)
    {
        int W = Halo.Widgets.NotifBanner.W, H = Halo.Widgets.NotifBanner.SummaryH;
        const int Zoom = 4, Pad = 6;

        var states = new[] { false, true };   // fresh code, then after the click
        var shots = new System.Drawing.Bitmap[states.Length];
        var rects = new System.Drawing.RectangleF[states.Length];

        for (int s = 0; s < states.Length; s++)
        {
            var n = new Halo.Notifications.NotifItem
            {
                App = "Aurora", Title = "Verify your sign-in",
                Body = "Your verification code is 482913. It expires in 10 minutes.",
                Code = "482913", Copied = states[s],
            };
            rects[s] = Halo.Widgets.NotifBanner.CopyRect(n, W);
            var full = new System.Drawing.Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                g.Clear(System.Drawing.Color.FromArgb(255, 18, 18, 22));
                Halo.Widgets.NotifBanner.Draw(g, W, H, 1f, n, 0f, false);
            }
            shots[s] = full;
        }

        int cw = (int)Math.Ceiling(rects[0].Width) + Pad * 2;
        int ch = (int)Math.Ceiling(rects[0].Height) + Pad * 2;
        using var bmp = new System.Drawing.Bitmap(cw * Zoom, ch * Zoom * states.Length);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.Clear(System.Drawing.Color.FromArgb(255, 12, 12, 14));
            for (int s = 0; s < states.Length; s++)
            {
                var r = rects[s];
                var src = new System.Drawing.Rectangle((int)r.X - Pad, (int)r.Y - Pad, cw, ch);
                var dst = new System.Drawing.Rectangle(0, s * ch * Zoom, cw * Zoom, ch * Zoom);
                g.DrawImage(shots[s], dst, src, System.Drawing.GraphicsUnit.Pixel);
                // the pill's own vertical centre, in the magnified frame
                float mid = dst.Y + (Pad + r.Height / 2f) * Zoom;
                using var guide = new System.Drawing.Pen(System.Drawing.Color.FromArgb(150, 255, 70, 70), 1f);
                g.DrawLine(guide, dst.X, mid, dst.Right, mid);
            }
        }
        foreach (var s in shots) s.Dispose();
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    // dev-only: Halo's own banners stacked, each through the real NotifBanner.Draw and the real shape path.
    // The guide-line down the middle is the point: the artwork and the text block must read as centred on
    // it, which is exactly what a body-less banner used to get wrong.
    private static void RenderLocal(string outPath)
    {
        int W = Halo.Widgets.NotifBanner.W, H = Halo.Widgets.NotifBanner.SummaryH, pad = 20;
        using var shot = new System.Drawing.Bitmap(1920, 1080);
        using (var sg = System.Drawing.Graphics.FromImage(shot))
        {
            using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, 1920, 1080),
                System.Drawing.Color.FromArgb(240, 245, 250), System.Drawing.Color.FromArgb(150, 190, 235), 45f);
            sg.FillRectangle(lg, 0, 0, 1920, 1080);   // a BRIGHT capture: the corner badge has to survive one
            using var wf = new System.Drawing.Font("Segoe UI", 130f);
            sg.DrawString("desktop", wf, System.Drawing.Brushes.DimGray, 430, 440);
        }

        var notices = Halo.Shell.NotchController.SampleLocalNotices(shot);
        using var bmp = new System.Drawing.Bitmap(W + pad * 2, notices.Length * (H + pad) + pad);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Color.FromArgb(60, 140, 200), System.Drawing.Color.FromArgb(200, 100, 60), 35f))
                g.FillRectangle(lg, 0, 0, bmp.Width, bmp.Height);

            var notch = new Halo.Shell.LayeredNotch();
            for (int i = 0; i < notices.Length; i++)
            {
                var state = g.Save();
                g.TranslateTransform(pad, pad + i * (H + pad));
                notch.DrawShape(g, W, H, 26, 245, glass: false);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Halo.Widgets.NotifBanner.Draw(g, W, H, 1f, notices[i], 0f, false);
                using (var guide = new System.Drawing.Pen(System.Drawing.Color.FromArgb(90, 255, 80, 80), 1f))
                    g.DrawLine(guide, 0, H / 2f, W, H / 2f);
                g.Restore(state);
            }
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    // dev-only: the generated notif badges (battery / net / limit / clock / cpu / shot / clip) on a dark
    // strip, so a bad Fluent code point shows up as tofu instead of shipping invisible.
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
    // scale renders the panel through the SAME draw code at N x the logical size — a real high-resolution
    // render, not an upscale of a 560x220 one. Everything here is vector or re-decoded art, so the only
    // thing that changes is how many pixels it lands on.
    private static void RenderWidget(string outPath, string which, int scale = 1)
    {
        var t = new System.Threading.Thread(() =>
        {
            if (which == "download") // inject a sample download so the read-only widget has something to draw
            {
                Halo.Widgets.Downloads.Name = "Source.Code.2011.1080p.BluRay.10bit.x265.Farsi.Dubbed.mkv";
                Halo.Widgets.Downloads.Percent = 36;
                // a browser, because that is where downloads come from and the icon is the first thing read;
                // explorer.exe rendered a generic folder that said nothing about what the widget is for
                Halo.Widgets.Downloads.ExePath = new[]
                {
                    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                }.FirstOrDefault(System.IO.File.Exists) ?? @"C:\Windows\explorer.exe";
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
            // claude-demo: a SYNTHETIC session in a temp directory, for docs and blog images. The plain
            // "claude" variant reads the live store, which on this machine means the author's real context
            // and real dollars — fine for eyeballing a layout, not for a public page. Credits are left
            // unset so the money line does not render at all, rather than rendering an invented figure.
            string demoRoot = "";
            if (which is "claude-demo" or "claude-idle")
            {
                demoRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "halo-claude-demo");
                System.IO.Directory.CreateDirectory(demoRoot);
                var now = DateTimeOffset.UtcNow;
                // the lamp that replaces the stop button when nothing can be interrupted is what the
                // panel shows most of the time, so it needs to be renderable too
                var demoState = which == "claude-idle" ? "idle" : "working";
                System.IO.File.WriteAllText(System.IO.Path.Combine(demoRoot, "status.json"), $$"""
                {
                  "pid": {{System.Environment.ProcessId}},
                  "sessionId": "demo",
                  "cwd": "C:\\Projects\\halo",
                  "state": "{{demoState}}",
                  "consolePid": {{System.Environment.ProcessId}},
                  "updatedAt": "{{now:o}}",
                  "startedAt": "{{now.AddMinutes(-12):o}}",
                  "currentTool": "Edit",
                  "session": { "contextUsed": 341000, "contextMax": 1000000, "promptTokens": 48200 }
                }
                """);
                Halo.ClaudeCode.Limits.FiveHour = 0.42f;
                Halo.ClaudeCode.Limits.FiveHourReset = now.AddHours(2).AddMinutes(48);
            }

            IWidget w = which switch
            {
                "claude-demo" or "claude-idle" => new ClaudeCodeWidget(
                    new Halo.ClaudeCode.StatusStore(System.IO.Path.Combine(demoRoot, "status.json"),
                        _ => DateTimeOffset.UtcNow.AddMinutes(-12), watchFiles: false), 0, () => { }),
                "claude" => new ClaudeCodeWidget(new Halo.ClaudeCode.StatusStore(), 0, () => { }),
                "codex" => new CodexWidget(new Halo.Codex.CodexStatusStore(), Halo.Codex.CodexSurface.Cli, () => { }),
                "download" => new DownloadWidget(),
                _ => new MediaWidget(new MediaSessions(), 0),
            };
            for (int i = 0; i < 100 && !w.IsActive; i++)
                System.Threading.Thread.Sleep(100);
            scale = Math.Clamp(scale, 1, 6);
            if (which is "claude-demo" or "claude-idle")
            {
                // Drawing the panel is what calls Limits.OnPanelOpen(), which goes and refetches — and the
                // synthetic figures set above are gone by the time the real draw happens, leaving a blank
                // gap where the usage row belongs. Burn one throwaway draw to get that out of the way, then
                // put the demo numbers back for the frame that gets saved.
                using (var warm = new System.Drawing.Bitmap(560, 220))
                using (var wg = System.Drawing.Graphics.FromImage(warm))
                    w.DrawContent(wg, 560, 220, 1f);
            }
            // The warm draw above opened NetMon's fast-sampling window; without a pause the ring buffer is
            // still empty and the connection graph renders its "sampling…" state every time — so the one
            // part of the panel that is a chart could never actually be eyeballed as a chart.
            if (which is "claude" or "claude-demo" or "claude-idle" or "codex")
                System.Threading.Thread.Sleep(8000); // Poke opens an 8s fast-sample window; use all of it
            // Demo figures go in LAST, after that wait: the refetch the warm draw kicked off is asynchronous,
            // and setting them before the sleep let the real answer land on top — the saved frame then showed
            // the author's actual usage and dollar spend, which is the exact thing this mode exists to avoid.
            if (which is "claude-demo" or "claude-idle")
            {
                Halo.ClaudeCode.Limits.FiveHour = 0.42f;
                Halo.ClaudeCode.Limits.FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).AddMinutes(48);
                Halo.ClaudeCode.Limits.Week = 0.61f;
                Halo.ClaudeCode.Limits.WeekReset = DateTimeOffset.UtcNow.AddDays(3).AddHours(5);
                Halo.ClaudeCode.Limits.CreditsUsed = 0;   // no invented dollars on a public image
                Halo.ClaudeCode.Limits.LastSuccess = DateTime.UtcNow.AddMinutes(-2);
            }
            using var bmp = new System.Drawing.Bitmap(560 * scale, 220 * scale);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.FromArgb(20, 20, 22));
                g.ScaleTransform(scale, scale);
                w.DrawContent(g, 560, 220, 1f);
            }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        });
        t.SetApartmentState(System.Threading.ApartmentState.MTA);
        t.Start();
        t.Join();
    }
}
