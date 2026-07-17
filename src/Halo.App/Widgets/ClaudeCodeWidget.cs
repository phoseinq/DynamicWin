using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Halo.ClaudeCode;

namespace Halo.Widgets;

internal sealed class ClaudeCodeWidget : IWidget
{
    private static readonly Color Blue = Color.FromArgb(91, 157, 255);
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);
    private static readonly Color Track = Color.FromArgb(38, 255, 255, 255);
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);

    private readonly StatusStore _store;
    private readonly int _slot;
    private readonly Action _cancel;

    public ClaudeCodeWidget(StatusStore store, int slot, Action cancel)
    {
        _store = store;
        _slot = slot;
        _cancel = cancel;
    }

    private static readonly Bitmap? ClaudeIcon = LoadIcon();
    internal static Bitmap? PlainIcon => ClaudeIcon; // unbadged mark for the grouped closed circle
    // icon-derived accent for the background wash (Claude coral); fallback if the icon is greyscale
    private static readonly Color Accent = Fx.AccentOf(ClaudeIcon) is var a && a != Fx.White
        ? a : Color.FromArgb(217, 119, 87);

    public string Icon => "\uE756"; // Segoe MDL2 CommandPrompt (fallback)

    // session icon = Claude mark + session-number badge (stable for the session's lifetime,
    // easier to find than a cwd initial — user's call)
    private Bitmap? _badged;

    public Bitmap? IconImage
    {
        get
        {
            if (ClaudeIcon is null) return null;
            return _badged ??= Badge(ClaudeIcon, (char)('1' + _slot));
        }
    }

    private static Bitmap Badge(Bitmap icon, char letter)
    {
        var b = new Bitmap(icon.Width, icon.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawImage(icon, 0, 0, icon.Width, icon.Height);
        float d = icon.Width * 0.42f, x = icon.Width - d, y = icon.Height - d;
        using (var bg = new SolidBrush(Color.FromArgb(230, 24, 24, 26)))
            g.FillEllipse(bg, x, y, d, d);
        using var f = new Font("Segoe UI Semibold", d * 0.62f, GraphicsUnit.Pixel);
        using var wb = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(letter.ToString(), f, wb, new RectangleF(x, y - d * 0.02f, d, d), sf);
        return b;
    }

    // one widget per session slot; visible only while that session's process is alive
    public bool IsActive => Live is not null;
    private CcStatus? Live => _store.SessionLive(_slot);
    public Color? Ring => Live is { } st ? RingColor(st) : null;
    public int Version => _store.Version + NetMon.Version;
    public AgentNotice AgentNotice => Live is { } status
        ? new AgentNotice(status.State, ParseTime(status.CompactedAt), status.Message)
        : AgentNotice.None;
    // text-emerge animation + the compacting pulse both need frames while collapsed
    public bool Animating => _appear < 1f || Compacting(Live);

    private string _shownKey = "";
    private float _appear = 1f;

    private static Bitmap? LoadIcon()
    {
        try
        {
            using var s = typeof(ClaudeCodeWidget).Assembly.GetManifestResourceStream("Halo.Assets.claude.png");
            return s != null ? new Bitmap(s) : null;
        }
        catch { return null; }
    }

    private bool CanCancel => Live is { State: "working", Pid: > 0 };

    private bool _wasOpen;

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        bool open = fade > 0.01f;
        if (open && !_wasOpen) Limits.OnPanelOpen(); // one refresh per open (spam-guarded)
        _wasOpen = open;
        if (open)
        {
            NetMon.Poke();
            Fx.Glow(g, w, h, fade, w * 0.16f, h * 0.35f, w * 0.85f, h * 1.2f, 30, Accent);
            DrawExpanded(g, w, h, fade, Live);
        }
    }

    // collapsed pill = Claude icon on the left, what it's doing on the right (Apple-style)
    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        var st = Live;
        float sz = (h - 16f) * 0.82f, x = 13, y = (h - sz) / 2f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 26, Accent);
        if (Compacting(st)) // soft blue breathing across the whole pill = process running
        {
            float pulse = 0.5f - 0.5f * MathF.Cos(Environment.TickCount % 2400 / 2400f * MathF.Tau);
            using var pb = new SolidBrush(Mul(Blue, fade * (0.05f + 0.11f * pulse)));
            using var pp = Rounded(new RectangleF(0, 0, w, h), h / 2f);
            g.FillPath(pb, pp);
        }
        // subtle status ring around the (circular) icon: green working, red on error, white otherwise
        using (var pen = new Pen(Mul(RingColor(st), fade * 0.55f), 1.9f))
            g.DrawEllipse(pen, x - 2.5f, y - 2.5f, sz + 5f, sz + 5f);
        if (ClaudeIcon != null) DrawIcon(g, ClaudeIcon, x, y, sz, fade, sz / 2f); // circular
        else
            using (var db = new SolidBrush(Mul(RingColor(st), fade)))
                g.FillEllipse(db, x, y, sz, sz);

        // balanced zones: verb hugs the icon, the timer owns the right edge — text length changes
        // never leave a lopsided gap. Moods (idle/offline) centre in the whole free space instead.
        string verb = OutageText() ?? st?.State switch
        {
            "working" => ToolVerb(st.CurrentTool),
            "compacting" when Compacting(st) => "compacting…",
            "waiting_input" => "your move ;)",
            _ => IdleMood(st),
        };
        string el = Elapsed(st);
        if (Compacting(st) && el.Length > 0) el = CompactPct(st!) + " · " + el;
        if (verb != _shownKey) { _shownKey = verb; _appear = 0f; } // timer ticking doesn't retrigger
        else if (_appear < 1f) _appear = Math.Min(1f, _appear + 0.1f);
        float e = 1f - MathF.Pow(1f - _appear, 3);
        bool busy = st?.State == "working" || Compacting(st);
        bool centred = !busy && st?.State != "waiting_input";

        float textX = x + sz + 11;
        using var tf2 = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        float elW = el.Length > 0 ? g.MeasureString(el, tf2, int.MaxValue, StringFormat.GenericTypographic).Width : 0;
        float avail = (w - 14) - textX - (elW > 0 ? elW + 10 : 0);

        float px = 15f;
        using (var fm = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel))
        {
            var m0 = g.MeasureString(verb, fm, int.MaxValue, StringFormat.GenericTypographic);
            if (m0.Width > avail && m0.Width > 0) px = Math.Max(9f, px * avail / m0.Width);
        }
        using var f = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, fade * e));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = centred ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var clip = g.Clip;
        g.SetClip(new RectangleF(x + sz + 2, 0, w - (x + sz + 2), h)); // text is born from behind the icon
        float zoneW = centred ? avail - 34f : avail + 16f; // centred moods lean toward the icon
        g.DrawString(verb, f, b, new RectangleF(textX - 16f * (1f - e), 0, zoneW, h), sf);
        g.Clip = clip;

        if (elW > 0) // timer zone, right-aligned and dimmer so the verb stays the focus
            using (var eb = new SolidBrush(Mul(Dim, fade * e)))
            using (var esf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(el, tf2, eb, new RectangleF(w - 14 - elW - 4, 0, elW + 4, h), esf);

    }

    private static string? _cancelledCompactKey; // startedAt of a compact the user Esc'd out of

    public static void MarkCompactCancelled(string? startedAt) => _cancelledCompactKey = startedAt;

    private static bool Compacting(CcStatus? st) =>
        st?.State == "compacting" && st.StartedAt != _cancelledCompactKey
        && ParseTime(st.StartedAt) is { } t
        && DateTimeOffset.UtcNow - t < TimeSpan.FromMinutes(3); // backstop if the Esc guess misses

    // deliberately approximate: % of the LAST compact's duration (post-compact hook records it),
    // capped at 99 — no real progress signal exists, this is honest pacing, not ground truth
    private static string CompactPct(CcStatus st)
    {
        if (ParseTime(st.StartedAt) is not { } t) return "";
        // ×3: user-tuned pacing — crawling past reality beats finishing before the compact does
        double expect = 3 * (st.LastCompactMs is > 3000 and < 600_000 ? st.LastCompactMs / 1000.0 : 60);
        return $"~{(int)Math.Clamp(100 * (DateTimeOffset.UtcNow - t).TotalSeconds / expect, 1, 99)}%";
    }

    private static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t : null;

    private static void DrawIcon(Graphics g, Bitmap img, float x, float y, float size, float fade, float radius)
    {
        using var path = Rounded(new RectangleF(x, y, size, size), radius);
        int s = Math.Max(1, (int)Math.Ceiling(size));
        using var scaled = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(scaled))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
            int side = Math.Min(img.Width, img.Height);
            sg.DrawImage(img, new Rectangle(0, 0, s, s), (img.Width - side) / 2, (img.Height - side) / 2, side, side, GraphicsUnit.Pixel, ia);
        }
        using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
        tb.TranslateTransform(x, y);
        g.FillPath(tb, path);
    }

    private void DrawExpanded(Graphics g, int w, int h, float a, CcStatus? st)
    {
        int pad = 26;
        using var title = new Font("Segoe UI Semibold", 21f, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 12.5f, GraphicsUnit.Pixel);

        var dot = StateColor(st?.State == "compacting" && !Compacting(st) ? null : st?.State);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var db = new SolidBrush(Mul(dot, a)))
            g.FillEllipse(db, pad, pad + 8, 11, 11); // centred on the title's cap height
        using (var tb = new SolidBrush(Mul(White, a)))
            g.DrawString("Claude Code", title, tb, pad + 20, pad - 2);
        string line = st?.State == "waiting_input" && !string.IsNullOrEmpty(st.Message)
            ? st.Message! : Activity(st); // show the actual question while Claude waits
        using (var ab = new SolidBrush(Mul(st?.State == "waiting_input" ? Amber : Dim, a)))
            g.DrawString(line, small, ab, pad + 20, pad + 24);

        // limits + graph stay up even with no session — only the context bar needs a live transcript
        float y = pad + 58;
        int barW = w - pad * 2;
        if (st?.Session is { } sess)
        {
            double ctx = ContextFrac(st);
            long maxK = sess.ContextMax / 1000, usedK = Math.Min(sess.ContextUsed / 1000, maxK);
            string maxLabel = maxK >= 1000 ? $"{maxK / 1000f:0.#}M" : $"{maxK}K";
            DrawBar(g, pad, y, barW, "Context", $"{usedK}K / {maxLabel}", ctx, Blue, a, body, small);
        }
        else
        {
            using var nb = new SolidBrush(Mul(Dim, a));
            g.DrawString("No active Claude Code session", body, nb, pad, y + 4);
        }
        // hovering a limit row swaps its value for the precise one (exact % + absolute reset time)
        string LimitValue(float f, DateTimeOffset reset, float rowY)
        {
            bool hov = WidgetInput.Over && WidgetInput.Mouse.Y >= rowY && WidgetInput.Mouse.Y < rowY + 36
                && WidgetInput.Mouse.X >= pad && WidgetInput.Mouse.X <= pad + barW;
            return hov ? $"{f * 100:0.#}%  ·  resets {reset.ToLocalTime():ddd HH:mm}"
                       : $"{Pct(f)}  ·  {ResetIn(reset)}";
        }
        if (Limits.FiveHour >= 0)
            DrawBar(g, pad, y + 40, barW, "5-hour limit",
                LimitValue(Limits.FiveHour, Limits.FiveHourReset, y + 40), Limits.FiveHour, UsageColor(Limits.FiveHour), a, body, small);
        if (Limits.Week >= 0)
            DrawBar(g, pad, y + 80, barW, "Weekly limit",
                LimitValue(Limits.Week, Limits.WeekReset, y + 80), Limits.Week, UsageColor(Limits.Week), a, body, small);

        // usage freshness + manual refresh (clickable)
        var rr = RefreshRect(w, h);
        bool rHover = WidgetInput.Over && rr.Contains(WidgetInput.Mouse);
        string age = Limits.LastSuccess == DateTime.MinValue ? "usage never fetched"
            : $"updated {AgeText(DateTime.UtcNow - Limits.LastSuccess)}";
        string rtxt = $"{age}  ·  ⟳ refresh";
        using (var rb = new SolidBrush(Mul(rHover ? White : Dim, a)))
        using (var rsf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(rtxt, small, rb, rr, rsf);

        DrawCancel(g, w, h, a, body);
    }

    // small circular stop button (square glyph = stop), red when a prompt can be interrupted
    private void DrawCancel(Graphics g, int w, int h, float a, Font font)
    {
        var r = CancelRect(w, h);
        bool on = CanCancel;
        var col = on ? Red : Color.FromArgb(120, 255, 255, 255);
        float ba = on ? a : a * 0.4f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Mul(Color.FromArgb(46, col), a)))
            g.FillEllipse(b, r.X, r.Y, r.Width, r.Height);
        using (var pen = new Pen(Mul(col, ba), 1.4f))
            g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
        float sq = r.Width * 0.34f;
        using (var sb = new SolidBrush(Mul(on ? Red : Dim, a)))
        using (var sp = Rounded(new RectangleF(r.X + (r.Width - sq) / 2, r.Y + (r.Height - sq) / 2, sq, sq), 2f))
            g.FillPath(sb, sp);

        DrawNet(g, r.X - 26, a); // breathing room between the graph and the stop button
    }

    // connection-to-Anthropic graph: green = your internet (ping 1.1.1.1), blue = path to
    // api.anthropic.com. Lost stretches turn red on that line — so you can tell whose fault it is.
    private static void DrawNet(Graphics g, float rightX, float a)
    {
        var (net, api) = NetMon.Snapshot();
        const float stepX = 5f, gh = 22f;
        int n = net.Length;
        float gw = (n - 1) * stepX, x0 = rightX - gw, top = 19, barsY = top + 14;

        // dynamic scale (api TCP latency is usually way above ping)
        int cap = 150;
        foreach (var v in net) if (v > cap) cap = v;
        foreach (var v in api) if (v > cap) cap = v;
        cap = (cap + 49) / 50 * 50;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float ax = x0 - 5;
        using (var axis = new Pen(Mul(Dim, a * 0.6f), 1f))
        {
            g.DrawLine(axis, ax, barsY - 3, ax, barsY + gh);       // Y axis
            g.DrawLine(axis, ax, barsY + gh, x0 + gw, barsY + gh); // X axis
        }
        using (var tf = new Font("Segoe UI", 9f, GraphicsUnit.Pixel))
        using (var tb = new SolidBrush(Mul(Dim, a * 0.8f)))
        {
            var sz = g.MeasureString(cap.ToString(), tf);
            g.DrawString(cap.ToString(), tf, tb, ax - sz.Width - 1, barsY - 5);
            sz = g.MeasureString("0", tf);
            g.DrawString("0", tf, tb, ax - sz.Width - 1, barsY + gh - 9);
        }

        float Y(int ms) => barsY + gh * (1 - Math.Clamp((float)ms / cap, 0.04f, 1f));

        void Series(int[] s, Color col)
        {
            var pts = new List<(PointF p, bool lost)>();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == NetMon.Empty) continue;
                bool lost = s[i] == NetMon.Lost;
                pts.Add((new PointF(x0 + i * stepX, lost ? barsY : Y(s[i])), lost));
            }
            using var ok = new Pen(Mul(col, a), 1.6f) { LineJoin = LineJoin.Round };
            using var bad = new Pen(Mul(Red, a), 1.6f) { LineJoin = LineJoin.Round };
            for (int i = 1; i < pts.Count; i++)
                g.DrawLine(pts[i - 1].lost || pts[i].lost ? bad : ok, pts[i - 1].p, pts[i].p);
            if (pts.Count > 0)
                using (var db = new SolidBrush(Mul(pts[^1].lost ? Red : col, a)))
                    g.FillEllipse(db, pts[^1].p.X - 2f, pts[^1].p.Y - 2f, 4.5f, 4.5f);
        }
        Series(net, Green);
        Series(api, Blue);

        // colour-coded legend/label: "net 15 · api 210 ms" (a lost side shows ":(")
        int lastN = LastSample(net), lastA = LastSample(api);
        string tn = "net " + (lastN == NetMon.Empty ? "…" : lastN == NetMon.Lost ? ":(" : lastN.ToString());
        string ta = "api " + (lastA == NetMon.Empty ? "…" : lastA == NetMon.Lost ? ":(" : lastA + " ms");
        using (var f = new Font("Segoe UI", 11f, GraphicsUnit.Pixel))
        {
            float wN = g.MeasureString(tn, f).Width, wS = g.MeasureString(" · ", f).Width, wA = g.MeasureString(ta, f).Width;
            float lx = rightX - (wN + wS + wA);
            using (var b = new SolidBrush(Mul(lastN == NetMon.Lost ? Red : Green, a))) g.DrawString(tn, f, b, lx, top - 2);
            using (var b = new SolidBrush(Mul(Dim, a))) g.DrawString(" · ", f, b, lx + wN, top - 2);
            using (var b = new SolidBrush(Mul(lastA == NetMon.Lost ? Red : Blue, a))) g.DrawString(ta, f, b, lx + wN + wS, top - 2);
        }

        DrawNetHover(g, a, net, api, x0, stepX, barsY, gh, rightX, Y);
    }

    private static int LastSample(int[] s)
    {
        for (int i = s.Length - 1; i >= 0; i--) if (s[i] != NetMon.Empty) return s[i];
        return NetMon.Empty;
    }

    // hover: guide line + details box (both paths at that sample, loss counts, whose fault)
    private static void DrawNetHover(Graphics g, float a, int[] net, int[] api,
        float x0, float stepX, float top, float gh, float right, Func<int, float> Y)
    {
        var m = WidgetInput.Mouse;
        if (!WidgetInput.Over || m.X < x0 - 9 || m.X > right + 6 || m.Y < top - 10 || m.Y > top + gh + 10)
            return;
        int idx = Math.Clamp((int)MathF.Round((m.X - x0) / stepX), 0, net.Length - 1);
        int vN = net[idx], vA = api[idx];
        if (vN == NetMon.Empty && vA == NetMon.Empty) return;

        float gx = x0 + idx * stepX;
        using (var guide = new Pen(Mul(White, a * 0.35f), 1f) { DashStyle = DashStyle.Dot })
            g.DrawLine(guide, gx, top - 3, gx, top + gh);
        void Mark(int v, Color col)
        {
            if (v == NetMon.Empty) return;
            using var hb = new SolidBrush(Mul(v == NetMon.Lost ? Red : col, a));
            g.FillEllipse(hb, gx - 2.5f, (v == NetMon.Lost ? top : Y(v)) - 2.5f, 5.5f, 5.5f);
        }
        Mark(vN, Green); Mark(vA, Blue);

        int lostN = 0, cntN = 0, lostA = 0, cntA = 0;
        for (int i = 0; i < net.Length; i++)
        {
            if (net[i] != NetMon.Empty) { cntN++; if (net[i] == NetMon.Lost) lostN++; }
            if (api[i] != NetMon.Empty) { cntA++; if (api[i] == NetMon.Lost) lostA++; }
        }
        string F(int v) => v == NetMon.Lost ? ":(" : v == NetMon.Empty ? "–" : $"{v} ms";
        var lines = new List<(string t, Color c)>
        {
            ($"net {F(vN)}   api {F(vA)}", White),
            ($"loss  net {lostN}/{cntN}  ·  api {lostA}/{cntA}", Dim),
            ("1.1.1.1  ·  api.anthropic.com", Dim),
        };
        if (vA == NetMon.Lost && vN >= 0) lines.Add(("Anthropic's side :(", Amber));
        else if (vN == NetMon.Lost) lines.Add(("your internet :(", Red));

        using var f2 = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);
        float bw2 = 0;
        foreach (var l in lines) bw2 = Math.Max(bw2, g.MeasureString(l.t, f2).Width);
        bw2 += 16;
        float bh2 = lines.Count * 14 + 10;
        float bx = Math.Min(gx + 8, right - bw2), by = top + gh + 8;
        using (var path = Rounded(new RectangleF(bx, by, bw2, bh2), 7))
        {
            using (var bg = new SolidBrush(Mul(Color.FromArgb(232, 20, 20, 22), a))) g.FillPath(bg, path);
            using (var pen = new Pen(Mul(Track, a), 1f)) g.DrawPath(pen, path);
        }
        for (int i = 0; i < lines.Count; i++)
            using (var b = new SolidBrush(Mul(lines[i].c, a)))
                g.DrawString(lines[i].t, f2, b, bx + 8, by + 5 + i * 14);
    }

    private static RectangleF CancelRect(int w, int h)
    {
        const float d = 34, margin = 22;
        return new RectangleF(w - margin - d, 20, d, d);
    }

    private static RectangleF RefreshRect(int w, int h) => new(w - 26 - 220, h - 26, 220, 20);

    private static string AgeText(TimeSpan d) =>
        d.TotalMinutes < 1 ? "just now"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m ago"
        : d.TotalDays < 1 ? $"{(int)d.TotalHours}h ago"
        : $"{(int)d.TotalDays}d ago";

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
        => new[]
        {
            (CancelRect(w, h), (Action<PointF>)(_ => { if (CanCancel) _cancel(); })),
            (RefreshRect(w, h), (Action<PointF>)(_ => Limits.ForceRefresh())),
        };

    private static void DrawBar(Graphics g, float x, float y, float w, string label, string value,
        double frac, Color fill, float a, Font labelFont, Font valueFont)
    {
        using (var lb = new SolidBrush(Mul(White, a)))
            g.DrawString(label, labelFont, lb, x, y);
        var sz = g.MeasureString(value, valueFont);
        using (var vb = new SolidBrush(Mul(Dim, a)))
            g.DrawString(value, valueFont, vb, x + w - sz.Width, y + 1);

        float by = y + 24, bh = 6;
        Fill(g, x, by, w, bh, Mul(Track, a));
        double f = Math.Clamp(frac, 0, 1);
        if (f > 0)
            Fill(g, x, by, (float)(w * f), bh, Mul(fill, a));
    }

    private static void Fill(Graphics g, float x, float y, float w, float h, Color c)
    {
        if (w <= 0) return;
        using var path = Rounded(new RectangleF(x, y, w, h), h / 2f);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color Mul(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);

    private static double ContextFrac(CcStatus? st)
    {
        var s = st?.Session;
        if (s == null || s.ContextMax <= 0) return 0;
        return Math.Clamp((double)s.ContextUsed / s.ContextMax, 0, 1);
    }

    private static Color StateColor(string? state) => state switch
    {
        "working" => Green,
        "compacting" => Blue,
        "waiting_input" => Amber,
        _ => Color.FromArgb(140, 255, 255, 255),
    };

    // ring mirrors the CLI spinner's colours, except its normal orange → green (orange = icon colour,
    // it would vanish): green = working, yellow = deep thinking / needs input, red = error, white = idle
    private static Color RingColor(CcStatus? st)
        => NetMon.ApiDown || NetMon.NetDown ? Red
         : st?.State == "waiting_input" ? Amber
         : Compacting(st) ? Blue
         : st?.State == "working" ? (string.IsNullOrEmpty(st.CurrentTool) ? Amber : Green)
         : White;

    private static string Pct(float f) => $"{(int)Math.Round(f * 100)}%";

    private static Color LerpC(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    // blue up to 50%, then smoothly blends into amber, then red — no hard steps
    private static Color UsageColor(float f) =>
        f <= 0.5f ? Blue
        : f <= 0.75f ? LerpC(Blue, Amber, (f - 0.5f) / 0.25f)
        : LerpC(Amber, Red, Math.Clamp((f - 0.75f) / 0.25f, 0f, 1f));

    private static string ResetIn(DateTimeOffset r)
    {
        if (r == default) return "";
        var d = r - DateTimeOffset.UtcNow;
        if (d.TotalSeconds <= 0) return "now";
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{d.Minutes}m";
    }

    private static string Activity(CcStatus? st)
    {
        string verb = OutageText() ?? st?.State switch
        {
            "working" => ToolVerb(st.CurrentTool),
            "compacting" when Compacting(st) => "compacting…",
            "waiting_input" => "your move ;)",
            _ => IdleMood(st),
        };
        if (st?.State != "working" && !Compacting(st)) return verb;
        var el = Elapsed(st);
        return el.Length > 0 ? $"{verb}  ·  {el}" : verb;
    }

    // minimal mood line when nothing is running
    private static string IdleMood(CcStatus? st) =>
        NetMon.NetDown ? "offline :("
        : NetMon.ApiDown ? "api down :("
        : JustCompacted(st) ? "compacted :)"
        : Limits.FiveHour >= 0.95f ? "outta juice XD"
        : "let's work :)";

    private static bool JustCompacted(CcStatus? st) =>
        DateTimeOffset.TryParse(st?.CompactedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
        && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(20);

    // an outage overrides whatever the verb was — even mid-work "writing…" becomes the error
    private static string? OutageText() =>
        NetMon.NetDown ? "net error :(" : NetMon.ApiDown ? "api error :(" : null;

    private static string ToolVerb(string? tool) => tool switch
    {
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "writing…",
        "Read" => "reading…",
        "Bash" or "PowerShell" => "running…",
        "Grep" or "Glob" => "digging…",
        "WebFetch" => "fetching…",
        "WebSearch" => "googling :P",
        "Task" or "Agent" => "delegating…",
        "TodoWrite" => "planning…",
        "SlashCommand" or "Skill" => "using a skill…",
        "AskUserQuestion" => "asking you :)",
        null or "" => "hmm…",
        _ => tool!.ToLowerInvariant() + "…",
    };

    // how long the current turn (or compact) has been running
    private static string Elapsed(CcStatus? st)
    {
        if ((st?.State != "working" && !Compacting(st)) || string.IsNullOrEmpty(st?.StartedAt)) return "";
        if (!DateTimeOffset.TryParse(st.StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return "";
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 1) return "";
        return d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.Seconds}s";
    }
}
