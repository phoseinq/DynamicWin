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

        if (args.Length >= 1 && args[0] == "--restore-notifications") { Halo.Notifications.BannerGate.Uninstall(); return; }

        if (args.Length >= 2 && args[0] == "--render-widget")
        {
            RenderWidget(args[1], args.Length > 2 ? args[2] : "media",
                args.Length > 3 && int.TryParse(args[3], out int sc) ? sc : 1, args);
            return;
        }

        if (args.Length >= 2 && args[0] == "--render-pill") { RenderPill(args[1]); return; }

        if (args.Length >= 1 && args[0] == "--probe-almanac")
        {
            Console.WriteLine($"zone     {TimeZoneInfo.Local.Id}");
            Console.WriteLine($"place    {Almanac.Place ?? "(none - offset-only zone)"}");
            Almanac.Poke();
            for (int i = 0; i < 60 && Almanac.Latest is null; i++) System.Threading.Thread.Sleep(500);
            Console.WriteLine($"weather  {(Almanac.Latest is { } wx ? $"{wx.TempC}C code {wx.Code} = {Almanac.Sky(wx.Code)}" : "(no reading)")}");
            Console.WriteLine($"country  {Almanac.PlaceCountry ?? "(not geocoded)"}   metric {Almanac.Metric}   calendar {Almanac.Calendar}");
            Console.WriteLine($"source   {(Almanac.FromDevice ? "windows location" : "time zone")}");
            Console.WriteLine($"label    {Almanac.Label}");
            Console.WriteLine($"title    {Almanac.Headline(DateTime.Now)}");
            Console.WriteLine($"body     {Almanac.Detail(DateTime.Now)}");
            return;
        }

        if (args.Length >= 2 && args[0] == "--render-pin") { RenderPin(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--render-notif") { RenderNotif(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--render-badges") { RenderBadges(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--render-ask") { RenderAsk(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--render-greeting") { RenderGreeting(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--render-local") { RenderLocal(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--render-copy") { RenderCopy(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--render-glyphs") { RenderGlyphs(args[1]); return; }

        if (args.Length >= 3 && args[0] == "--probe-console")
        {
            int.TryParse(args[1], out int cpid);
            int cbelow = args.Length > 3 && int.TryParse(args[3], out var cb) ? cb : 0;
            System.IO.File.WriteAllText(args[2], Halo.Interop.ConsoleRead.Describe(cpid, 16, cbelow));
            return;
        }

        if (args.Length >= 4 && args[0] == "--probe-type")
        {
            int.TryParse(args[1], out int tpid);

            bool sent = args[2] switch
            {
                "enter" => Halo.Interop.ConsoleRead.Press(tpid, Halo.Interop.ConsoleRead.VkEnter),
                "tab" => Halo.Interop.ConsoleRead.Press(tpid, Halo.Interop.ConsoleRead.VkTab),
                var s when s.StartsWith("down:") && int.TryParse(s[5..], out var n)
                    => Halo.Interop.ConsoleRead.Press(tpid, Halo.Interop.ConsoleRead.VkDown, n),
                var s when s.StartsWith("up:") && int.TryParse(s[3..], out var n)
                    => Halo.Interop.ConsoleRead.Press(tpid, Halo.Interop.ConsoleRead.VkUp, n),
                _ => Halo.Interop.ConsoleRead.Type(tpid, args[2]),
            };
            System.Threading.Thread.Sleep(400);
            System.IO.File.WriteAllText(args[3], $"sent={sent}\n" + Halo.Interop.ConsoleRead.Dump(tpid, 10));
            return;
        }
        if (args.Length >= 2 && args[0] == "--render-fluent")
        { RenderFluent(args[1], args.Length > 2 ? args[2] : "E700", args.Length > 3 ? args[3] : "256"); return; }

        if (args.Length >= 2 && args[0] == "--render-bar")
        { RenderBar(args[1], args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null); return; }

        if (args.Length >= 1 && args[0] == "--probe-media") { ProbeMedia(); return; }

        if (args.Length >= 2 && args[0] == "--probe-size")
        {
            var title = args[1];
            Console.WriteLine($"title       {title}");
            Console.WriteLine($"looks like a file  {Halo.Widgets.MediaFileInfo.LooksLikeFile(title)}");
            Halo.Widgets.MediaFileInfo.Size(title);
            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(100);
                if (Halo.Widgets.MediaFileInfo.Size(title) is { } b)
                { Console.WriteLine($"size        {b:N0} bytes = {Halo.Widgets.MediaFileInfo.Human(b)}"); return; }
            }
            Console.WriteLine("size        (not found)");
            return;
        }

        if (args.Length >= 2 && args[0] == "--probe-seek") { ProbeSeek(double.Parse(args[1],
            System.Globalization.CultureInfo.InvariantCulture), args.Length > 2 ? int.Parse(args[2]) : 1); return; }

        if (args.Length >= 2 && args[0] == "--probe-downloads") { ProbeDownloads(args[1]); return; }

        if (args.Length >= 1 && args[0] == "--cancel-download") { CancelDownload(); return; }

        if (args.Length >= 2 && args[0] == "--probe-icon") { ProbeIcon(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--probe-tree") { ProbeTree(int.Parse(args[1])); return; }

        if (args.Length >= 2 && args[0] == "--render-shape")
        {

            if (args.Length >= 4)
            {
                var parts = args[3].Split(',');
                float P(int i, float dflt) => i < parts.Length && float.TryParse(parts[i],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;
                Halo.Shell.LayeredNotch.FrostMix = P(0, Halo.Shell.LayeredNotch.FrostMix);
                Halo.Shell.LayeredNotch.Sheen = P(1, Halo.Shell.LayeredNotch.Sheen);
                Halo.Shell.LayeredNotch.Grain = P(2, Halo.Shell.LayeredNotch.Grain);
                Halo.Shell.LayeredNotch.RimLight = P(3, Halo.Shell.LayeredNotch.RimLight);
            }

            System.Drawing.Bitmap back;
            if (args.Length >= 3 && System.IO.File.Exists(args[2]))
            {
                using var src0 = new System.Drawing.Bitmap(args[2]);
                using var fit0 = new System.Drawing.Bitmap(560, 220, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var bgg0 = System.Drawing.Graphics.FromImage(fit0))
                    bgg0.DrawImage(src0, new System.Drawing.Rectangle(0, 0, 560, 220));

                back = Halo.Shell.LayeredNotch.BlurPyramid(fit0);
            }
            else
            {
                back = new System.Drawing.Bitmap(560, 220, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using var bgg = System.Drawing.Graphics.FromImage(back);
                bgg.Clear(System.Drawing.Color.Magenta);
            }
            using var _back = back;
            using var shot = new System.Drawing.Bitmap(560, 220, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var sg = System.Drawing.Graphics.FromImage(shot))
            {
                sg.Clear(System.Drawing.Color.Transparent);
                Halo.Shell.LayeredNotch.ShapeInto(sg, 560, 220, 30, NotchController.TintAppExpanded, back, 1f);
            }
            shot.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("wrote " + args[1]);
            return;
        }

        if (args.Length >= 1 && args[0] == "--probe-ip")
        {
            Halo.ClaudeCode.IpCountry.Poke();
            System.Threading.Thread.Sleep(5000);
            Console.WriteLine($"ip={Halo.ClaudeCode.IpCountry.Ip} cc={Halo.ClaudeCode.IpCountry.Cc} "
                + $"isp={Halo.ClaudeCode.IpCountry.Isp} asn={Halo.ClaudeCode.IpCountry.Asn}");
            Console.WriteLine($"apiIp={Halo.ClaudeCode.IpCountry.ApiIp} apiCc={Halo.ClaudeCode.IpCountry.ApiCc} "
                + $"split={Halo.ClaudeCode.IpCountry.Split}");
            string? scored = Halo.ClaudeCode.IpCountry.Split
                ? Halo.ClaudeCode.IpCountry.ApiIp : Halo.ClaudeCode.IpCountry.Ip;
            Halo.ClaudeCode.IpRep.Want(scored);
            System.Threading.Thread.Sleep(5000);
            Console.WriteLine($"scored={scored} forIp={Halo.ClaudeCode.IpRep.ForIp} "
                + $"verdict={Halo.ClaudeCode.IpRep.Verdict} abuse={Halo.ClaudeCode.IpRep.Abuse} "
                + $"sev={Halo.ClaudeCode.IpRep.Sev}");
            Halo.ClaudeCode.DnsLeak.Want(scored,
                Halo.ClaudeCode.IpCountry.Split ? Halo.ClaudeCode.IpCountry.ApiCc : Halo.ClaudeCode.IpCountry.Cc);
            for (int i = 0; i < 40 && !Halo.ClaudeCode.DnsLeak.Done; i++) System.Threading.Thread.Sleep(500);
            Console.WriteLine($"dns done={Halo.ClaudeCode.DnsLeak.Done} resolvers={Halo.ClaudeCode.DnsLeak.Resolvers} "
                + $"where={Halo.ClaudeCode.DnsLeak.Where} leaking={Halo.ClaudeCode.DnsLeak.Leaking}");
            Console.WriteLine("mark=" + Halo.ClaudeCode.IpRep.Score(
                Halo.ClaudeCode.IpRep.Tor, Halo.ClaudeCode.IpRep.Abuser, Halo.ClaudeCode.IpRep.Bogon,
                Halo.ClaudeCode.IpRep.Vpn, Halo.ClaudeCode.IpRep.Proxy, Halo.ClaudeCode.IpRep.Datacenter,
                Halo.ClaudeCode.IpRep.Abuse, Halo.ClaudeCode.IpCountry.Split, Halo.ClaudeCode.DnsLeak.Leaking));
            return;
        }

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

        if (args.Length >= 1 && args[0] == "--probe-timeline")
        {

            var probe = new System.Threading.Thread(() => ProbeTimeline());
            probe.SetApartmentState(System.Threading.ApartmentState.MTA);
            probe.Start();
            probe.Join();
            return;
        }

        if (args.Length >= 1 && args[0] == "--moods") { Moods(); return; }

        _instance = new System.Threading.Mutex(true, "Halo.Notch.SingleInstance", out bool created);
        if (!created) { OpenSettingsPanel(); return; }
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) OpenSettingsPanel();

        try
        {
            Win32.OleInitialize(IntPtr.Zero);
            var notch = new LayeredNotch();
            notch.Show();
            Halo.ClaudeCode.Limits.Poke();
            Halo.ClaudeCode.NetMon.Poke();
            Halo.Codex.CodexNetMon.Poke();
            _ = new NotchController(notch);
            _tray = new Halo.Shell.TrayIcon();
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

    private static void Moods()
    {
        int keys = 0, lines = 0;
        foreach (var key in Halo.Agents.Moods.Keys)
        {
            var set = Halo.Agents.Moods.Set(key);
            keys++; lines += set.Length;
            Console.WriteLine($"{key,-18} {set.Length,2}  {string.Join("  ·  ", set)}");
        }
        Console.WriteLine();
        Console.WriteLine($"{keys} keys, {lines} lines, none of them generated at runtime.");
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

    private static void RenderPill(string outPath)
    {
        var t = new System.Threading.Thread(() =>
        {

            (string label, string state, string? tool, int agoMin, long ctxUsed, float usage, string? target)[] rows =
            {
                ("idle — white", "idle", null, 0, 120_000, 0.30f, null),
                ("thinking — amber", "working", null, 0, 120_000, 0.30f, null),
                ("shell — green", "working", "Bash", 0, 120_000, 0.30f, null),
                ("reading — cyan", "working", "Read", 0, 120_000, 0.30f, null),
                ("fetching — teal", "working", "WebFetch", 0, 120_000, 0.30f, null),
                ("writing — violet", "working", "Edit", 0, 120_000, 0.30f, null),
                ("digging — lime", "working", "Grep", 0, 120_000, 0.30f, null),
                ("planning — gold", "working", "TodoWrite", 0, 120_000, 0.30f, null),
                ("subagent — magenta", "working", "Task", 0, 120_000, 0.30f, null),
                ("watching — slate", "working", "Monitor", 0, 120_000, 0.30f, null),
                ("your turn — pink", "waiting_input", null, 0, 120_000, 0.30f, null),
                ("named: a program", "working", "Bash", 0, 120_000, 0.30f, "dotnet"),
                ("named: a file", "working", "Edit", 0, 120_000, 0.30f, "Fx.cs"),
                ("named: a host", "working", "WebFetch", 0, 120_000, 0.30f, "learn.microsoft.com"),
                ("an mcp server", "working", "mcp__serena__find_symbol", 0, 120_000, 0.30f, null),
                ("a tool with no slot", "working", "SomeOtherTool", 0, 120_000, 0.30f, null),
                ("thinking, 10 min in", "working", null, 10, 120_000, 0.30f, null),

                ("compacting, just in", "compacting", null, 0, 920_000, 0.30f, null),
                ("compacting, 2 min in", "compacting", null, 2, 986_000, 0.30f, null),
                ("named, but context 92%", "working", "Edit", 1, 920_000, 0.30f, "Fx.cs"),
                ("shell, usage 96%", "working", "Bash", 1, 120_000, 0.96f, null),
                ("both, and dragging", "working", "Grep", 15, 950_000, 0.97f, null),
            };
            const int pw = 220, ph = 40, gap = 12, labelW = 168, scale = 2;
            int width = labelW + pw + 20, height = rows.Length * (ph + gap) + gap;
            using var bmp = new System.Drawing.Bitmap(width * scale, height * scale,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.Clear(System.Drawing.Color.FromArgb(255, 30, 30, 34));
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var lf = new System.Drawing.Font("Segoe UI", 11f);
            using var lb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(180, 235, 235, 235));

            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "halo-pill-demo");
            System.IO.Directory.CreateDirectory(root);
            float y = gap;
            int n = 0;
            foreach (var (label, state, tool, agoMin, ctxUsed, usage, target) in rows)
            {
                var now = DateTimeOffset.UtcNow;

                var path = System.IO.Path.Combine(root, $"status-{n++}.json");
                System.IO.File.WriteAllText(path, $$"""
                {
                  "pid": {{System.Environment.ProcessId}},
                  "sessionId": "pill",
                  "state": "{{state}}",
                  "consolePid": {{System.Environment.ProcessId}},
                  "updatedAt": "{{now:o}}",
                  "startedAt": "{{now.AddMinutes(-agoMin):o}}",
                  {{(tool is null ? "" : $"\"currentTool\": \"{tool}\",")}}
                  {{(target is null ? "" : $"\"toolTarget\": \"{target}\",")}}
                  "session": { "contextUsed": {{ctxUsed}}, "contextMax": 1000000, "promptTokens": 12000 }
                }
                """);
                IWidget w = new ClaudeCodeWidget(new Halo.ClaudeCode.StatusStore(path,
                    _ => DateTimeOffset.UtcNow.AddMinutes(-agoMin), watchFiles: false), 0, () => { });
                for (int i = 0; i < 60 && !w.IsActive; i++) System.Threading.Thread.Sleep(50);

                Halo.ClaudeCode.Limits.FiveHour = usage;
                Halo.ClaudeCode.Limits.FiveHourReset = DateTimeOffset.UtcNow.AddHours(2);
                Halo.ClaudeCode.Limits.CreditsUsed = 0;

                using (var warm = new System.Drawing.Bitmap(pw, ph,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                using (var wg = System.Drawing.Graphics.FromImage(warm))
                    for (int f = 0; f < 14; f++) w.DrawCollapsed(wg, pw, ph, 1f);

                using var pill = new System.Drawing.Bitmap(pw, ph,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var pg = System.Drawing.Graphics.FromImage(pill))
                {
                    pg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    using (var plate = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(236, 16, 16, 18)))
                    using (var pp = Fx.PillPath(pw, ph, ph / 2f))
                        pg.FillPath(plate, pp);
                    w.DrawCollapsed(pg, pw, ph, 1f);
                }
                g.DrawString(label, lf, lb, new System.Drawing.RectangleF(12, y + 10, labelW - 20, ph));
                g.DrawImage(pill, labelW, y);
                y += ph + gap;
            }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(outPath);
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private static void RenderPin(string outPath)
    {

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

    private static void RenderGreeting(string outPath)
    {

        float[] install = [0.05f, 0.20f, 0.36f, 0.51f, 0.59f, 0.70f, 0.85f, 0.97f];
        float[] login = [0.10f, 0.35f, 0.62f, 0.88f];

        const int cellW = 620, pad = 18;

        float tall = 40f;
        foreach (float t in install) tall += Halo.Shell.GreetingPlan.Install(t).PillH + pad;
        foreach (float t in login) tall += Halo.Shell.GreetingPlan.Login(t).PillH + pad;
        using var bmp = new System.Drawing.Bitmap(cellW + pad * 2, (int)tall + pad * 4);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Color.FromArgb(255, 22, 26, 34),
                System.Drawing.Color.FromArgb(255, 46, 30, 40), 60f))
                g.FillRectangle(lg, 0, 0, bmp.Width, bmp.Height);

            using var cap = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.GraphicsUnit.Pixel);
            using var capBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(150, 255, 255, 255));
            var notch = new Halo.Shell.LayeredNotch();
            float y = pad;

            g.DrawString("install - the pill opens, writes, clears, then says who it is", cap, capBrush, pad, y);
            y += 20f;
            foreach (float t in install)
            {
                var s = Halo.Shell.GreetingPlan.Install(t);
                int w = (int)s.PillW, h = (int)s.PillH;
                float x = pad + (cellW - w) / 2f;
                var st = g.Save();
                g.TranslateTransform(x, y);
                notch.DrawShape(g, w, h, (int)s.Radius, 190, glass: false);
                var box = Halo.Widgets.Greeting.InkBox(w, h);
                Halo.Widgets.Greeting.DrawHello(g, box, s.Written, s.HelloAlpha,
                    System.Drawing.Color.White, 9f);
                if (s.LineAlpha > 0f)
                    Halo.Widgets.Greeting.DrawLine(g, Halo.Widgets.Greeting.Lines[s.LineIndex], box,
                        s.LineWritten, s.LineAlpha, System.Drawing.Color.White, 9f);
                g.Restore(st);
                g.DrawString($"t={t:0.00}", cap, capBrush, pad, y + h - 14f);
                y += h + pad;
            }

            y += pad;
            g.DrawString("login - the same hand, inside a pill that never opens", cap, capBrush, pad, y);
            y += 20f;
            foreach (float t in login)
            {
                var s = Halo.Shell.GreetingPlan.Login(t);
                int w = (int)s.PillW, h = (int)s.PillH;
                float x = pad + (cellW - w) / 2f;
                var st = g.Save();
                g.TranslateTransform(x, y);
                notch.DrawShape(g, w, h, (int)s.Radius, 190, glass: false);
                Halo.Widgets.Greeting.DrawHello(g, Halo.Widgets.Greeting.InkBox(w, h),
                    s.Written, s.HelloAlpha, System.Drawing.Color.White, 11f);
                g.Restore(st);
                g.DrawString($"t={t:0.00}", cap, capBrush, pad, y + h - 4f);
                y += h + pad;
            }
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"wrote {outPath}");
    }

    private static void RenderAsk(string outPath)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(20);
        var question = new Halo.ClaudeCode.PendingAsk(
            "n1", 100, "sess", "AskUserQuestion", null,

            "Fix the frame cadence first, or the icon nobody can miss?",
            [new Halo.ClaudeCode.AskOption("Cadence", "the CPU one"),
             new Halo.ClaudeCode.AskOption("Icon", "the visible one"),
             new Halo.ClaudeCode.AskOption("Measure more first",
                 "no code yet - sit on the profiler until the regression names itself, "
                 + "which is the option that costs a day and saves three")], expires);

        var permission = new Halo.ClaudeCode.PendingAsk(
            "n2", 100, "sess", "Bash", "git push --force-with-lease origin master", null,
            [new Halo.ClaudeCode.AskOption("allow", "run it"),
             new Halo.ClaudeCode.AskOption("deny", "skip it")], expires);

        int W = Halo.Widgets.AskBanner.W, pad = 24;
        int h1 = Halo.Widgets.AskBanner.Height(question, W);
        int h2 = Halo.Widgets.AskBanner.Height(permission, W);

        int[] tints =
        [
            Halo.Shell.NotchController.TintAskDesk,
            Halo.Shell.NotchController.TintAskApp,
        ];
        int total = h1 * tints.Length + h2 + pad * (tints.Length + 2);
        using var bmp = new System.Drawing.Bitmap(W + pad * 2, total);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {

            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, W + pad * 2, total),
                System.Drawing.Color.FromArgb(70, 150, 210), System.Drawing.Color.FromArgb(210, 110, 70), 35f))
                g.FillRectangle(lg, 0, 0, W + pad * 2, total);

            using (var wb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(238, 240, 244)))
            using (var kb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(18, 18, 20)))
                for (int i = 0; i < tints.Length + 1; i++)
                {
                    g.FillRectangle(wb, 0, pad + i * (h1 + pad) + 92, W + pad * 2, 74);
                    g.FillRectangle(kb, 0, pad + i * (h1 + pad) + 176, W + pad * 2, 74);
                }

            g.TranslateTransform(pad, pad);
            for (int i = 0; i < tints.Length; i++)
            {

                string? typed = i == 1 ? "\u0633\u0644\u0627\u0645 - profile first" : null;
                new Halo.Shell.LayeredNotch().DrawShape(g, W, h1, 26, tints[i], glass: false);
                Halo.Widgets.AskBanner.Draw(g, W, h1, 1f, question, hover: 1, tints[i], typed);
                g.TranslateTransform(0, h1 + pad);
            }
            new Halo.Shell.LayeredNotch().DrawShape(g, W, h2, 26, tints[^1], glass: false);
            Halo.Widgets.AskBanner.Draw(g, W, h2, 1f, permission, hover: -1, tints[^1]);
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RenderNotif(string outPath)
    {

        int W = Halo.Widgets.NotifBanner.W, H = Halo.Widgets.NotifBanner.SummaryH, pad = 24, detailRoom = 340;
        using var bmp = new System.Drawing.Bitmap(W + pad * 2, H * 2 + detailRoom + pad * 4);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, W + pad * 2, H * 2 + pad * 3),
                System.Drawing.Color.FromArgb(70, 150, 210), System.Drawing.Color.FromArgb(210, 110, 70), 35f))
                g.FillRectangle(lg, 0, 0, W + pad * 2, H * 2 + pad * 3);
            g.TranslateTransform(pad, pad);

            using var icon = new System.Drawing.Bitmap(64, 64);
            using (var ig = System.Drawing.Graphics.FromImage(icon))
            {
                ig.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                ig.Clear(System.Drawing.Color.Transparent);
                using var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 40, 150, 235));
                ig.FillEllipse(b, 2, 2, 60, 60);
            }

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
                Icon = icon,

                App = Halo.Notifications.NotifItem.ScreenshotApp,
                Title = Halo.Notifications.NotifItem.ScreenshotTitle,

                Body = "Saved to the clipboard. Click the banner to edit it, or press Ctrl+V in any "
                     + "app to paste it straight in.",
                Code = "482913",
                Preview = shot,
            };
            Halo.Widgets.NotifBanner.Draw(g, W, H, 1f, n, 0f, false);

            g.TranslateTransform(0, H + pad);
            new Halo.Shell.LayeredNotch().DrawShape(g, W, H, 26, 245, glass: false);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Halo.Widgets.NotifBanner.Draw(g, W, H, 1f, new Halo.Notifications.NotifItem
            {
                Icon = icon,
                App = "Telegram",
                Title = "\u0633\u0644\u0627\u0645",
                Body = "\u0628\u0632\u0646 \u0628\u0631\u06cc\u0645",

                Stacked = 5,
            }, 0f, false);

            var mixed = new Halo.Notifications.NotifItem
            {
                Icon = icon,
                App = "ChatGPT",
                Title = "\u0633\u0627\u062e\u062a \u067e\u0646\u0644 \u0645\u062f\u06cc\u0631\u06cc\u062a \u062f\u0633\u062a\u0631\u0633\u06cc Halo",
                Body = "\u0627\u0648\u06a9\u06cc\u060c \u0622\u062e\u0631\u06cc\u0646 \u0627\u0646\u062a\u062e\u0627\u0628: \u0645\u0646\u0648 \u0633\u0645\u062a \u0686\u067e\u060c \u0645\u062d\u062a\u0648\u0627\u06cc \u062a\u0646\u0638\u06cc\u0645\u0627\u062a \u0633\u0645\u062a \u0631\u0627\u0633\u062a. "
                     + "Halo | Media | [ Enabled ] | General | Appearance | Playback | "
                     + "Auto-show on track change | FEATURES | Include VLC | Show collapsed progress | "
                     + "Downloads | File Tray | Bluetooth | Follow active player | Notifications | "
                     + "Idle timeout 15 sec | Claude",
            };
            int dh = Math.Min(detailRoom, Halo.Widgets.NotifBanner.DetailHeight(mixed));
            g.TranslateTransform(0, H + pad);
            new Halo.Shell.LayeredNotch().DrawShape(g, W, dh, 26, 245, glass: false);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Halo.Widgets.NotifBanner.Draw(g, W, dh, 1f, mixed, 1f, true);
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RenderBar(string outPath, string? accentHex, string? fracStr)
    {
        const int W = 220, H = 40, Zoom = 2, Rows = 7, Pad = 8;
        var accent = System.Drawing.Color.FromArgb(228, 168, 64);
        float frac = fracStr != null
            ? float.Parse(fracStr, System.Globalization.CultureInfo.InvariantCulture) : 0.42f;
        if (accentHex != null)
            accent = System.Drawing.Color.FromArgb(
                (int)(uint.Parse(accentHex, System.Globalization.NumberStyles.HexNumber) | 0xFF000000));
        using var bmp = new System.Drawing.Bitmap(W * Zoom + Pad * 2 + 150,
            (H * Zoom + Pad) * Rows + Pad, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.FromArgb(255, 18, 18, 21));
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var label = new System.Drawing.Font("Segoe UI", 13f, System.Drawing.GraphicsUnit.Pixel);
        using var lb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(225, 225, 230));

        for (int r = 0; r < Rows; r++)
        {
            bool paused = r == Rows - 1;
            g.DrawString(paused ? "paused" : $"playing +{r * 430}ms", label, lb, 8, Pad + r * (H * Zoom + Pad) + 26);
            using var pill = new System.Drawing.Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var pg = System.Drawing.Graphics.FromImage(pill))
            {
                pg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var back = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 12, 12, 14)))
                using (var pp = Halo.Widgets.Fx.PillPath(W, H, H / 2f))
                    pg.FillPath(back, pp);

                Halo.Widgets.Fx.PillBar(pg, W, H, 1f, frac, accent, 0.5f, alive: !paused);
            }
            g.DrawImage(pill, new System.Drawing.Rectangle(150, Pad + r * (H * Zoom + Pad), W * Zoom, H * Zoom));
            if (!paused) System.Threading.Thread.Sleep(430);
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine(outPath);
    }

    private static void ProbeSeek(double secs, int count)
    {
        var sessions = new Halo.Widgets.MediaSessions();
        for (int i = 0; i < 40 && sessions.Session(0) is null; i++) System.Threading.Thread.Sleep(100);
        var s = sessions.Session(0);
        if (s is null) { Console.WriteLine("no session"); return; }

        var widget = new Halo.Widgets.MediaWidget(sessions, 0);

        using var scratch = new System.Drawing.Bitmap(220, 40,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var sg = System.Drawing.Graphics.FromImage(scratch);
        void Pump() { try { widget.DrawCollapsed(sg, 220, 40, 1f); } catch { } }
        for (int i = 0; i < 20 && widget.RingProgress < 0f; i++) { Pump(); System.Threading.Thread.Sleep(100); }

        Console.WriteLine($"before   pos={s.GetTimelineProperties().Position}");

        for (int n = 1; n <= count; n++)
        {
            widget.SeekByForProbe((int)secs);

            int gap = int.TryParse(Environment.GetEnvironmentVariable("HALO_SEEK_GAP"), out var gv) ? gv : 120;
            for (int k = 0; k < Math.Max(1, gap / 100); k++) { Pump(); System.Threading.Thread.Sleep(100); }
            Console.WriteLine($"tap {n}    player={s.GetTimelineProperties().Position}"
                + $"  widget={widget.PositionForProbe}  ring={widget.RingProgress:0.0000}");
        }
        for (int i = 1; i <= 16; i++)
        {
            System.Threading.Thread.Sleep(400);
            Pump();
            var now = s.GetTimelineProperties();
            Console.WriteLine($"  +{i * 400,4}ms pos={now.Position}  updated={now.LastUpdatedTime:HH:mm:ss.fff}"
                + $"  widget.RingProgress={widget.RingProgress:0.0000}");
        }
    }

    private static void ProbeTimeline()
    {
        var sessions = new Halo.Widgets.MediaSessions();

        for (int i = 0; i < 40 && sessions.Session(0) is null; i++) System.Threading.Thread.Sleep(100);
        var slots = new Halo.Widgets.MediaWidget[Halo.Widgets.MediaSessions.MaxSlots];
        for (int s = 0; s < slots.Length; s++) slots[s] = new Halo.Widgets.MediaWidget(sessions, s);
        for (int i = 0; i < 30; i++)
        {
            for (int s = 0; s < slots.Length; s++)
            {
                if (sessions.Session(s) is null) continue;
                Console.WriteLine($"{i * 0.5,5:0.0}s [{s}] {slots[s].ProbeLine() ?? "session hooked, no title yet"}");
            }
            System.Threading.Thread.Sleep(500);
        }
    }

    private static void ProbeMedia()
    {
        var sessions = new Halo.Widgets.MediaSessions();
        for (int i = 0; i < 40 && sessions.Session(0) is null; i++) System.Threading.Thread.Sleep(100);

        for (int slot = 0; slot < Halo.Widgets.MediaSessions.MaxSlots; slot++)
        {
            var s = sessions.Session(slot);
            if (s is null) { Console.WriteLine($"slot {slot}   (empty)"); continue; }
            string? aumid = null;
            try { aumid = s.SourceAppUserModelId; } catch { }
            Console.WriteLine($"slot {slot}   app='{sessions.SlotApp(slot)}'  aumid='{aumid}'");

            bool thumb = false;
            try
            {
                var props = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                thumb = props?.Thumbnail != null;
                Console.WriteLine($"          title='{props?.Title}'  thumbnail={(thumb ? "yes" : "NONE")}");
            }
            catch (Exception ex) { Console.WriteLine("          properties failed: " + ex.Message); }

            try
            {
                var tl = s.GetTimelineProperties();
                var pb = s.GetPlaybackInfo();
                Console.WriteLine($"          pos={tl.Position} start={tl.StartTime} end={tl.EndTime}");
                Console.WriteLine($"          minSeek={tl.MinSeekTime} maxSeek={tl.MaxSeekTime}"
                    + $"  lastUpdated={tl.LastUpdatedTime:HH:mm:ss}");
                Console.WriteLine($"          canSeek={pb.Controls.IsPlaybackPositionEnabled}"
                    + $" canRate={pb.Controls.IsPlaybackRateEnabled} rate={pb.PlaybackRate}"
                    + $" state={pb.PlaybackStatus} type={pb.PlaybackType}");
            }
            catch (Exception ex) { Console.WriteLine("          timeline failed: " + ex.Message); }

            var shell = aumid is null ? null : Halo.Notifications.ShellIcon.ForAumid(aumid);
            var exe = Halo.Widgets.AppIcon.ForAumid(aumid);
            var chain = Halo.Widgets.AppIcon.ForSessionApp(aumid);
            Console.WriteLine($"          ShellIcon={(shell is null ? "NULL" : $"{shell.Width}x{shell.Height}")}"
                + $"   AppIcon={(exe is null ? "NULL" : $"{exe.Width}x{exe.Height}")}"
                + $"   chain={(chain is null ? "NULL → the glyph fallback draws" : $"{chain.Width}x{chain.Height}")}");
        }
    }

    private static void RenderGlyphs(string outPath)
    {
        (string glyph, string name)[] rows =
        {
            ("", "media art fallback"),
            ("", "media (menu)"),
            ("", "agent fallback"),
            ("", "download"),
            ("", "robot / generic agent"),
            ("", "bluetooth"),
            ("", "file tray"),
        };
        const int Tile = 22, Zoom = 6, Pad = 10, LabelW = 190;
        int cell = Tile * Zoom;
        int width = LabelW + Pad * 3 + cell * 2, height = Pad + rows.Length * (cell + Pad);

        using var bmp = new System.Drawing.Bitmap(width, height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.FromArgb(255, 24, 24, 28));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var label = new System.Drawing.Font("Segoe UI", 15f, System.Drawing.GraphicsUnit.Pixel);
        using var head = new System.Drawing.Font("Segoe UI Semibold", 14f, System.Drawing.GraphicsUnit.Pixel);
        using var lb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 235, 240));
        using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        using var tileBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(40, 255, 255, 255));
        using var cross = new System.Drawing.Pen(System.Drawing.Color.FromArgb(120, 255, 90, 140), 1f);

        int y = Pad;
        for (int i = 0; i < rows.Length; i++)
        {
            var (glyph, name) = rows[i];
            g.DrawString(name, label, lb, new System.Drawing.PointF(Pad, y + cell / 2f - 10f));
            if (i == 0)
            {
                g.DrawString("StringFormat", head, lb, new System.Drawing.PointF(LabelW + Pad * 2, 2f));
                g.DrawString("ink", head, lb, new System.Drawing.PointF(LabelW + Pad * 3 + cell, 2f));
            }

            for (int col = 0; col < 2; col++)
            {
                float tx = LabelW + Pad * 2 + col * (cell + Pad);
                var tile = new System.Drawing.RectangleF(tx, y, cell, cell);

                using (var p = Halo.Widgets.Fx.Rounded(tile, 14f * Zoom)) g.FillPath(tileBrush, p);
                g.DrawLine(cross, tx, y + cell / 2f, tx + cell, y + cell / 2f);
                g.DrawLine(cross, tx + cell / 2f, y, tx + cell / 2f, y + cell);

                using var gf = new System.Drawing.Font("Segoe Fluent Icons", Tile * 0.5f * Zoom,
                    System.Drawing.GraphicsUnit.Pixel);
                if (col == 0)
                {
                    using var sf = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic)
                    {
                        Alignment = System.Drawing.StringAlignment.Center,
                        LineAlignment = System.Drawing.StringAlignment.Center,
                    };
                    g.DrawString(glyph, gf, white, tile, sf);
                }
                else Halo.Widgets.Fx.GlyphCentred(g, tile, glyph, gf, white);
            }
            y += cell + Pad;
        }

        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine(outPath);
    }

    private static void RenderCopy(string outPath)
    {
        int W = Halo.Widgets.NotifBanner.W, H = Halo.Widgets.NotifBanner.SummaryH;
        const int Zoom = 4, Pad = 6;

        var states = new[] { false, true };
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

                float mid = dst.Y + (Pad + r.Height / 2f) * Zoom;
                using var guide = new System.Drawing.Pen(System.Drawing.Color.FromArgb(150, 255, 70, 70), 1f);
                g.DrawLine(guide, dst.X, mid, dst.Right, mid);
            }
        }
        foreach (var s in shots) s.Dispose();
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RenderLocal(string outPath)
    {
        int W = Halo.Widgets.NotifBanner.W, H = Halo.Widgets.NotifBanner.SummaryH, pad = 20;
        using var shot = new System.Drawing.Bitmap(1920, 1080);
        using (var sg = System.Drawing.Graphics.FromImage(shot))
        {
            using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, 1920, 1080),
                System.Drawing.Color.FromArgb(240, 245, 250), System.Drawing.Color.FromArgb(150, 190, 235), 45f);
            sg.FillRectangle(lg, 0, 0, 1920, 1080);
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

    private static void RenderBadges(string outPath)
    {
        var badges = Halo.Shell.Badges.All();
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

    private static IWidget Tray()
    {
        var tray = new FileTray();
        FileTray.SetDragActive(true);
        return tray;
    }

    private static System.Threading.Mutex? _instance;
    private static Halo.Shell.TrayIcon? _tray;

    private static void OpenSettingsPanel()
    {
        try
        {
            string exe = System.IO.Path.Combine(AppContext.BaseDirectory, "Halo.Settings.exe");
            if (!System.IO.File.Exists(exe)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { }
    }

    internal static void OpenSettings() => OpenSettingsPanel();

    private static void Teardown()
    {
        try { _tray?.Dispose(); _tray = null; } catch { }
        try { _instance?.ReleaseMutex(); } catch { }
        try { _instance?.Dispose(); _instance = null; } catch { }
    }

    internal static void Quit()
    {
        Teardown();
        Environment.Exit(0);
    }

    internal static void Restart()
    {
        string exe = Environment.ProcessPath ?? "";
        Teardown();
        try
        {
            if (exe.Length > 0)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { }
        Environment.Exit(0);
    }

    private static void RenderFluent(string outPath, string startOrList, string countArg)
    {
        var codes = new System.Collections.Generic.List<int>();
        if (startOrList.Contains(','))
        {
            foreach (var part in startOrList.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var c)) codes.Add(c);
        }
        else if (int.TryParse(startOrList, System.Globalization.NumberStyles.HexNumber, null, out var start))
        {
            int n = int.TryParse(countArg, out var parsed) ? parsed : 256;
            for (int i = 0; i < n; i++) codes.Add(start + i);
        }
        if (codes.Count == 0) return;

        const int Cell = 92, Cols = 12, Label = 18;
        int rows = (codes.Count + Cols - 1) / Cols;
        using var bmp = new System.Drawing.Bitmap(Cols * Cell, rows * (Cell + Label) + 10);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(24, 24, 28));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var glyphFont = new System.Drawing.Font("Segoe Fluent Icons", 46f, System.Drawing.GraphicsUnit.Pixel);
            using var labelFont = new System.Drawing.Font("Consolas", 14f, System.Drawing.GraphicsUnit.Pixel);
            using var ink = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var dim = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(150, 160, 170));
            using var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            for (int i = 0; i < codes.Count; i++)
            {
                float x = i % Cols * Cell, y = i / Cols * (Cell + Label);
                g.DrawString(((char)codes[i]).ToString(), glyphFont, ink,
                    new System.Drawing.RectangleF(x, y, Cell, Cell), sf);
                g.DrawString(codes[i].ToString("X4"), labelFont, dim,
                    new System.Drawing.RectangleF(x, y + Cell - 4, Cell, Label), sf);
            }
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RenderWidget(string outPath, string which, int scale = 1, string[]? args = null)
    {
        var t = new System.Threading.Thread(() =>
        {
            if (which == "download")
            {
                Halo.Widgets.Downloads.Name = "Source.Code.2011.1080p.BluRay.10bit.x265.Farsi.Dubbed.mkv";
                Halo.Widgets.Downloads.Percent = 36;

                Halo.Widgets.Downloads.ExePath = new[]
                {
                    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                }.FirstOrDefault(System.IO.File.Exists) ?? @"C:\Windows\explorer.exe";
                Halo.Widgets.Downloads.Hwnd = new IntPtr(1);
            }
            if (which == "download-install")
            {
                Halo.Widgets.Downloads.Name = "Microsoft Store";
                Halo.Widgets.Downloads.ExePath = "Microsoft.WindowsStore_8wekyb3d8bbwe!App";
                Halo.Widgets.Downloads.IsStore = true;
                Halo.Widgets.Downloads.Installing = true;
                which = "download";
            }

            string demoRoot = "";

            string codexRoot = "";
            if (which == "codex-demo")
            {
                codexRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "halo-codex-demo");
                System.IO.Directory.CreateDirectory(codexRoot);
                var cnow = DateTimeOffset.UtcNow;
                System.IO.File.WriteAllText(System.IO.Path.Combine(codexRoot, "cli.json"), $$"""
                {
                  "pid": {{System.Environment.ProcessId}},
                  "source": "cli",
                  "state": "working",
                  "consolePid": {{System.Environment.ProcessId}},
                  "updatedAt": "{{cnow:o}}",
                  "startedAt": "{{cnow.AddMinutes(-4):o}}",
                  "currentTool": "apply_patch",
                  "contextUsed": 712000,
                  "contextMax": 1000000,
                  "primaryLimit": { "usedPercent": 61, "windowMinutes": 300, "resetsAt": "{{cnow.AddHours(1).AddMinutes(52):o}}" },
                  "secondaryLimit": { "usedPercent": 34, "windowMinutes": 10080, "resetsAt": "{{cnow.AddDays(4):o}}" }
                }
                """);
            }
            bool demo = which is "claude-demo" or "claude-idle" or "claude-hot";

            bool hot = which == "claude-hot";
            if (demo)
            {
                demoRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "halo-claude-demo");
                System.IO.Directory.CreateDirectory(demoRoot);
                var now = DateTimeOffset.UtcNow;

                var demoState = which == "claude-idle" ? "idle" : "working";
                long ctxUsed = hot ? 862_000 : 341_000;
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
                  "session": { "contextUsed": {{ctxUsed}}, "contextMax": 1000000, "promptTokens": 48200 }
                }
                """);
                Halo.ClaudeCode.Limits.FiveHour = hot ? 0.93f : 0.42f;
                Halo.ClaudeCode.Limits.FiveHourReset = now.AddHours(2).AddMinutes(48);
            }

            IWidget w = which switch
            {
                "claude-demo" or "claude-idle" or "claude-hot" => new ClaudeCodeWidget(
                    new Halo.ClaudeCode.StatusStore(System.IO.Path.Combine(demoRoot, "status.json"),
                        _ => DateTimeOffset.UtcNow.AddMinutes(-12), watchFiles: false), 0, () => { }),
                "claude" => new ClaudeCodeWidget(new Halo.ClaudeCode.StatusStore(), 0, () => { }),
                "codex-demo" => new CodexWidget(
                    new Halo.Codex.CodexStatusStore(codexRoot, codexRoot, _ => true, watchFiles: false),
                    Halo.Codex.CodexSurface.Cli, () => { }, observeLimits: _ => { }),
                "codex" => new CodexWidget(new Halo.Codex.CodexStatusStore(), Halo.Codex.CodexSurface.Cli, () => { }),
                "download" => new DownloadWidget(),

                "tray" => Tray(),
                _ => new MediaWidget(new MediaSessions(), 0),
            };
            for (int i = 0; i < 100 && !w.IsActive; i++)
                System.Threading.Thread.Sleep(100);
            scale = Math.Clamp(scale, 1, 6);
            if (demo || which == "codex-demo")
            {

                using (var warm = new System.Drawing.Bitmap(560, 220))
                using (var wg = System.Drawing.Graphics.FromImage(warm))
                    w.DrawContent(wg, 560, 220, 1f);
            }

            if (which is "claude" or "codex" or "codex-demo" || demo)
                System.Threading.Thread.Sleep(8000);

            if (demo || which == "codex-demo")
            {
                Halo.ClaudeCode.Limits.FiveHour = hot ? 0.93f : 0.42f;
                Halo.ClaudeCode.Limits.FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).AddMinutes(48);
                Halo.ClaudeCode.Limits.Week = hot ? 0.78f : 0.61f;
                Halo.ClaudeCode.Limits.WeekReset = DateTimeOffset.UtcNow.AddDays(3).AddHours(5);
                Halo.ClaudeCode.Limits.CreditsUsed = 0;
                Halo.ClaudeCode.Limits.LastSuccess = DateTime.UtcNow.AddMinutes(-2);

                const string demoIp = "203.0.113.24";
                Halo.ClaudeCode.IpCountry.Ip = demoIp;
                Halo.ClaudeCode.IpCountry.ApiIp = null;
                Halo.ClaudeCode.IpCountry.Cc = "NL";
                Halo.ClaudeCode.IpCountry.Isp = "Example ISP";
                Halo.ClaudeCode.IpCountry.Asn = "AS64496";
                try
                {
                    using var flagHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    Halo.ClaudeCode.IpCountry.Flag = new System.Drawing.Bitmap(new System.IO.MemoryStream(
                        flagHttp.GetByteArrayAsync("https://flagcdn.com/w320/nl.png").Result));
                }
                catch { }
                Halo.ClaudeCode.IpRep.ForIp = demoIp;
                Halo.ClaudeCode.IpRep.Verdict = "residential";
                Halo.ClaudeCode.IpRep.Abuse = null;
                Halo.ClaudeCode.IpRep.Sev = 0;
                Halo.ClaudeCode.IpRep.Tor = false;
                Halo.ClaudeCode.IpRep.Abuser = false;
                Halo.ClaudeCode.IpRep.Bogon = false;
                Halo.ClaudeCode.IpRep.Vpn = false;
                Halo.ClaudeCode.IpRep.Proxy = false;
                Halo.ClaudeCode.IpRep.Datacenter = false;
                Halo.ClaudeCode.DnsLeak.ForIp = demoIp;
                Halo.ClaudeCode.DnsLeak.Running = false;
                Halo.ClaudeCode.DnsLeak.Done = true;
                Halo.ClaudeCode.DnsLeak.Resolvers = 3;
                Halo.ClaudeCode.DnsLeak.Where = "NL";
                Halo.ClaudeCode.DnsLeak.Leaking = false;
            }

            if (args is { Length: > 4 } && args[4].Contains(','))
            {
                var xy = args[4].Split(',');
                if (float.TryParse(xy[0], out float mx) && float.TryParse(xy[1], out float my))
                {
                    Halo.Widgets.WidgetInput.Mouse = new System.Drawing.PointF(mx, my);
                    Halo.Widgets.WidgetInput.Over = true;
                }
            }

            if (Environment.GetEnvironmentVariable("HALO_RENDER_NET") == "1")
            {
                Halo.ClaudeCode.IpCountry.Poke();
                System.Threading.Thread.Sleep(5000);
                string? exit = Halo.ClaudeCode.IpCountry.Split
                    ? Halo.ClaudeCode.IpCountry.ApiIp : Halo.ClaudeCode.IpCountry.Ip;
                Halo.ClaudeCode.IpRep.Want(exit);
                Halo.ClaudeCode.DnsLeak.Want(exit,
                    Halo.ClaudeCode.IpCountry.Split ? Halo.ClaudeCode.IpCountry.ApiCc : Halo.ClaudeCode.IpCountry.Cc);
                for (int i = 0; i < 40 && !Halo.ClaudeCode.DnsLeak.Done; i++) System.Threading.Thread.Sleep(500);
            }

            using (var warm = new System.Drawing.Bitmap(560, 220,
                       System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (var wg = System.Drawing.Graphics.FromImage(warm))
                for (int f = 0; f < 45; f++)
                {
                    wg.Clear(System.Drawing.Color.FromArgb(20, 20, 22));
                    try { w.DrawContent(wg, 560, 220, 1f); } catch { }
                    System.Threading.Thread.Sleep(12);
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
