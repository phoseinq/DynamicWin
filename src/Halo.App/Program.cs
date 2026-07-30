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
        // dev hook: `Halo.App --render-widget <out.png> [media|claude|claude-demo|claude-idle|claude-hot|codex|codex-demo|download] [scale] [x,y]`
        // the optional x,y parks the cursor there, so hover states can be rendered too
        // claude-demo / claude-idle render a synthetic session instead of the live one — for docs and blog images, where
        // the author's real context and real spend have no business appearing.
        if (args.Length >= 2 && args[0] == "--render-widget")
        {
            RenderWidget(args[1], args.Length > 2 ? args[2] : "media",
                args.Length > 3 && int.TryParse(args[3], out int sc) ? sc : 1, args);
            return;
        }
        // dev hook: `Halo.App --render-pill <out.png>` — the COLLAPSED pill, one row per situation. Every
        // other hook renders an expanded panel, but the status ring and the voice both live on the 220x40
        // pill, and how they read TOGETHER as a session tightens is the whole point of both.
        if (args.Length >= 2 && args[0] == "--render-pill") { RenderPill(args[1]); return; }
        // dev hook: `Halo.App --probe-almanac` — the hourly banner's second line, for real: which city the
        // timezone resolves to, what Open-Meteo answered, and the assembled line. The unit tests pin the
        // shape from known parts; this is the only thing that exercises the two live fetches.
        if (args.Length >= 1 && args[0] == "--probe-almanac")
        {
            Console.WriteLine($"zone     {TimeZoneInfo.Local.Id}");
            Console.WriteLine($"place    {Almanac.Place ?? "(none - offset-only zone)"}");
            Almanac.Poke();
            for (int i = 0; i < 60 && Almanac.Latest is null; i++) System.Threading.Thread.Sleep(500);
            Console.WriteLine($"weather  {(Almanac.Latest is { } wx ? $"{wx.TempC}C code {wx.Code} = {Almanac.Sky(wx.Code)}" : "(no reading)")}");
            Console.WriteLine($"country  {Almanac.PlaceCountry ?? "(not geocoded)"}   metric {Almanac.Metric}   solar hijri {Almanac.SolarHijri}");
            Console.WriteLine($"source   {(Almanac.FromDevice ? "windows location" : "time zone")}");
            Console.WriteLine($"label    {Almanac.Label}");
            Console.WriteLine($"title    {Almanac.Headline(DateTime.Now)}");
            Console.WriteLine($"body     {Almanac.Detail(DateTime.Now)}");
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
        // dev hook: `Halo.App --render-glyphs <out.png>` — every fallback glyph in its tile at 6x, the old
        // StringFormat centring beside the ink centring, with crosshairs through the true centre. A glyph
        // being 2px high in a 20px tile is exactly the kind of claim that cannot be settled by eye.
        if (args.Length >= 2 && args[0] == "--render-glyphs") { RenderGlyphs(args[1]); return; }
        // dev hook: `Halo.App --probe-media` — every live SMTC slot, its app id, whether it ships a
        // thumbnail, and what each icon resolver answers for it. The pill falling back to a glyph is always
        // one of these three coming back empty, and guessing which is how a whole evening gets spent.
        if (args.Length >= 1 && args[0] == "--probe-media") { ProbeMedia(); return; }
        // dev hook: `Halo.App --probe-seek <±seconds>` — ask the live session to move by that much and report
        // what it says and what actually happened. Whether a player HONOURS a seek cannot be reasoned about
        // from outside, and two rounds of theorising about it is two rounds too many.
        if (args.Length >= 2 && args[0] == "--probe-seek") { ProbeSeek(double.Parse(args[1],
            System.Globalization.CultureInfo.InvariantCulture)); return; }
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
        // dev hook: `Halo.App --render-shape <png>` — the pill's own glass composite over a flat magenta
        // backdrop. The window cannot be screenshotted, and the edge is where this has gone wrong before:
        // any magenta surviving at the rim is backdrop escaping from under the tint.
        if (args.Length >= 2 && args[0] == "--render-shape")
        {
            // optional 4th arg sweeps the frost mix without a rebuild — the only way to pick that number is
            // to look at three of them side by side
            // "mix,sheen,grain,rim" — any prefix of it; the rest keep their defaults
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
            // an optional third argument feeds it a REAL captured backdrop (see HALO_DUMP_GLASS), which is
            // the only way to check the frosting against the kind of content that showed through wrong
            System.Drawing.Bitmap back;
            if (args.Length >= 3 && System.IO.File.Exists(args[2]))
            {
                using var src0 = new System.Drawing.Bitmap(args[2]);
                using var fit0 = new System.Drawing.Bitmap(560, 220, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var bgg0 = System.Drawing.Graphics.FromImage(fit0))
                    bgg0.DrawImage(src0, new System.Drawing.Rectangle(0, 0, 560, 220));
                // through the same blur the live path uses, so feeding it a RAW grab measures the whole
                // pipeline and not just the tint
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
        // dev hook: `Halo.App --probe-ip` — the exit as both providers see it, which is the only way to
        // check the reputation parse without hovering an uncapturable window
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
        // dev hook: `Halo.App --moods` — the whole vocabulary, one line per key. These reach the screen
        // one at a time, weeks apart, in a window that cannot be screenshotted, so printing the table is
        // the only way to read what the pill can actually say.
        if (args.Length >= 1 && args[0] == "--moods") { Moods(); return; }

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

    // `--moods` prints the whole table: every key, how many lines it has, and the lines themselves.
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
    // The collapsed pill, one row per situation, at 2x with the situation named beside it. The ring and the
    // line are both driven by the SAME MoodContext now, so the thing that has to be judged is whether they
    // agree: a ring warming while the words say "no room to work…" is the design, a ring warming while the
    // words still say "writing…" is a bug. Neither is visible to a unit test, and the live window carries
    // WDA_EXCLUDEFROMCAPTURE, so this is the only way to look at it.
    private static void RenderPill(string outPath)
    {
        var t = new System.Threading.Thread(() =>
        {
            // (label, state, tool, minutes in, context used of 1M, 5-hour usage). The first block is the
            // palette - one row per colour family, so they can be compared side by side, which is the only
            // way to tell whether they are actually distinguishable at 20px. The second block is the same
            // green shell command under rising pressure.
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
                // one file and one widget PER ROW: the tool-run counter is per-widget state, and a shared
                // store would carry one row's turn into the next
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
                // the usage fraction is a static, so it goes in per row immediately before the draw
                Halo.ClaudeCode.Limits.FiveHour = usage;
                Halo.ClaudeCode.Limits.FiveHourReset = DateTimeOffset.UtcNow.AddHours(2);
                Halo.ClaudeCode.Limits.CreditsUsed = 0;

                // The line fades IN over frames (_appear, eased), so a single-frame render draws it at
                // alpha 0 - the first pass of this hook produced six pills with rings and no words on them.
                // Warm up on a throwaway surface, because drawing repeatedly onto the real one would
                // composite the glow six times over.
                using (var warm = new System.Drawing.Bitmap(pw, ph,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                using (var wg = System.Drawing.Graphics.FromImage(warm))
                    for (int f = 0; f < 14; f++) w.DrawCollapsed(wg, pw, ph, 1f);

                using var pill = new System.Drawing.Bitmap(pw, ph,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var pg = System.Drawing.Graphics.FromImage(pill))
                {
                    pg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    // the pill's own plate, because the ring is judged against what sits behind it live
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
        // TWO banners: a body longer than the summary can hold, and a short one. The grabber bar belongs to
        // the first and must be absent from the second — it promises "drag me, there is more", and for every
        // short message it used to promise it falsely. One image is the only way to check that by eye.
        int W = Halo.Widgets.NotifBanner.W, H = Halo.Widgets.NotifBanner.SummaryH, pad = 24;
        using var bmp = new System.Drawing.Bitmap(W + pad * 2, H * 2 + pad * 3);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, W + pad * 2, H * 2 + pad * 3),
                System.Drawing.Color.FromArgb(70, 150, 210), System.Drawing.Color.FromArgb(210, 110, 70), 35f))
                g.FillRectangle(lg, 0, 0, W + pad * 2, H * 2 + pad * 3);
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

            // the short one, mixed FA+EN so the RTL path is exercised on the same image
            g.TranslateTransform(0, H + pad);
            new Halo.Shell.LayeredNotch().DrawShape(g, W, H, 26, 245, glass: false);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Halo.Widgets.NotifBanner.Draw(g, W, H, 1f, new Halo.Notifications.NotifItem
            {
                Icon = icon,
                App = "Telegram",
                Title = "سلام",
                Body = "بزن بریم",
            }, 0f, false);
        }
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void ProbeSeek(double secs)
    {
        var sessions = new Halo.Widgets.MediaSessions();
        for (int i = 0; i < 40 && sessions.Session(0) is null; i++) System.Threading.Thread.Sleep(100);
        var s = sessions.Session(0);
        if (s is null) { Console.WriteLine("no session"); return; }

        // the widget's own view of the same session, so "did the player seek" and "did the BAR follow" are two
        // separate readings rather than one guess
        var widget = new Halo.Widgets.MediaWidget(sessions, 0);
        // the widget refreshes from its draw path, like it does in the pill, so this has to draw too
        using var scratch = new System.Drawing.Bitmap(220, 40,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var sg = System.Drawing.Graphics.FromImage(scratch);
        void Pump() { try { widget.DrawCollapsed(sg, 220, 40, 1f); } catch { } }
        for (int i = 0; i < 20 && widget.RingProgress < 0f; i++) { Pump(); System.Threading.Thread.Sleep(100); }

        var before = s.GetTimelineProperties();
        var target = before.Position + TimeSpan.FromSeconds(secs);
        Console.WriteLine($"before   pos={before.Position}  (asking for {target})");
        bool ok = false;
        try { ok = s.TryChangePlaybackPositionAsync(target.Ticks).AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("threw: " + ex.Message); }
        Console.WriteLine($"returned {ok}");
        for (int i = 1; i <= 6; i++)
        {
            System.Threading.Thread.Sleep(400);
            Pump();
            var now = s.GetTimelineProperties();
            Console.WriteLine($"  +{i * 400,4}ms pos={now.Position}  updated={now.LastUpdatedTime:HH:mm:ss.fff}"
                + $"  widget.RingProgress={widget.RingProgress:0.0000}");
        }
    }

    // dev-only: what each live media session actually offers the pill to draw. Three things decide whether
    // the art tile shows something real or falls back to a glyph — the track's own thumbnail, then the shell's
    // icon for the app id, then the icon inside the app's exe — and this prints all three per slot.
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

            // the numbers the seek bar is built on. Guessing at these cost a round trip: what a player reports
            // as its seekable window is the whole reason a backward seek can be rejected while a forward one
            // works, and it cannot be reasoned about from outside.
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

    // dev-only: every fallback glyph the pill can end up showing, in the tile it has to fill, magnified, with
    // the old StringFormat centring beside the ink centring and crosshairs through the tile's true centre.
    // The complaint that started it ("the fallback icon is not aligned") is a two-pixel claim about a 20px
    // tile, which is unsettleable on a 1x screen and obvious at 6x.
    private static void RenderGlyphs(string outPath)
    {
        (string glyph, string name)[] rows =
        {
            ("", "media art fallback"),   // MusicInfo — the one that was reported
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
                // the tile is drawn at the magnified size and the glyph asked for a magnified em, so what is
                // being compared is the same geometry the pill uses, six times bigger
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
    private static void RenderWidget(string outPath, string which, int scale = 1, string[]? args = null)
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
            // codex-demo: the Codex twin of claude-demo. Its panel is a ring cluster now, and the ring
            // cluster is the part that cannot be checked any other way - the window it lives in carries
            // WDA_EXCLUDEFROMCAPTURE, so a screenshot of the running pill shows whatever is behind it.
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
            // claude-hot is the same synthetic session wound up to where the warning colours live: the
            // context ring and its figure only disagree once the band flips, so a demo parked at 34%
            // could never have shown the bug that was reported at 86%.
            bool hot = which == "claude-hot";
            if (demo)
            {
                demoRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "halo-claude-demo");
                System.IO.Directory.CreateDirectory(demoRoot);
                var now = DateTimeOffset.UtcNow;
                // the lamp that replaces the stop button when nothing can be interrupted is what the
                // panel shows most of the time, so it needs to be renderable too
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
                _ => new MediaWidget(new MediaSessions(), 0),
            };
            for (int i = 0; i < 100 && !w.IsActive; i++)
                System.Threading.Thread.Sleep(100);
            scale = Math.Clamp(scale, 1, 6);
            if (demo || which == "codex-demo")
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
            if (which is "claude" or "codex" or "codex-demo" || demo)
                System.Threading.Thread.Sleep(8000); // Poke opens an 8s fast-sample window; use all of it
            // Demo figures go in LAST, after that wait: the refetch the warm draw kicked off is asynchronous,
            // and setting them before the sleep let the real answer land on top — the saved frame then showed
            // the author's actual usage and dollar spend, which is the exact thing this mode exists to avoid.
            if (demo || which == "codex-demo")
            {
                Halo.ClaudeCode.Limits.FiveHour = hot ? 0.93f : 0.42f;
                Halo.ClaudeCode.Limits.FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).AddMinutes(48);
                Halo.ClaudeCode.Limits.Week = hot ? 0.78f : 0.61f;
                Halo.ClaudeCode.Limits.WeekReset = DateTimeOffset.UtcNow.AddDays(3).AddHours(5);
                Halo.ClaudeCode.Limits.CreditsUsed = 0;   // no invented dollars on a public image
                Halo.ClaudeCode.Limits.LastSuccess = DateTime.UtcNow.AddMinutes(-2);

                // The exit block has the same problem as the money line, only worse: on this machine it
                // renders the author's real address, ISP and ASN. RFC 5737 TEST-NET-3 and an RFC 5398
                // documentation ASN read as a real exit and can never BE anyone's. Setting ForIp on both
                // probes is the load-bearing part — Want() early-returns when it already holds that ip, so
                // the draw-time calls at the bottom of the panel never go out and never overwrite these.
                const string demoIp = "203.0.113.24";
                Halo.ClaudeCode.IpCountry.Ip = demoIp;
                Halo.ClaudeCode.IpCountry.ApiIp = null;   // Split is Ip != ApiIp, so this keeps it one row
                Halo.ClaudeCode.IpCountry.Cc = "NL";
                Halo.ClaudeCode.IpCountry.Isp = "Example ISP";   // longer and the column ellipsises it
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
            // hover states are half the panel's behaviour (the graph tooltip, the exact-reset swap, the
            // route readout) and none of it could be rendered - so a 5th argument parks the cursor.
            if (args is { Length: > 4 } && args[4].Contains(','))
            {
                var xy = args[4].Split(',');
                if (float.TryParse(xy[0], out float mx) && float.TryParse(xy[1], out float my))
                {
                    Halo.Widgets.WidgetInput.Mouse = new System.Drawing.PointF(mx, my);
                    Halo.Widgets.WidgetInput.Over = true;
                }
            }
            // HALO_RENDER_NET=1 pays a few seconds to let the exit probes land, so the block renders what it
            // will really say. Off by default: every other render wants to be instant.
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
            // Everything in these panels eases toward its target with a time constant, so a single frame
            // renders every hover state part-way there — a menu that opens on hover came out at a fifth of
            // its opacity and read as "not drawn at all". Real frames, on a real clock, until they settle.
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
