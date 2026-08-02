using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Halo.Agents;
using Halo.ClaudeCode;

namespace Halo.Widgets;

internal sealed class ClaudeCodeWidget : IWidget
{
    private static readonly Color Blue = Color.FromArgb(91, 157, 255);
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);
    private static readonly Color Mint = Color.FromArgb(82, 224, 163);
    private const float MinVerbPx = 12.5f;
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
    internal static Bitmap? PlainIcon => ClaudeIcon;

    private static readonly Color Accent = Fx.AccentOf(ClaudeIcon) is var a && a != Fx.White
        ? a : Color.FromArgb(217, 119, 87);

    public string Icon => "\uE756";

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

    public bool IsActive => Live is not null;
    private CcStatus? Live => _store.SessionLive(_slot);
    public Color? Ring => Live is { } st ? RingColor(st) : null;

    public float RingProgress
        => Live is null || (Limits.FiveHour < 0 && Limits.Week < 0) ? -1f : UsageFrac();
    public int Version => _store.Version + NetMon.Version + CompactProgress.Version;
    public AgentNotice AgentNotice => Live is { } status
        ? new AgentNotice(Shown(status), ParseTime(status.CompactedAt), status.Message)
        : AgentNotice.None;
    public IEnumerable<int> OwnerPids => Live is { } st ? new[] { st.Pid, st.ConsolePid } : Array.Empty<int>();

    public bool Animating => _appear < 1f || Compacting(Live)
        || (_wasOpen && (WidgetInput.Over || RingsSettling));

    private string _shownKey = "";
    private float _appear = 1f;

    private readonly float[] _ringLift = new float[3];
    private long _ringTick;
    private bool RingsSettling
    {
        get { foreach (var v in _ringLift) if (v > 0.01f) return true; return false; }
    }

    private static Bitmap? LoadIcon()
    {
        try
        {
            using var s = typeof(ClaudeCodeWidget).Assembly.GetManifestResourceStream("Halo.Assets.claude.png");
            return s != null ? new Bitmap(s) : null;
        }
        catch { return null; }
    }

    private bool CanCancel => Live is { Pid: > 0 } st && Shown(st) == "working";

    private bool _wasOpen;

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        bool open = fade > 0.01f;
        if (open && !_wasOpen) Limits.OnPanelOpen();
        _wasOpen = open;
        if (open)
        {
            NetMon.Poke();
            Fx.Glow(g, w, h, fade, w * 0.16f, h * 0.35f, w * 0.85f, h * 1.2f, 30, Accent);
            DrawExpanded(g, w, h, fade, Live);
        }
    }

    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        var st = Live;
        float sz = (h - 16f) * 0.82f, x = 13, y = (h - sz) / 2f;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (!Compacting(st)) Fx.PillBar(g, w, h, fade, UsageFrac(), Accent, 0.3f);
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 26, Accent);
        if (Compacting(st))
        {
            float pulse = 0.5f - 0.5f * MathF.Cos(Environment.TickCount % 2400 / 2400f * MathF.Tau);
            using var pb = new SolidBrush(Mul(Blue, fade * (0.05f + 0.11f * pulse)));
            using var pp = Fx.PillPath(w, h, h / 2f);
            g.FillPath(pb, pp);
        }

        using (var pen = new Pen(Mul(RingColor(st), fade * 0.9f), 1.9f))
            g.DrawEllipse(pen, x - 2.5f, y - 2.5f, sz + 5f, sz + 5f);
        if (ClaudeIcon != null) DrawIcon(g, ClaudeIcon, x, y, sz, fade, sz / 2f);
        else
            using (var db = new SolidBrush(Mul(RingColor(st), fade)))
                g.FillEllipse(db, x, y, sz, sz);

        string el0 = LimitHit ? LimitReset() : Elapsed(st);
        if (Compacting(st) && !LimitHit && CompactPct(st!) is { Length: > 0 } done)
            el0 = el0.Length > 0 ? done + " · " + Coarse(el0) : done;
        float textX0 = x + sz + 11;
        if (st?.State == "waiting_input") textX0 += 16;
        using var elFont = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        float elW0 = el0.Length > 0
            ? g.MeasureString(el0, elFont, int.MaxValue, StringFormat.GenericTypographic).Width : 0;
        float avail0 = (w - 14) - textX0 - (elW0 > 0 ? elW0 + 10 : 0);

        int fit = fade > 0.99f ? Fx.FitChars(g, avail0, MinVerbPx) : 0;
        var mood = Mood(st) with { MaxChars = fit >= 8 ? fit : 0 };
        string verb = OutageText() ?? (LimitHit ? "outta juice :(" : Shown(st) switch
        {
            "working" => ToolVerb(Glow(st).Tool, mood),
            "compacting" when Compacting(st) => Moods.Line("compacting", mood),
            "waiting_input" => "your move ;)",
            _ => IdleMood(st, mood),
        });
        string el = el0;
        if (verb != _shownKey) { _shownKey = verb; _appear = 0f; }
        else if (_appear < 1f) _appear = Math.Min(1f, _appear + 0.1f);
        float e = 1f - MathF.Pow(1f - _appear, 3);
        bool busy = Shown(st) == "working" || Compacting(st) || LimitHit;
        bool centred = !busy && st?.State != "waiting_input";

        float textX = x + sz + 11;
        if (st?.State == "waiting_input") textX += 16;
        using var tf2 = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        float elW = el.Length > 0 ? g.MeasureString(el, tf2, int.MaxValue, StringFormat.GenericTypographic).Width : 0;
        float avail = (w - 14) - textX - (elW > 0 ? elW + 10 : 0);

        float px = 15f;
        using (var fm = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel))
        {
            var m0 = g.MeasureString(verb, fm, int.MaxValue, StringFormat.GenericTypographic);

            if (m0.Width > avail && m0.Width > 0) px = Math.Max(MinVerbPx, px * avail / m0.Width);
        }
        using var f = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, fade * e));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = centred ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,

            Trimming = StringTrimming.EllipsisCharacter,
        };

        float originX = textX - 16f * (1f - e);
        float rightEdge = textX + avail;
        var clip = g.Clip;
        g.SetClip(new RectangleF(x + sz + 2, 0, rightEdge - (x + sz + 2), h));

        float zoneW = (centred ? rightEdge - 34f : rightEdge) - originX;

        g.DrawString(verb, f, b, new RectangleF(originX, -Fx.CenterLift(f), zoneW, h), sf);
        g.Clip = clip;

        if (elW > 0)
            using (var eb = new SolidBrush(Mul(Dim, fade * e)))
            using (var esf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(el, tf2, eb, new RectangleF(w - 14 - elW - 4, -Fx.CenterLift(tf2), elW + 4, h), esf);

    }

    private static string? _cancelledCompactKey;

    public static void MarkCompactCancelled(string? startedAt) => _cancelledCompactKey = startedAt;

    private static string? _cancelledTurnKey;

    public static void MarkTurnCancelled(string? startedAt) => _cancelledTurnKey = startedAt;

    internal const int SettleAfterSeconds = 180;

        internal static bool TurnOver(CcStatus? st, DateTimeOffset now)
    {
        if (st is not { State: "working" }) return false;
        if (st.StartedAt is { Length: > 0 } && st.StartedAt == _cancelledTurnKey) return true;
        if (!string.IsNullOrEmpty(st.CurrentTool)) return false;
        return ParseTime(st.UpdatedAt) is { } u && now - u > TimeSpan.FromSeconds(SettleAfterSeconds);
    }

    private static string? Shown(CcStatus? st) =>
        TurnOver(st, DateTimeOffset.UtcNow) ? "idle" : st?.State;

    internal static bool Compacting(CcStatus? st) =>
        st?.State == "compacting" && st.StartedAt != _cancelledCompactKey
        && ParseTime(st.StartedAt) is { } t
        && DateTimeOffset.UtcNow - t < TimeSpan.FromMinutes(3);

    internal static string CompactPct(CcStatus st)
        => CompactProgress.Caption();

    internal static string Coarse(string elapsed)
    {
        int m = elapsed.IndexOf('m');
        return m > 0 ? elapsed[..(m + 1)] : elapsed;
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

    internal static float ContextWarnAt
        => Halo.Settings.SettingsStore.Percent("alert.contextAt", 80) / 100f;

    internal static int ContextBand(float frac)
        => frac >= ContextWarnAt ? 2 : frac >= ContextWarnAt - 0.15f ? 1 : 0;

    internal static Color ContextColour(double frac)
        => frac < 0 ? Blue : ContextBand((float)frac) switch { 2 => Red, 1 => Amber, _ => Blue };

    internal (string? id, float frac) ContextState()
    {
        var st = Live;
        if (st?.Session is not { ContextMax: > 0 } ses) return (null, -1f);
        var id = st.Pid + ":" + st.StartedAt;
        return (id, (float)Math.Clamp((double)ses.ContextUsed / ses.ContextMax, 0, 1));
    }

    private const int Pad = 22;
    private const float ColR = 356f, RightEdge = 538f;
    private const float RingCx = 84f, RingCy = 132f, RingOuter = 52f, RingBand = 8f, RingStep = 16f;
    private const float KeyX = 178f, KeyValX = 268f;
    private const float Row0 = 96f, RowPitch = 42f;

    private static float TextTop(Font f, float baseline)
        => MathF.Round(baseline - f.FontFamily.GetCellAscent(f.Style) / (float)f.FontFamily.GetEmHeight(f.Style) * f.Size);

    private static void Text(Graphics g, string t, Font f, Brush b, float x, float baseline)
        => g.DrawString(t, f, b, MathF.Round(x), TextTop(f, baseline), StringFormat.GenericTypographic);

    private static readonly StringFormat AdvanceFmt =
        new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };

    private static float Advance(Graphics g, string t, Font f)
        => t.Length == 0 ? 0f : g.MeasureString(t, f, System.Drawing.Point.Empty, AdvanceFmt).Width;

    private static void TextClipped(Graphics g, string t, Font f, Brush b, float x, float baseline, float w)
    {
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(t, f, b, new RectangleF(MathF.Round(x), TextTop(f, baseline), w, f.Size * 1.6f), sf);
    }

    private void DrawExpanded(Graphics g, int w, int h, float a, CcStatus? st)
    {
        using var title = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var line = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var keyCap = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        using var keyVal = new Font("Segoe UI Semibold", 16f, GraphicsUnit.Pixel);

        using var keySub = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var state = RingColor(st);

        DrawCancel(g, w, h, a, state);
        using (var tb = new SolidBrush(Mul(White, a)))
            Text(g, "Claude Code", title, tb, 84, 40);

        if (st?.State == "waiting_input" && !string.IsNullOrEmpty(st.Message))
            using (var ab = new SolidBrush(Mul(Amber, a)))
                TextClipped(g, st.Message!, line, ab, 84, 62, ColR - 92);

        double ctxFrac = st?.Session is { ContextMax: > 0 } ? ContextFrac(st) : -1;

        var ctxCol = ContextColour(ctxFrac);
        var rings = new (float frac, Color col)[]
        {
            (Limits.FiveHour, Limits.FiveHour >= 0 ? UsageColor(Limits.FiveHour) : Dim),
            (Limits.Week,     Limits.Week     >= 0 ? UsageColor(Limits.Week)     : Dim),
            ((float)ctxFrac,  ctxCol),
        };

        int hotRing = -1;
        if (WidgetInput.Over)
        {
            float dx = WidgetInput.Mouse.X - RingCx, dy = WidgetInput.Mouse.Y - RingCy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            for (int i = 0; i < rings.Length; i++)
                if (MathF.Abs(dist - (RingOuter - i * RingStep)) <= RingBand / 2f + 3f) { hotRing = i; break; }
        }

        long ringNow = Environment.TickCount64;
        float rdt = _ringTick == 0 ? 1f / 60f : Math.Clamp((ringNow - _ringTick) / 1000f, 0.001f, 0.1f);
        _ringTick = ringNow;
        for (int i = 0; i < _ringLift.Length; i++)
            _ringLift[i] += ((hotRing == i ? 1f : 0f) - _ringLift[i]) * (1f - MathF.Exp(-rdt / 0.09f));

        for (int i = 0; i < rings.Length; i++)
        {
            float lift = _ringLift[i];
            float r = RingOuter - i * RingStep;
            float band = RingBand + 3.2f * lift;
            using (var track = new Pen(Mul(Track, a * (1f + 0.5f * lift)), band))
                g.DrawArc(track, RingCx - r, RingCy - r, r * 2, r * 2, 0, 360);

            if (rings[i].frac < 0) continue;
            float sweep = Math.Clamp(rings[i].frac, 0f, 1f) * 360f;
            if (sweep <= 0.5f) continue;

            float other = hotRing >= 0 ? 1f - 0.35f * (1f - lift) : 1f;
            using var arc = new Pen(Mul(rings[i].col, a * other), band) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, RingCx - r, RingCy - r, r * 2, r * 2, -90f, sweep);
        }

        float show = 0f;
        int shown = -1;
        for (int i = 0; i < _ringLift.Length; i++)
            if (_ringLift[i] > show) { show = _ringLift[i]; shown = i; }
        if (shown >= 0 && show > 0.01f)
        {
            var (rf, rc) = rings[shown];
            string big = rf < 0 ? "\u2014" : $"{Math.Clamp(rf, 0f, 1f) * 100:0}%";
            string cap2 = shown switch
            {
                0 => Limits.FiveHour < 0 ? "5-hour  \u00b7  not fetched"
                    : Limits.CreditsUsed > 0 ? $"5-hour  \u00b7  {ResetIn(Limits.FiveHourReset)} left  \u00b7  ${Limits.CreditsUsed:0.00}"
                    : $"5-hour  \u00b7  {ResetIn(Limits.FiveHourReset)} left",
                1 => Limits.Week >= 0 ? $"weekly  \u00b7  {ResetIn(Limits.WeekReset)} left" : "weekly  \u00b7  not fetched",
                _ => st?.Session is { ContextMax: > 0 } ses
                    ? $"context  \u00b7  {ses.ContextUsed / 1000}K of {ses.ContextMax / 1000}K" : "context  \u00b7  no session",
            };

            float hole = RingOuter - 2 * RingStep - RingBand / 2f - 2f;
            Font centreF = new("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
            foreach (float px in new[] { 15f, 14f, 13f, 12f, 11f, 10f, 9f })
            {
                var probe = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
                float half = probe.Height / 2f;
                float chord = 2f * MathF.Sqrt(MathF.Max(1f, hole * hole - half * half));
                if (Advance(g, big, probe) <= chord || px <= 9f) { centreF.Dispose(); centreF = probe; break; }
                probe.Dispose();
            }
            using var _centreF = centreF;
            using var underF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
            using var mid = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            using (var cb = new SolidBrush(Mul(rf < 0 ? Dim : rc, a * show)))
                g.DrawString(big, centreF, cb, new RectangleF(RingCx - 30, RingCy - 11, 60, 22), mid);
            using (var ub = new SolidBrush(Mul(Dim, a * show * 0.95f)))

                g.DrawString(cap2, underF, ub,
                    new RectangleF(RingCx - 74, RingCy + RingOuter + RingBand / 2f + 7f, 148, 16), mid);
        }

        bool KeyHover(int i) => WidgetInput.Over
            && WidgetInput.Mouse.X >= KeyX - 20 && WidgetInput.Mouse.X < ColR - 8
            && WidgetInput.Mouse.Y >= Row0 + i * RowPitch - 16 && WidgetInput.Mouse.Y < Row0 + i * RowPitch + 20;

        void Key(int i, Color swatch, string cap, string value, string sub, Color? figure = null,
                 string? hot = null)
        {
            float b1 = Row0 + i * RowPitch, b2 = b1 + 17;
            using (var sb = new SolidBrush(Mul(swatch, a)))
                g.FillEllipse(sb, KeyX - 20, b1 - 9, 9, 9);
            using (var cb = new SolidBrush(Mul(Dim, a * 0.85f)))
                Text(g, cap, keyCap, cb, KeyX, b1);
            using (var vb = new SolidBrush(Mul(figure ?? White, a)))
                Text(g, value, keyVal, vb, KeyValX, b1);
            if (sub.Length == 0) return;
            int cut = hot is { Length: > 0 } ? sub.IndexOf(hot, StringComparison.Ordinal) : -1;
            if (cut < 0)
            {
                using var ub = new SolidBrush(Mul(Dim, a * 0.8f));
                TextClipped(g, sub, keySub, ub, KeyX, b2, ColR - KeyX - 12);
                return;
            }
            using (var ub = new SolidBrush(Mul(Dim, a * 0.8f)))
            using (var hb = new SolidBrush(Mul(figure ?? White, a * 0.95f)))
            {
                string pre = sub.Substring(0, cut), post = sub.Substring(cut + hot!.Length);
                float x = KeyX;
                Text(g, pre, keySub, ub, x, b2);
                x += Advance(g, pre, keySub);
                Text(g, hot!, keySub, hb, x, b2);
                x += Advance(g, hot!, keySub);
                Text(g, post, keySub, ub, x, b2);
            }
        }

        int slot = 0;

        if (Limits.FiveHour >= 0)
        {
            int s = slot++;
            string sub = KeyHover(s) ? $"resets {Limits.FiveHourReset.ToLocalTime():ddd HH:mm}"
                                     : $"{ResetIn(Limits.FiveHourReset)} left";

            if (Limits.CreditsUsed > 0 && KeyHover(s))
                sub += Limits.CreditsBalance >= 0 ? $"  ·  ${Limits.CreditsBalance:0.00} left"
                     : Limits.CreditsLimit > 0 ? $"  ·  ${Math.Max(0, Limits.CreditsLimit - Limits.CreditsUsed):0.00} of ${Limits.CreditsLimit:0}"
                     : $"  ·  ${Limits.CreditsUsed:0.00} used";
            Key(s, UsageColor(Limits.FiveHour), "5-hour",
                KeyHover(s) ? $"{Limits.FiveHour * 100:0.#}%" : Pct(Limits.FiveHour), sub,
                UsageColor(Limits.FiveHour));
        }

        else Key(slot++, Dim, "5-hour", "\u2014", "");

        if (Limits.Week >= 0)
        {
            int s = slot++;
            Key(s, UsageColor(Limits.Week), "weekly",
                KeyHover(s) ? $"{Limits.Week * 100:0.#}%" : Pct(Limits.Week),
                KeyHover(s) ? $"resets {Limits.WeekReset.ToLocalTime():ddd HH:mm}"
                            : $"{ResetIn(Limits.WeekReset)} left",
                UsageColor(Limits.Week));
        }

        if (st?.Session is { } sess)
        {
            long maxK = sess.ContextMax / 1000, usedK = Math.Min(sess.ContextUsed / 1000, maxK);
            string maxLabel = maxK >= 1000 ? $"{maxK / 1000f:0.#}M" : $"{maxK}K";
            Key(slot, ctxCol, "context", $"{usedK}K", $"of {maxLabel}  ·  {ctxFrac * 100:0}% used", ctxCol,
                $"{ctxFrac * 100:0}%");
        }
        else Key(slot, Dim, "context", "\u2014", "no active session");

        DrawNet(g, ColR, 74, RightEdge - ColR, 38, a);
        ExitBlock.Draw(g, a, keySub, keyCap, ColR, RightEdge,
            NetMon.Snapshot().api, NetMon.Empty, NetMon.Lost);

        var rr = RefreshRect(w, h);
        bool rHover = WidgetInput.Over && rr.Contains(WidgetInput.Mouse);
        using (var rb = new SolidBrush(Mul(rHover ? White : Dim, a * (rHover ? 1f : 0.65f))))
        using (var rsf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
        {
            string label = rHover
                ? (Limits.LastSuccess == DateTime.MinValue ? "never fetched  ·  \u27f3 refresh"
                   : $"updated {AgeText(DateTime.UtcNow - Limits.LastSuccess)}  ·  \u27f3 refresh")
                : "\u27f3 refresh";
            g.DrawString(label, keySub, rb, rr, rsf);
        }

        DrawNetHover(g, a);
    }

    internal static RectangleF ExitRect() => ExitBlock.Rect(ColR, RightEdge);

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

    private (int[] net, int[] api, float x0, float step, int first, int count,
             float top, float bottom, float right)? _hover;

    private void DrawNet(Graphics g, float colX, float topY, float colW, float colH, float a)
    {
        var (net, api) = NetMon.Snapshot();
        int n = net.Length;

        bool hasData = false;
        foreach (var v in net) if (v != NetMon.Empty) { hasData = true; break; }
        if (!hasData) foreach (var v in api) if (v != NetMon.Empty) { hasData = true; break; }

        float mid = topY + colH / 2f, half = colH / 2f - 1f;
        float span = colW - 4f;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        _hover = null;
        if (!hasData)
        {
            using var wf = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
            using var wb = new SolidBrush(Mul(Dim, a * 0.7f));
            using var wsf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("sampling…", wf, wb, new RectangleF(colX, topY, colW, colH), wsf);
            return;
        }

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

        int first = n;
        for (int i = 0; i < n; i++)
            if (net[i] != NetMon.Empty || api[i] != NetMon.Empty) { first = i; break; }
        int count = n - first;

        float slot = count > 0 ? span / count : span;
        float X(int i) => colX + 2f + i * slot + slot / 2f;

        float Mag(int v) => v == NetMon.Lost ? half
            : v == NetMon.Empty ? 1.2f
            : Math.Max(1.6f, half * 0.94f * Math.Clamp(v / (float)cap, 0.02f, 1f));

        float Age(int i) => count < 2 ? 1f : 0.45f + 0.55f * (i / (float)(count - 1));

        void Rule(float alpha)
        {
            using var rule = new Pen(Mul(Dim, a * alpha), 1f);
            g.DrawLine(rule, colX, mid, colX + colW, mid);
        }

        void Waveform()
        {
            Rule(0.22f);

            float barW = Math.Clamp(slot - 2.2f, 2f, 5.5f);
            for (int i = 0; i < count; i++)
            {
                void Cap(int v, Color col, bool up)
                {
                    if (v == NetMon.Empty) return;
                    bool lost = v == NetMon.Lost;
                    float m = Mag(v);
                    var r = up ? new RectangleF(X(i) - barW / 2f, mid - 1.5f - m, barW, m)
                               : new RectangleF(X(i) - barW / 2f, mid + 1.5f, barW, m);
                    using var b = new SolidBrush(Mul(lost ? Red : col, a * Age(i) * (lost ? 1f : 0.92f)));
                    using var p = Rounded(r, barW / 2f);
                    g.FillPath(b, p);
                }
                Cap(net[first + i], Green, true);
                Cap(api[first + i], Blue, false);
            }
        }

        Waveform();

        int lastN = LastSample(net), lastA = LastSample(api);
        string tn = Fx.NetLabel + " " + (lastN == NetMon.Empty ? "…" : lastN == NetMon.Lost ? ":(" : lastN.ToString());
        string ta = Fx.ApiLabel + " " + (lastA == NetMon.Empty ? "…" : lastA == NetMon.Lost ? ":(" : lastA + " ms");
        using (var f = new Font("Segoe UI", 13f, GraphicsUnit.Pixel))
        {
            float bl = topY - 8;
            using (var b = new SolidBrush(Mul(lastN == NetMon.Lost ? Red : Green, a)))
                Text(g, tn, f, b, colX, bl);
            float wN = g.MeasureString(tn, f, PointF.Empty, StringFormat.GenericTypographic).Width;
            using (var b = new SolidBrush(Mul(Dim, a * 0.7f)))
                Text(g, "·", f, b, colX + wN + 6, bl);
            using (var b = new SolidBrush(Mul(lastA == NetMon.Lost ? Red : Blue, a)))
                Text(g, ta, f, b, colX + wN + 18, bl);
        }

        _hover = (net, api, colX + 2f, slot, first, count, topY, topY + colH, colX + colW);
    }

    private void DrawNetHover(Graphics g, float a)
    {
        if (_hover is not { } hv) return;
        var (net, api, x0, step, first, count, top, bottom, right) = hv;
        var m = WidgetInput.Mouse;
        if (!WidgetInput.Over || m.X < x0 || m.X > right || m.Y < top - 10 || m.Y > bottom + 10) return;
        if (count <= 0) return;

        int rel = step > 0 ? (int)((m.X - x0) / step) : 0;
        int idx = first + Math.Clamp(rel, 0, count - 1);
        int vN = net[idx], vA = api[idx];
        if (vN == NetMon.Empty && vA == NetMon.Empty) return;

        float gx = x0 + (idx - first) * step;
        using (var guide = new Pen(Mul(White, a * 0.30f), 1f) { DashStyle = DashStyle.Dot })
            g.DrawLine(guide, gx, top, gx, bottom);

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
        float by = bottom + 8;
        if (by + bh2 > 214) by = top - bh2 - 8;
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

    private static RectangleF CancelRect(int w, int h) => new(42, 16, 34, 34);

    private static RectangleF RefreshRect(int w, int h) => new(RightEdge - 210, 22, 210, 20);

    private static string AgeText(TimeSpan d) =>
        d.TotalMinutes < 1 ? "just now"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m ago"
        : d.TotalDays < 1 ? $"{(int)d.TotalHours}h ago"
        : $"{(int)d.TotalDays}d ago";

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
    {
        var list = new List<(RectangleF, Action<PointF>)>
        {
            (CancelRect(w, h), _ => { if (CanCancel) _cancel(); }),
            (RefreshRect(w, h), _ => Limits.ForceRefresh()),
        };

        if (ExitBlock.DnsRowRect != RectangleF.Empty)
            list.Add((ExitBlock.DnsRowRect, _ => DnsLeak.Retest()));
        return list;
    }

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

    private MoodContext Mood(CcStatus? st) => new(
        Running(st), (float)ContextFrac(st), UsageFrac(),
        st?.Session?.PromptTokens ?? 0, ToolRuns(st), DateTime.Now.Hour, Glow(st).Target);

    private const int AfterglowMs = 9_000;
    private string? _glowTool, _glowTarget, _glowTurn;
    private long _glowAt;

    private (string? Tool, string? Target) Glow(CcStatus? st)
    {
        var turn = st?.StartedAt;
        if (turn != _glowTurn) { _glowTurn = turn; _glowTool = _glowTarget = null; }
        if (st?.CurrentTool is { Length: > 0 } cur)
        {
            _glowTool = cur;
            _glowTarget = st.ToolTarget;
            _glowAt = Environment.TickCount64;
            return (cur, _glowTarget);
        }

        if (Shown(st) != "working") { _glowTool = _glowTarget = null; return (null, null); }
        return Environment.TickCount64 - _glowAt <= AfterglowMs ? (_glowTool, _glowTarget) : (null, null);
    }

    private string? _runsTurn;
    private string? _runsTool;
    private int _runs;

    private int ToolRuns(CcStatus? st)
    {
        var stamp = st?.StartedAt;
        if (stamp != _runsTurn) { _runsTurn = stamp; _runsTool = null; _runs = 0; }
        var tool = st?.CurrentTool;
        if (!string.IsNullOrEmpty(tool) && tool != _runsTool) { _runsTool = tool; _runs++; }
        return _runs;
    }

    private static float UsageFrac()
        => Limits.FiveHour >= 0 ? Limits.FiveHour : Limits.Week >= 0 ? Limits.Week : 0f;

    private static bool RingIsTheMessage(CcStatus? st)
        => NetMon.ApiDown || NetMon.NetDown || LimitHit || Compacting(st);

    private static Color RingBase(CcStatus? st, string? tool)
        => NetMon.ApiDown || NetMon.NetDown ? Red
         : LimitHit ? White

         : st?.State == "waiting_input" ? Fx.SlotColor("asking")
         : Compacting(st) ? Blue
         : JustCompacted(st) ? Mint
         : Shown(st) == "working" ? Fx.SlotColor(ToolSlot(tool))
         : White;

    private Color RingColor(CcStatus? st)
    {
        var tool = Glow(st).Tool;
        var b = RingBase(st, tool);
        if (RingIsTheMessage(st)) return b;

        bool hueIsFree = st?.State != "waiting_input"
            && (Shown(st) != "working" || string.IsNullOrEmpty(tool));
        return Fx.MoodRing(b, Mood(st), hueIsFree);
    }

    private static string Pct(float f) => $"{(int)Math.Round(f * 100)}%";

    private static Color LerpC(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    internal static Color UsageColorForTest(float f) => UsageColor(f);

    private static Color UsageColor(float f) => Fx.UsageColor(f);

    private static string ResetIn(DateTimeOffset r)
    {
        if (r == default) return "";
        var d = r - DateTimeOffset.UtcNow;
        if (d.TotalSeconds <= 0) return "now";
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{d.Minutes}m";
    }

    private static bool LimitHit =>
        (Limits.FiveHour >= 0.99f || Limits.Week >= 0.99f) && !Limits.ExtraUsageOn && Limits.CreditsUsed >= 0;

    private static string LimitReset()
    {
        var r = ResetIn(Limits.FiveHour >= 0.99f ? Limits.FiveHourReset : Limits.WeekReset);
        return r.Length > 0 ? "back in " + r : "";
    }

    private static string? Trouble(CcStatus? st) =>
        NetMon.NetDown ? Moods.Line("offline")
        : NetMon.ApiDown ? Moods.Line("apiDown")
        : JustCompacted(st) ? Moods.Line("compacted")
        : Limits.FiveHour >= 0.95f && !Limits.ExtraUsageOn && Limits.CreditsUsed >= 0 ? Moods.Line("outOfCredit")
        : null;

    private static string IdleMood(CcStatus? st, in MoodContext ctx) => Trouble(st) ?? Moods.Line("idle", ctx);

    private static bool JustCompacted(CcStatus? st) =>
        DateTimeOffset.TryParse(st?.CompactedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
        && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(20);

    private static string? OutageText() =>
        NetMon.NetDown ? Moods.Line("netError") : NetMon.ApiDown ? Moods.Line("apiError") : null;

        internal static string? ToolSlot(string? tool) => tool switch
    {
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "writing",
        "Read" => "reading",
        "Bash" or "PowerShell" or "KillShell" => "running",
        "BashOutput" or "Monitor" => "watching",
        "Grep" or "Glob" or "ToolSearch" => "digging",
        "WebFetch" => "fetching",
        "WebSearch" => "searching",
        "Task" or "Agent" or "SendMessage" => "delegating",
        "TodoWrite" or "TaskCreate" or "TaskUpdate" or "ExitPlanMode" or "EnterPlanMode"
            or "ScheduleWakeup" or "CronCreate" => "planning",
        "SlashCommand" or "Skill" => "skill",
        "AskUserQuestion" => "asking",
        "ReportFindings" => "reviewing",
        "Artifact" or "SendUserFile" => "publishing",
        null or "" => "unknown",

        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "consulting",
        _ => null,
    };

    private static string ToolVerb(string? tool, in MoodContext ctx)
        => ToolSlot(tool) is { } slot ? Moods.Line(slot, ctx) : Moods.PrettyTool(tool);

    private static TimeSpan? Running(CcStatus? st) =>
        ParseTime(st?.StartedAt) is { } t ? DateTimeOffset.UtcNow - t : null;

    private static string Elapsed(CcStatus? st)
    {
        if ((Shown(st) != "working" && !Compacting(st)) || string.IsNullOrEmpty(st?.StartedAt)) return "";
        if (!DateTimeOffset.TryParse(st.StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return "";
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 1) return "";
        return d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.Seconds}s";
    }
}
