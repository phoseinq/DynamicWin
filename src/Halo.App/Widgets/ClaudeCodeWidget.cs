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

    // Session icon = Claude mark + session-number badge, stable for the session's lifetime (easier to find
    // than a cwd initial — user's call). The number only earns its place once there is more than one
    // session to tell apart: a single session wearing a "1" is noise, and it read as a notification count.
    // Same rule as the download widget's counter, and the same rule the Codex twin already followed.
    private Bitmap? _badged;

    public Bitmap? IconImage
    {
        get
        {
            if (ClaudeIcon is null) return null;
            if (_store.LiveSessions() < 2) return ClaudeIcon;
            return _badged ??= Fx.Badge(ClaudeIcon, (char)('1' + _slot));
        }
    }

    // one widget per session slot; visible only while that session's process is alive
    public bool IsActive => Live is not null;
    private CcStatus? Live => _store.SessionLive(_slot);
    public Color? Ring => Live is { } st ? RingColor(st) : null;

    // The ring around the session circle was a full circle that said nothing — permanently "100%". It now
    // draws the usage window as an arc, so the one thing you actually want at a glance (how much of the
    // budget is spent) is readable without opening anything. The COLOUR still comes from what the session
    // is doing, so two facts sit in one mark: colour = state, fill = budget. The 5-hour window first, the
    // weekly one standing in when it is missing; a full ring when neither is known, rather than drawing an
    // empty arc and implying a fresh budget.
    // UsageFrac already prefers the 5-hour window and stands in the weekly one; it returns 0 when neither is
    // known, which as an ARC would read as a fresh budget, so that case asks for a full ring instead.
    public float RingProgress
        => Live is null || (Limits.FiveHour < 0 && Limits.Week < 0) ? -1f : UsageFrac();
    public int Version => _store.Version + NetMon.Version;
    public AgentNotice AgentNotice => Live is { } status
        ? new AgentNotice(status.State, ParseTime(status.CompactedAt), status.Message)
        : AgentNotice.None;
    public IEnumerable<int> OwnerPids => Live is { } st ? new[] { st.Pid, st.ConsolePid } : Array.Empty<int>();
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
        // Backmost layer: how much of the usage window is spent, as the pill's own background — the same
        // "pill IS the bar" language as a download, but a whisper, since here it is ambient context and
        // not the point of the pill. Collapsed only; the expanded panel keeps its labelled bars, which are
        // what you actually compare three numbers on. Skipped while compacting, whose breathing wash owns
        // the whole pill already.
        if (!Compacting(st)) Fx.PillBar(g, w, h, fade, UsageFrac(), Accent, 0.3f);
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 26, Accent);
        if (Compacting(st)) // soft blue breathing across the whole pill = process running
        {
            float pulse = 0.5f - 0.5f * MathF.Cos(Environment.TickCount % 2400 / 2400f * MathF.Tau);
            using var pb = new SolidBrush(Mul(Blue, fade * (0.05f + 0.11f * pulse)));
            using var pp = Fx.PillPath(w, h, h / 2f); // flat top: matches the pill, no corner crescents
            g.FillPath(pb, pp);
        }
        // status ring around the (circular) icon: green on a tool, yellow thinking, red on error, white idle.
        // Alpha was 0.55, which broke the thinking state: amber at 55% over the near-black pill composites to
        // ~(139,94,18), a dark brown-gold only 86 RGB units from the coral icon it hugs (green sits at 164),
        // so "thinking" read as a shadow around the icon and the user reported the ring never turning yellow.
        using (var pen = new Pen(Mul(RingColor(st), fade * 0.9f), 1.9f))
            g.DrawEllipse(pen, x - 2.5f, y - 2.5f, sz + 5f, sz + 5f);
        if (ClaudeIcon != null) DrawIcon(g, ClaudeIcon, x, y, sz, fade, sz / 2f); // circular
        else
            using (var db = new SolidBrush(Mul(RingColor(st), fade)))
                g.FillEllipse(db, x, y, sz, sz);

        // balanced zones: verb hugs the icon, the timer owns the right edge — text length changes
        // never leave a lopsided gap. Moods (idle/offline) centre in the whole free space instead.
        string verb = OutageText() ?? (LimitHit ? "outta juice :(" : st?.State switch
        {
            "working" => ToolVerb(st.CurrentTool),
            "compacting" when Compacting(st) => "compacting…",
            "waiting_input" => "your move ;)",
            _ => IdleMood(st),
        });
        string el = LimitHit ? LimitReset() : Elapsed(st); // limit shows regardless of session state
        if (Compacting(st) && !LimitHit && el.Length > 0) el = CompactPct(st!) + " · " + el;
        if (verb != _shownKey) { _shownKey = verb; _appear = 0f; } // timer ticking doesn't retrigger
        else if (_appear < 1f) _appear = Math.Min(1f, _appear + 0.1f);
        float e = 1f - MathF.Pow(1f - _appear, 3);
        bool busy = st?.State == "working" || Compacting(st) || LimitHit;
        bool centred = !busy && st?.State != "waiting_input";

        float textX = x + sz + 11;
        if (st?.State == "waiting_input") textX += 16; // no timer on the right — breathe off the icon
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
        // lift comes from the font metrics (Fx.CenterLift), not a fixed pixel: px shrinks to fit
        g.DrawString(verb, f, b, new RectangleF(textX - 16f * (1f - e), -Fx.CenterLift(f), zoneW, h), sf);
        g.Clip = clip;

        if (elW > 0) // timer zone, right-aligned and dimmer so the verb stays the focus
            using (var eb = new SolidBrush(Mul(Dim, fade * e)))
            using (var esf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(el, tf2, eb, new RectangleF(w - 14 - elW - 4, -Fx.CenterLift(tf2), elW + 4, h), esf);

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

    // Everything on this panel is positioned from a BASELINE, not from a top-left corner. Mixing 13px and
    // 18px text in one row and giving both the same y puts their baselines ~4px apart, which is exactly
    // the "nothing lines up" the layout kept being accused of - and it is invisible until you look for it.
    // TextTop() converts a baseline into the top-left GDI+ actually wants, using the font's own ascent.
    private const int Pad = 22;
    private const float ColR = 356f, RightEdge = 538f;   // right column: graph, exit, freshness
    private const float RingCx = 84f, RingCy = 132f, RingOuter = 52f, RingBand = 8f, RingStep = 16f;
    private const float KeyX = 178f, KeyValX = 268f;     // key captions and their figures, both fixed
    private const float Row0 = 96f, RowPitch = 42f;      // baselines of the three key rows

    private static float TextTop(Font f, float baseline)
        => baseline - f.FontFamily.GetCellAscent(f.Style) / (float)f.FontFamily.GetEmHeight(f.Style) * f.Size;

    private static void Text(Graphics g, string t, Font f, Brush b, float x, float baseline)
        => g.DrawString(t, f, b, x, TextTop(f, baseline), StringFormat.GenericTypographic);

    private static void TextClipped(Graphics g, string t, Font f, Brush b, float x, float baseline, float w)
    {
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(t, f, b, new RectangleF(x, TextTop(f, baseline), w, f.Size * 1.6f), sf);
    }

    private void DrawExpanded(Graphics g, int w, int h, float a, CcStatus? st)
    {
        using var title = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var line = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var keyCap = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        using var keyVal = new Font("Segoe UI Semibold", 18f, GraphicsUnit.Pixel);
        using var keySub = new Font("Segoe UI", 12.5f, GraphicsUnit.Pixel);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var state = RingColor(st); // yellow while thinking, green on a tool - same as the collapsed ring

        // ---- header. The stop button is the status lamp: same circle, same slot, so the title never
        // shifts between them - red stop while a prompt can be interrupted, plain lamp otherwise.
        DrawCancel(g, w, h, a, state);
        using (var tb = new SolidBrush(Mul(White, a)))
            Text(g, "Claude Code", title, tb, Pad + 38, 40);
        string act = st?.State == "waiting_input" && !string.IsNullOrEmpty(st.Message)
            ? st.Message! : Activity(st); // show the actual question while Claude waits
        using (var ab = new SolidBrush(Mul(st?.State == "waiting_input" ? Amber : Dim, a)))
            TextClipped(g, act, line, ab, Pad + 38, 62, ColR - Pad - 46);

        // ---- the object: three arcs, outer to inner - 5-hour, weekly, context
        double ctxFrac = st?.Session is { ContextMax: > 0 } ? ContextFrac(st) : -1;
        var rings = new (float frac, Color col)[]
        {
            (Limits.FiveHour, Limits.FiveHour >= 0 ? UsageColor(Limits.FiveHour) : Dim),
            (Limits.Week,     Limits.Week     >= 0 ? UsageColor(Limits.Week)     : Dim),
            ((float)ctxFrac,  Blue),
        };
        for (int i = 0; i < rings.Length; i++)
        {
            float r = RingOuter - i * RingStep;
            using (var track = new Pen(Mul(Track, a), RingBand))
                g.DrawArc(track, RingCx - r, RingCy - r, r * 2, r * 2, 0, 360);
            // an unfetched budget draws its track only: an arc at zero would read as "nothing spent yet"
            if (rings[i].frac < 0) continue;
            float sweep = Math.Clamp(rings[i].frac, 0f, 1f) * 360f;
            if (sweep <= 0.5f) continue;
            using var arc = new Pen(Mul(rings[i].col, a), RingBand) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, RingCx - r, RingCy - r, r * 2, r * 2, -90f, sweep);
        }

        // ---- the key. Caption and figure share a baseline; the sub-line sits on the next one down.
        bool KeyHover(int i) => WidgetInput.Over
            && WidgetInput.Mouse.X >= KeyX - 20 && WidgetInput.Mouse.X < ColR - 8
            && WidgetInput.Mouse.Y >= Row0 + i * RowPitch - 16 && WidgetInput.Mouse.Y < Row0 + i * RowPitch + 20;

        void Key(int i, Color swatch, string cap, string value, string sub)
        {
            float b1 = Row0 + i * RowPitch, b2 = b1 + 17;
            using (var sb = new SolidBrush(Mul(swatch, a)))
                g.FillEllipse(sb, KeyX - 20, b1 - 9, 9, 9);
            using (var cb = new SolidBrush(Mul(Dim, a * 0.85f)))
                Text(g, cap, keyCap, cb, KeyX, b1);
            using (var vb = new SolidBrush(Mul(White, a)))
                Text(g, value, keyVal, vb, KeyValX, b1);
            if (sub.Length > 0)
                using (var ub = new SolidBrush(Mul(Dim, a * 0.8f)))
                    TextClipped(g, sub, keySub, ub, KeyX, b2, ColR - KeyX - 12);
        }

        if (Limits.FiveHour >= 0)
        {
            string sub = KeyHover(0) ? $"resets {Limits.FiveHourReset.ToLocalTime():ddd HH:mm}"
                                     : $"{ResetIn(Limits.FiveHourReset)} left";
            // credits ride the 5-hour row: the spend normally, the remaining on hover IF the API exposes it.
            // Promotional balance on claude.ai is NOT returned to the Claude Code token, so hover falls back.
            if (Limits.CreditsUsed > 0)
                sub += KeyHover(0)
                    ? (Limits.CreditsBalance >= 0 ? $"  ·  ${Limits.CreditsBalance:0.00} left"
                       : Limits.CreditsLimit > 0 ? $"  ·  ${Math.Max(0, Limits.CreditsLimit - Limits.CreditsUsed):0.00} of ${Limits.CreditsLimit:0}"
                       : $"  ·  ${Limits.CreditsUsed:0.00} used")
                    : $"  ·  ${Limits.CreditsUsed:0.00}";
            Key(0, UsageColor(Limits.FiveHour), "5-hour",
                KeyHover(0) ? $"{Limits.FiveHour * 100:0.#}%" : Pct(Limits.FiveHour), sub);
        }
        else Key(0, Dim, "5-hour", "\u2014", "not fetched yet");

        if (Limits.Week >= 0)
            Key(1, UsageColor(Limits.Week), "weekly",
                KeyHover(1) ? $"{Limits.Week * 100:0.#}%" : Pct(Limits.Week),
                KeyHover(1) ? $"resets {Limits.WeekReset.ToLocalTime():ddd HH:mm}"
                            : $"{ResetIn(Limits.WeekReset)} left");
        else Key(1, Dim, "weekly", "\u2014", "not fetched yet");

        if (st?.Session is { } sess)
        {
            long maxK = sess.ContextMax / 1000, usedK = Math.Min(sess.ContextUsed / 1000, maxK);
            string maxLabel = maxK >= 1000 ? $"{maxK / 1000f:0.#}M" : $"{maxK}K";
            Key(2, Blue, "context", $"{usedK}K", $"of {maxLabel}  ·  {ctxFrac * 100:0}% used");
        }
        else Key(2, Dim, "context", "\u2014", "no active session");

        // ---- right column, all three blocks on the same left edge and the same right edge
        DrawNet(g, ColR, 74, RightEdge - ColR, 46, a);
        DrawExit(g, a, keySub, keyCap);

        var rr = RefreshRect(w, h);
        bool rHover = WidgetInput.Over && rr.Contains(WidgetInput.Mouse);
        string age = Limits.LastSuccess == DateTime.MinValue ? "usage never fetched"
            : $"updated {AgeText(DateTime.UtcNow - Limits.LastSuccess)}";
        using (var rb = new SolidBrush(Mul(rHover ? White : Dim, a)))
        using (var rsf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString($"{age}  ·  \u27f3 refresh", keySub, rb, rr, rsf);

        DrawNetHover(g, a); // last: the exit block used to be painted over the top of it
    }

    // The flag used to be the whole story: a country and nothing else. What you actually want to know
    // about an exit is whose network it is, and - when the API is routed through a proxy - whether the
    // tool is leaving by the same door as everything else. Both are measured; the second line only
    // appears when the two exits genuinely differ, so it stays quiet the rest of the time.
    internal static RectangleF ExitRect() => new(ColR, 132, RightEdge - ColR, 56);

    private static void DrawExit(Graphics g, float a, Font body, Font cap)
    {
        const float y = 152f, fw = 26f, fh = 17f;
        bool hov = WidgetInput.Over && ExitRect().Contains(WidgetInput.Mouse);
        var flag = IpCountry.Flag;
        if (flag != null)
        {
            var old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.85f * a });
            g.DrawImage(flag, Rectangle.Round(new RectangleF(ColR, y - fh + 3, fw, fh)),
                        0, 0, flag.Width, flag.Height, GraphicsUnit.Pixel, ia);
            g.InterpolationMode = old;
            using var bd = new Pen(Mul(Track, a), 1f);
            g.DrawRectangle(bd, ColR, y - fh + 3, fw, fh);
        }

        string who = IpCountry.Cc is { Length: > 0 } cc
            ? (IpCountry.Isp is { Length: > 0 } isp ? $"{cc}  ·  {isp}" : cc)
            : "locating…";
        using (var wb = new SolidBrush(Mul(White, a * 0.9f)))
        {
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(who, body, wb, new RectangleF(ColR + fw + 9, TextTop(body, y),
                RightEdge - ColR - fw - 9, body.Size * 1.6f), sf);
        }

        // Resting: the address. Hovering: what is actually known about the route — its ASN, and whether
        // Claude's own traffic leaves by this exit or another one, with the loss this route is measuring.
        // No score: ipwho.is does not sell one on the free tier and inventing a number is not on.
        string second, third = "";
        Color secondCol = Dim;
        if (IpCountry.Split)
        {
            second = $"api exits {IpCountry.ApiCc ?? "?"}  ·  {IpCountry.ApiIp}";
            secondCol = Amber;
            if (hov) third = IpCountry.Ip is { Length: > 0 } mine ? $"everything else: {mine}" : "";
        }
        else if (hov)
        {
            second = IpCountry.Asn is { Length: > 0 } asn
                ? $"{asn}  ·  {RouteQuality()}" : RouteQuality();
            third = NetMon.ProxyUrl is { Length: > 0 }
                ? "api takes the same exit" : "no proxy set · direct";
        }
        else second = IpCountry.Ip ?? "";

        using var sf2 = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        if (second.Length > 0)
            using (var sb2 = new SolidBrush(Mul(secondCol, a * 0.85f)))
                g.DrawString(second, cap, sb2, new RectangleF(ColR, TextTop(cap, y + 19),
                    RightEdge - ColR, cap.Size * 1.6f), sf2);
        if (third.Length > 0)
            using (var sb3 = new SolidBrush(Mul(Dim, a * 0.7f)))
                g.DrawString(third, cap, sb3, new RectangleF(ColR, TextTop(cap, y + 36),
                    RightEdge - ColR, cap.Size * 1.6f), sf2);
    }

    // the only honest quality figure available: what this route is measuring right now
    private static string RouteQuality()
    {
        var (net, api) = NetMon.Snapshot();
        int lost = 0, seen = 0;
        foreach (var v in api) { if (v == NetMon.Empty) continue; seen++; if (v == NetMon.Lost) lost++; }
        int last = LastSample(api);
        string ms = last == NetMon.Empty ? "…" : last == NetMon.Lost ? "dropped" : $"{last} ms";
        return seen == 0 ? ms : $"{ms}  ·  {lost}/{seen} lost";
    }

    // One element doing two jobs, in one slot so the title never shifts: while a prompt can be
    // interrupted it is the red stop button, and the rest of the time it is the status lamp in whatever
    // colour the state is (white when idle). Drawing a button when there is nothing to cancel would be
    // faking an affordance, so the idle form has no ring and no square - just the lamp.
    private void DrawCancel(Graphics g, int w, int h, float a, Color state)
    {
        var r = CancelRect(w, h);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!CanCancel)
        {
            const float d = 15f;
            using var glow = new SolidBrush(Mul(Color.FromArgb(38, state), a));
            g.FillEllipse(glow, r.X + (r.Width - d * 1.9f) / 2, r.Y + (r.Height - d * 1.9f) / 2, d * 1.9f, d * 1.9f);
            using var lamp = new SolidBrush(Mul(state, a));
            g.FillEllipse(lamp, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            return;
        }
        using (var b = new SolidBrush(Mul(Color.FromArgb(46, Red), a)))
            g.FillEllipse(b, r.X, r.Y, r.Width, r.Height);
        using (var pen = new Pen(Mul(Red, a), 1.4f))
            g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
        float sq = r.Width * 0.34f;
        using (var sb = new SolidBrush(Mul(Red, a)))
        using (var sp = Rounded(new RectangleF(r.X + (r.Width - sq) / 2, r.Y + (r.Height - sq) / 2, sq, sq), 2f))
            g.FillPath(sb, sp);
    }

    // connection-to-Anthropic graph: green = your internet (GET google.com/generate_204), blue = path to
    // api.anthropic.com. Lost stretches turn red on that line — so you can tell whose fault it is.
    // Two series stacked on one axis fight each other however they are drawn - as lines they crossed, as
    // filled areas they hid each other. So they are mirrored instead: your internet grows upward from a
    // centre rule, the path to Anthropic grows downward, one bar per sample. Nothing ever overlaps, the
    // shared scale keeps them comparable, and a dropped sample is a full-height bar in red on whichever
    // side lost it - which is the question this graph exists to answer: whose fault is it.
    // what the hover needs, stashed by DrawNet so the tooltip can be painted after everything else
    private (int[] net, int[] api, float x0, float slot, float mid, float half, float right, int cap)? _hover;

    private void DrawNet(Graphics g, float colX, float topY, float colW, float colH, float a)
    {
        var (net, api) = NetMon.Snapshot();
        int n = net.Length;

        bool hasData = false;
        foreach (var v in net) if (v != NetMon.Empty) { hasData = true; break; }
        if (!hasData) foreach (var v in api) if (v != NetMon.Empty) { hasData = true; break; }

        float mid = topY + colH / 2f, half = colH / 2f - 1f;
        float slot = colW / n, barW = Math.Max(2.5f, slot - 2f);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var rule = new Pen(Mul(Dim, a * (hasData ? 0.32f : 0.18f)), 1f))
            g.DrawLine(rule, colX, mid, colX + colW, mid);

        _hover = null;
        if (!hasData)
        {
            using var wf = new Font("Segoe UI", 12.5f, GraphicsUnit.Pixel);
            using var wb = new SolidBrush(Mul(Dim, a * 0.7f));
            using var wsf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("sampling…", wf, wb, new RectangleF(colX, topY, colW, colH), wsf);
            return;
        }

        // Scale off three times the MEDIAN, not the maximum and not a high percentile. A cold TLS handshake
        // costs ~1450ms against a steady ~85, and scaling to either the max or p90 let that handful of
        // spikes flatten every honest sample into a stub - measured: scale 550, steady bars 3px. The median
        // ignores them, so typical latency sits around a third of the height and a spike clips at full
        // height, which is what a spike should look like anyway.
        var seen = new List<int>();
        foreach (var v in net) if (v >= 0) seen.Add(v);
        foreach (var v in api) if (v >= 0) seen.Add(v);
        int cap = 150;
        if (seen.Count > 0)
        {
            seen.Sort();
            cap = Math.Max(cap, seen[seen.Count / 2] * 3);
        }
        cap = (cap + 49) / 50 * 50;

        void Side(int[] series, Color col, bool up)
        {
            for (int i = 0; i < series.Length; i++)
            {
                int v = series[i];
                if (v == NetMon.Empty) continue;
                bool lost = v == NetMon.Lost;
                float mag = lost ? half : Math.Max(2f, half * Math.Clamp(v / (float)cap, 0.03f, 1f));
                float x = colX + i * slot + (slot - barW) / 2f;
                var rect = up ? new RectangleF(x, mid - mag, barW, mag)
                              : new RectangleF(x, mid + 1, barW, mag);
                using var b = new SolidBrush(Mul(lost ? Red : col, a * (lost ? 0.95f : 0.8f)));
                using var path = Rounded(rect, barW / 2f);
                g.FillPath(b, path);
            }
        }
        Side(net, Green, up: true);
        Side(api, Blue, up: false);

        // legend carries the scale, since a mirrored profile has no axis to hang numbers off
        int lastN = LastSample(net), lastA = LastSample(api);
        string tn = Fx.NetLabel + " " + (lastN == NetMon.Empty ? "…" : lastN == NetMon.Lost ? ":(" : lastN.ToString());
        string ta = Fx.ApiLabel + " " + (lastA == NetMon.Empty ? "…" : lastA == NetMon.Lost ? ":(" : lastA.ToString());
        using (var f = new Font("Segoe UI", 12.5f, GraphicsUnit.Pixel))
        {
            float bl = topY - 8;
            using (var b = new SolidBrush(Mul(lastN == NetMon.Lost ? Red : Green, a)))
                Text(g, tn, f, b, colX, bl);
            float wN = g.MeasureString(tn, f, PointF.Empty, StringFormat.GenericTypographic).Width;
            using (var b = new SolidBrush(Mul(Dim, a * 0.7f)))
                Text(g, "·", f, b, colX + wN + 6, bl);
            using (var b = new SolidBrush(Mul(lastA == NetMon.Lost ? Red : Blue, a)))
                Text(g, ta, f, b, colX + wN + 18, bl);
            using (var b = new SolidBrush(Mul(Dim, a * 0.7f)))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far })
                g.DrawString($"ms · scale {cap}", f, b,
                    new RectangleF(colX, TextTop(f, bl), colW, f.Size * 1.6f), sf);
        }

        _hover = (net, api, colX, slot, mid, half, colX + colW, cap);
    }

    // the tooltip is the precise read: both values, how many samples each side lost, and whose fault a
    // drop was - the one thing the shape alone cannot say
    private void DrawNetHover(Graphics g, float a)
    {
        if (_hover is not { } hv) return;
        var (net, api, x0, slot, mid, half, right, cap) = hv;
        var m = WidgetInput.Mouse;
        if (!WidgetInput.Over || m.X < x0 || m.X > right || m.Y < mid - half - 10 || m.Y > mid + half + 10)
            return;
        int idx = Math.Clamp((int)((m.X - x0) / slot), 0, net.Length - 1);
        int vN = net[idx], vA = api[idx];
        if (vN == NetMon.Empty && vA == NetMon.Empty) return;

        float gx = x0 + idx * slot + slot / 2f;
        using (var guide = new Pen(Mul(White, a * 0.30f), 1f) { DashStyle = DashStyle.Dot })
            g.DrawLine(guide, gx, mid - half, gx, mid + half);

        int lostN = 0, cntN = 0, lostA = 0, cntA = 0;
        for (int i = 0; i < net.Length; i++)
        {
            if (net[i] != NetMon.Empty) { cntN++; if (net[i] == NetMon.Lost) lostN++; }
            if (api[i] != NetMon.Empty) { cntA++; if (api[i] == NetMon.Lost) lostA++; }
        }
        string F(int v) => v == NetMon.Lost ? ":(" : v == NetMon.Empty ? "–" : $"{v} ms";
        var lines = new List<(string t, Color c)>
        {
            ($"{Fx.NetLabel} {F(vN)}   {Fx.ApiLabel} {F(vA)}", White),
            ($"{Fx.LossLabel}  {Fx.NetLabel} {lostN}/{cntN}  ·  {Fx.ApiLabel} {lostA}/{cntA}", Dim),
            ("google.com  ·  api.anthropic.com", Dim),
        };
        if (vA == NetMon.Lost && vN >= 0) lines.Add(("Anthropic's side :(", Amber));
        else if (vN == NetMon.Lost) lines.Add(("your internet :(", Red));

        using var f2 = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        float bw2 = 0;
        foreach (var l in lines) bw2 = Math.Max(bw2, g.MeasureString(l.t, f2).Width);
        bw2 += 16;
        float bh2 = lines.Count * 15 + 10;
        float bx = Math.Clamp(gx - bw2 / 2f, Pad, right - bw2);
        float by = mid + half + 8;
        if (by + bh2 > 214) by = mid - half - bh2 - 8;   // no room below: hang it above the profile
        using (var path = Rounded(new RectangleF(bx, by, bw2, bh2), 7))
        {
            using (var bg = new SolidBrush(Mul(Color.FromArgb(255, 16, 16, 18), a))) g.FillPath(bg, path);
            using (var pen = new Pen(Mul(Track, a), 1f)) g.DrawPath(pen, path);
        }
        for (int i = 0; i < lines.Count; i++)
            using (var b = new SolidBrush(Mul(lines[i].c, a)))
                g.DrawString(lines[i].t, f2, b, bx + 8, by + 5 + i * 15);
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
            ($"{Fx.NetLabel} {F(vN)}   {Fx.ApiLabel} {F(vA)}", White),
            ($"{Fx.LossLabel}  {Fx.NetLabel} {lostN}/{cntN}  ·  {Fx.ApiLabel} {lostA}/{cntA}", Dim),
            ("google.com  ·  api.anthropic.com", Dim),
        };
        if (vA == NetMon.Lost && vN >= 0) lines.Add(("Anthropic's side :(", Amber));
        else if (vN == NetMon.Lost) lines.Add(("your internet :(", Red));

        using var f2 = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
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

    // in front of the title, where the lamp used to be
    private static RectangleF CancelRect(int w, int h) => new(Pad - 4, 16, 34, 34);

    // bottom-right of the band, right-aligned to the panel's padding
    private static RectangleF RefreshRect(int w, int h) => new(RightEdge - 210, 22, 210, 20);

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

    // ring mirrors the CLI spinner's colours, except its normal orange → green (orange = icon colour,
    // it would vanish): green = running a tool, yellow = thinking / needs input, blue = compacting,
    // red = error, white = idle
    // The 5-hour window is the number that matters minute to minute; the weekly one stands in when the
    // 5-hour figure hasn't been fetched. 0 when neither is known, which draws nothing rather than
    // implying an empty budget.
    private static float UsageFrac()
        => Limits.FiveHour >= 0 ? Limits.FiveHour : Limits.Week >= 0 ? Limits.Week : 0f;

    private static Color RingColor(CcStatus? st)
        => NetMon.ApiDown || NetMon.NetDown ? Red
         : LimitHit ? White                 // out of juice: nothing can run, so the ring reads idle. Amber implied
                                            // activity and left the pill looking busy while it was waiting on a reset.
         : st?.State == "waiting_input" ? Amber
         : Compacting(st) ? Blue
         : st?.State == "working" ? (string.IsNullOrEmpty(st.CurrentTool) ? Amber : Green)
         : White;

    private static string Pct(float f) => $"{(int)Math.Round(f * 100)}%";

    private static Color LerpC(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    // Green while there is plenty left, into amber, then red — rotated around HUE, not lerped through RGB.
    // Two reasons. Component-wise, (91,157,255)→(255,176,32) averages to (163,165,157), so a bar at 61%
    // came out at 0.05 saturation — grey, which reads as disabled on a meter whose job is "warming up".
    // And the ramp used to START at blue, which is also the context colour: with both drawn as concentric
    // arcs, any usage under 50% made the outer and inner rings identical. Blue now belongs to context alone.
    // the ramp is a design rule with a test on it, so it needs one seam out to the test assembly
    internal static Color UsageColorForTest(float f) => UsageColor(f);

    private static Color UsageColor(float f) =>
        f <= 0.5f ? Green
        : f <= 0.75f ? HueLerp(Green, Amber, (f - 0.5f) / 0.25f)
        : HueLerp(Amber, Red, Math.Clamp((f - 0.75f) / 0.25f, 0f, 1f));

    // interpolates in HSV along the shorter way round the wheel
    private static Color HueLerp(Color a, Color b, float t)
    {
        var (h1, s1, v1) = ToHsv(a);
        var (h2, s2, v2) = ToHsv(b);
        float dh = h2 - h1;
        if (dh > 180f) dh -= 360f;
        else if (dh < -180f) dh += 360f;
        return FromHsv((h1 + dh * t + 360f) % 360f, s1 + (s2 - s1) * t, v1 + (v2 - v1) * t);
    }

    private static (float h, float s, float v) ToHsv(Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        float h = d == 0 ? 0
            : max == r ? 60f * (((g - b) / d + 6f) % 6f)
            : max == g ? 60f * ((b - r) / d + 2f)
            : 60f * ((r - g) / d + 4f);
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(float h, float s, float v)
    {
        float c = v * s, x = c * (1 - Math.Abs(h / 60f % 2 - 1)), m = v - c;
        (float r, float g, float b) p = h < 60 ? (c, x, 0) : h < 120 ? (x, c, 0) : h < 180 ? (0, c, x)
            : h < 240 ? (0, x, c) : h < 300 ? (x, 0, c) : (c, 0, x);
        return Color.FromArgb((int)Math.Round((p.r + m) * 255), (int)Math.Round((p.g + m) * 255),
                              (int)Math.Round((p.b + m) * 255));
    }

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
        string verb = OutageText() ?? (LimitHit ? "outta juice :(" : st?.State switch
        {
            "working" => ToolVerb(st.CurrentTool),
            "compacting" when Compacting(st) => "compacting…",
            "waiting_input" => "your move ;)",
            _ => IdleMood(st),
        });
        if (!LimitHit && st?.State != "working" && !Compacting(st)) return verb;
        var el = LimitHit ? LimitReset() : Elapsed(st);
        return el.Length > 0 ? $"{verb}  ·  {el}" : verb;
    }

    // account out of juice: the usage endpoint reports ~100%. Surface it in ANY state, not just
    // "working" — hitting the limit usually ends the turn (idle/waiting), which is exactly when the
    // old "working"-only check went dark and the user saw nothing. Shows the reset countdown instead.
    // With extra-usage credits enabled the limit ISN'T a wall (work continues on credits) — so a
    // maxed bar no longer flags "outta juice". Require CONFIRMED credit data (CreditsUsed >= 0): when
    // /usage is rate-limited the Probe fallback can't read extra_usage, and defaulting to "off" made
    // the pill scream "outta juice" while credits were actually covering the work.
    private static bool LimitHit =>
        (Limits.FiveHour >= 0.99f || Limits.Week >= 0.99f) && !Limits.ExtraUsageOn && Limits.CreditsUsed >= 0;

    private static string LimitReset()
    {
        var r = ResetIn(Limits.FiveHour >= 0.99f ? Limits.FiveHourReset : Limits.WeekReset);
        return r.Length > 0 ? "back in " + r : "";
    }

    // minimal mood line when nothing is running
    private static string IdleMood(CcStatus? st) =>
        NetMon.NetDown ? "offline :("
        : NetMon.ApiDown ? "api down :("
        : JustCompacted(st) ? "compacted :)"
        : Limits.FiveHour >= 0.95f && !Limits.ExtraUsageOn && Limits.CreditsUsed >= 0 ? "outta juice XD"
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
