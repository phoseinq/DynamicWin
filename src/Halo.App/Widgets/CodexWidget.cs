using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.Codex;

using Halo.Agents;

namespace Halo.Widgets;

internal enum CodexCancelRoute { None, Cli, Desktop }

internal sealed class CodexWidget : IWidget
{
    private static readonly Color Blue = Color.FromArgb(91, 157, 255);
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);
    private static readonly Color Mint = Color.FromArgb(82, 224, 163);   // just compacted: there is room again
    private static readonly Color Track = Color.FromArgb(38, 255, 255, 255);
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);

    private readonly CodexStatusStore _store;
    private readonly CodexSurface _surface;
    private readonly Action _cancel;
    private readonly Func<bool> _canCancelDesktop;
    private readonly Action<CodexSnapshot?> _observeLimits;

    // one widget per surface: the ChatGPT desktop app and the codex CLI are separate sessions
    public CodexWidget(CodexStatusStore store, CodexSurface surface, Action cancel,
        Func<bool>? canCancelDesktop = null, Action<CodexSnapshot?>? observeLimits = null)
    {
        _store = store;
        _surface = surface;
        _cancel = cancel;
        _canCancelDesktop = canCancelDesktop ?? (static () => false);
        _observeLimits = observeLimits ?? CodexLimits.UpdateFrom;
        CodexLimits.Attach(store);
    }

    private CodexSnapshot? Current => _store.Candidate(_surface);

    private static readonly Bitmap? OpenAiIcon = LoadIcon();
    internal static Bitmap? PlainIcon => OpenAiIcon;
    public float IconOffsetX => -1.25f;
    // icon-derived accent for the background wash; the OpenAI mark is white → ChatGPT green fallback
    private static readonly Color Accent = Fx.AccentOf(OpenAiIcon) is var a && a != Fx.White
        ? a : Color.FromArgb(16, 163, 127);

    public string Icon => "\uE756"; // Segoe MDL2 CommandPrompt (fallback)

    // number badge only when both surfaces are live (1 = ChatGPT app, 2 = CLI) \u2014 same scheme as CC sessions
    private Bitmap? _badged;

    public Bitmap? IconImage
    {
        get
        {
            if (OpenAiIcon is null) return null;
            var other = _surface == CodexSurface.Desktop ? CodexSurface.Cli : CodexSurface.Desktop;
            if (_store.Candidate(other) is null) return OpenAiIcon;
            return _badged ??= Fx.Badge(OpenAiIcon, _surface == CodexSurface.Desktop ? '1' : '2');
        }
    }

    public string Id => "codex";
    public string? AgentState => Current?.State;
    // visible only while this surface actually runs (user's call); the panel still
    // shows cached limits when it's open with no task in flight
    public bool IsActive => Current is not null;
    public Color? Ring => Current is { } st ? RingColor(st) : null;

    // same as the Claude twin: colour is what it is doing, fill is how much of the window is spent.
    // UsageFrac already prefers the 5-hour window and stands in the weekly one when it is missing.
    public float RingProgress
        => Current is null || (CodexLimits.FiveHour < 0 && CodexLimits.Week < 0) ? -1f : UsageFrac();
    public int Version => _store.Version + CodexNetMon.Version + CodexLimits.Version;
    public bool IsDesktop => _surface == CodexSurface.Desktop;
    public AgentNotice AgentNotice => Current is { } status
        ? new AgentNotice(status.State, status.CompactedAt, status.Message)
        : AgentNotice.None;
    public IEnumerable<int> OwnerPids => Current is { } st ? new[] { st.Pid, st.ConsolePid } : Array.Empty<int>();
    // text-emerge animation + the compacting pulse both need frames while collapsed. RingsSettling
    // keeps them coming after the pointer leaves the panel, or the lift would freeze half-raised and
    // the next hover would start from wherever it stopped.
    public bool Animating => _appear < 1f || Compacting(Current)
        || (_wasOpen && (WidgetInput.Over || RingsSettling));

    private string _shownKey = "";
    private float _appear = 1f;

    // per-ring hover lift, eased; _ringTick is the wall clock the easing is measured against
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
            using var s = typeof(CodexWidget).Assembly.GetManifestResourceStream("Halo.Assets.openai.png");
            return s != null ? new Bitmap(s) : null;
        }
        catch { return null; }
    }

    private bool CanCancel
    {
        get
        {
            var snapshot = Current;
            var canCancelDesktop = snapshot is { Source: CodexSurface.Desktop, State: "working" } &&
                _canCancelDesktop();
            return GetCancelRoute(snapshot, canCancelDesktop) != CodexCancelRoute.None;
        }
    }

    // A turn the pill has stopped believing in cannot be cancelled either: the stop button used to stay
    // live on a turn that was already interrupted, which sends a second Esc into whatever has the
    // terminal now.
    internal static CodexCancelRoute GetCancelRoute(CodexSnapshot? snapshot, bool canCancelDesktop) =>
        TurnOver(snapshot, DateTimeOffset.UtcNow) ? CodexCancelRoute.None : snapshot switch
    {
        { Source: CodexSurface.Cli, State: "working", ConsolePid: > 0 } => CodexCancelRoute.Cli,
        { Source: CodexSurface.Desktop, State: "working" } when canCancelDesktop => CodexCancelRoute.Desktop,
        _ => CodexCancelRoute.None,
    };

    private bool _wasOpen;

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        bool open = fade > 0.01f;
        if (open && !_wasOpen) CodexLimits.ForceRefresh();
        _wasOpen = open;
        if (open)
        {
            var snapshot = Current;
            if (snapshot is not null) CodexLimits.UpdateFrom(snapshot);
            CodexNetMon.Poke();
            Halo.ClaudeCode.IpCountry.Poke(); // same exit IP → same flag as the CC panel
            Fx.Glow(g, w, h, fade, w * 0.16f, h * 0.35f, w * 0.85f, h * 1.2f, 30, Accent);
            DrawExpanded(g, w, h, fade, snapshot);
        }
    }

    // collapsed pill = OpenAI icon on the left, what it's doing on the right (Apple-style)
    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        var st = Current;
        _observeLimits(st);
        float sz = (h - 16f) * 0.82f, x = 13, y = (h - sz) / 2f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // mirrors ClaudeCodeWidget: the spent share of the usage window as a whisper-faint pill background,
        // collapsed only, and never while the compacting wash owns the pill
        if (!Compacting(st)) Fx.PillBar(g, w, h, fade, UsageFrac(), Accent, 0.3f);
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 26, Accent);
        if (Compacting(st)) // soft blue breathing across the whole pill = process running
        {
            float pulse = 0.5f - 0.5f * MathF.Cos(Environment.TickCount % 2400 / 2400f * MathF.Tau);
            using var pb = new SolidBrush(Mul(Blue, fade * (0.05f + 0.11f * pulse)));
            using var pp = Fx.PillPath(w, h, h / 2f); // flat top: matches the pill, no corner crescents
            g.FillPath(pb, pp);
        }
        // subtle status ring around the (circular) icon: green working, red on error, white otherwise
        using (var pen = new Pen(Mul(RingColor(st), fade * 0.9f), 1.9f))
            g.DrawEllipse(pen, x - 2.5f, y - 2.5f, sz + 5f, sz + 5f);
        if (OpenAiIcon != null) DrawIcon(g, OpenAiIcon, x, y, sz, fade, sz / 2f); // circular
        else
            using (var db = new SolidBrush(Mul(RingColor(st), fade)))
                g.FillEllipse(db, x, y, sz, sz);

        // balanced zones: verb hugs the icon, the timer owns the right edge — text length changes
        // never leave a lopsided gap. Moods (idle/offline) centre in the whole free space instead.
        var mood = Mood(st);
        string verb = OutageText() ?? (LimitHit ? "outta juice :(" : Shown(st) switch
        {
            "working" => ToolVerb(st?.CurrentTool, mood),
            "compacting" when Compacting(st) => Moods.Line("compacting", mood),
            "waiting_input" => "your move ;)",
            _ => IdleMood(st, mood),
        });
        string el = LimitHit ? LimitReset() : Elapsed(st); // limit shows regardless of session state
        if (Compacting(st) && !LimitHit && el.Length > 0) el = CompactPct(st!) + " · " + el;
        if (verb != _shownKey) { _shownKey = verb; _appear = 0f; } // timer ticking doesn't retrigger
        else if (_appear < 1f) _appear = Math.Min(1f, _appear + 0.1f);
        float e = 1f - MathF.Pow(1f - _appear, 3);
        bool busy = Shown(st) == "working" || Compacting(st) || LimitHit;
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

    private static DateTimeOffset? _cancelledCompactKey; // startedAt of a compact the user Esc'd out of

    public static void MarkCompactCancelled(DateTimeOffset? startedAt) => _cancelledCompactKey = startedAt;

    private static DateTimeOffset? _cancelledTurnKey; // startedAt of a turn the user interrupted

    public static void MarkTurnCancelled(DateTimeOffset? startedAt) => _cancelledTurnKey = startedAt;

    // the twin of ClaudeCodeWidget.SettleAfterSeconds, and deliberately the same number: the gap it
    // covers is a model thinking between tools, which is not a per-vendor quantity
    internal const int SettleAfterSeconds = 180;

    /// <summary>
    /// Whether a turn the file still calls "working" is actually over - the same hole the Claude twin
    /// had, for the same reason. An interrupt is not a lifecycle event, so nothing writes a status for
    /// it, and a pid-backed status counts as live for as long as the process runs: the pill sat on
    /// "hmm…" until the next real turn. Two ways out because there are two ways in - the stop button and
    /// the Esc watcher both latch the turn's own startedAt, which is exact and self-clearing since the
    /// next turn carries a new stamp, and the backstop is time, for an Esc typed into a terminal that is
    /// not the foreground agent host. The time path can only catch the thinking gap: while a tool runs,
    /// its name is on the status.
    /// </summary>
    internal static bool TurnOver(CodexSnapshot? st, DateTimeOffset now)
    {
        if (st is not { State: "working" }) return false;
        if (st.StartedAt is { } started && started == _cancelledTurnKey) return true;
        if (!string.IsNullOrEmpty(st.CurrentTool)) return false;
        // an absent stamp is not evidence that the turn ended - unlike the Claude twin's string field, this
        // one is non-nullable, so "never written" arrives as default rather than as null
        return st.UpdatedAt != default && now - st.UpdatedAt > TimeSpan.FromSeconds(SettleAfterSeconds);
    }

    // the state the panel should BELIEVE, which is not always the one on disk
    private static string? Shown(CodexSnapshot? st) =>
        TurnOver(st, DateTimeOffset.UtcNow) ? "idle" : st?.State;

    private static bool Compacting(CodexSnapshot? st) =>
        st?.State == "compacting" && st.StartedAt is { } t && t != _cancelledCompactKey
        && DateTimeOffset.UtcNow - t < TimeSpan.FromMinutes(3); // backstop if the Esc guess misses

    // ponytail: no duration history plumbed for Codex — pace against 180s (user-tuned 1/3 speed)
    private static string CompactPct(CodexSnapshot st) => st.StartedAt is { } t
        ? $"~{(int)Math.Clamp(100 * (DateTimeOffset.UtcNow - t).TotalSeconds / 180, 1, 99)}%" : "";

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

    // ---- layout. The same grid as the Claude twin, on purpose: the two panels sit in the same pill
    // and swapping between them should not move the furniture. Mirrored rather than shared, because
    // the data behind each row is a different type and the two modules do not depend on each other.
    private const int Pad = 22;
    private const float ColR = 356f, RightEdge = 538f;   // right column: graph, exit, freshness
    private const float RingCx = 84f, RingCy = 132f, RingOuter = 52f, RingBand = 8f, RingStep = 16f;
    private const float KeyX = 178f, KeyValX = 268f;
    private const float Row0 = 96f, RowPitch = 42f;

    // Rounded: AntiAliasGridFit hints glyph outlines onto the pixel grid and was being handed a
    // fractional origin, so every hinted stem was resampled across two pixels. Costs half a pixel of
    // layout, buys back the hinting - and the error is a fixed fraction, so the smaller the font the
    // larger the share of the glyph it ate.
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

    // Context colour. Same thresholds as the Claude twin so the two panels mean the same thing by the
    // same colour - Codex raises no compact banner of its own, but a user reading both should not have
    // to learn two scales. The arc and the figure both come from here; on the Claude side they did not,
    // and the panel spent a while showing a red number inside a blue ring.
    internal const float ContextWarnAt = 0.80f;

    internal static Color ContextColour(double frac)
        => frac < 0 ? Blue
         : frac >= ContextWarnAt ? Red
         : frac >= ContextWarnAt - 0.15f ? Amber : Blue;

    private void DrawExpanded(Graphics g, int w, int h, float a, CodexSnapshot? st)
    {
        using var title = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var line = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var keyCap = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        using var keyVal = new Font("Segoe UI Semibold", 16f, GraphicsUnit.Pixel);
        using var keySub = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var state = RingColor(st);

        // ---- header. The stop button IS the status lamp: one circle in one slot, so the title never
        // shifts between them - red stop while a prompt can be interrupted, plain lamp otherwise.
        DrawCancel(g, w, h, a, state);
        using (var tb = new SolidBrush(Mul(White, a)))
            Text(g, "Codex", title, tb, 84, 40);
        // The line under the title has one job: the question Codex is actually waiting on. The verbs
        // and the elapsed clock left with the redesign - narrating that something is running, in the
        // panel you opened because something is running, while the lamp already says so in colour.
        if (st?.State == "waiting_input" && !string.IsNullOrEmpty(st.Message))
            using (var ab = new SolidBrush(Mul(Amber, a)))
                TextClipped(g, st.Message!, line, ab, 84, 62, ColR - 92);

        // ---- the object: three arcs, outer to inner - primary window, secondary window, context
        // ContextMax > 0 is the whole test. The old panel also demanded the PresentFields flags, and
        // those are only ever set by the ROLLOUT parser - a status written by the hooks leaves them at
        // zero however real its numbers are. So a plain CLI session showed "no active session" under a
        // context it was perfectly able to report. A figure that came out of the file is not invented.
        double ctxFrac = st is not null && st.ContextMax > 0 ? ContextFrac(st) : -1;
        var ctxCol = ContextColour(ctxFrac);
        var primary = CodexLimits.Current?.Primary;
        var secondary = CodexLimits.Current?.Secondary;
        float pFrac = primary is null ? -1f : (float)(primary.UsedPercent / 100d);
        float sFrac = secondary is null ? -1f : (float)(secondary.UsedPercent / 100d);
        var rings = new (float frac, Color col)[]
        {
            (pFrac, pFrac >= 0 ? UsageColor(pFrac) : Dim),
            (sFrac, sFrac >= 0 ? UsageColor(sFrac) : Dim),
            ((float)ctxFrac, ctxCol),
        };

        // Which band the pointer is inside, by distance from the centre - the rings are concentric, so
        // the radius alone answers it and there is no need to test three shapes.
        int hotRing = -1;
        if (WidgetInput.Over)
        {
            float dx = WidgetInput.Mouse.X - RingCx, dy = WidgetInput.Mouse.Y - RingCy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            for (int i = 0; i < rings.Length; i++)
                if (MathF.Abs(dist - (RingOuter - i * RingStep)) <= RingBand / 2f + 3f) { hotRing = i; break; }
        }

        // eased on a time constant rather than a per-frame step, so the lift takes the same ~0.09s
        // whatever fps tier the pill has dropped to
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
            // an unfetched budget draws its track only: an arc at zero would read as "nothing spent yet"
            if (rings[i].frac < 0) continue;
            float sweep = Math.Clamp(rings[i].frac, 0f, 1f) * 360f;
            if (sweep <= 0.5f) continue;
            // the unhovered rings step BACK rather than the hovered one merely stepping forward: dimming
            // the others is what makes one of three concentric arcs actually read as picked out
            float other = hotRing >= 0 ? 1f - 0.35f * (1f - lift) : 1f;
            using var arc = new Pen(Mul(rings[i].col, a * other), band) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, RingCx - r, RingCy - r, r * 2, r * 2, -90f, sweep);
        }

        // The readout goes in the hole at the centre of the cluster, fading in with the same lift that
        // thickens the ring - so pointing at an arc and reading its figure are one movement.
        float show = 0f;
        int shown = -1;
        for (int i = 0; i < _ringLift.Length; i++)
            if (_ringLift[i] > show) { show = _ringLift[i]; shown = i; }
        if (shown >= 0 && show > 0.01f)
        {
            var (rf, rc) = rings[shown];
            string big = rf < 0 ? "—" : $"{Math.Clamp(rf, 0f, 1f) * 100:0}%";
            string cap2 = shown switch
            {
                0 => primary is null ? "no limit reported"
                    : $"{LimitCaption(primary)}  ·  {ResetIn(primary.ResetsAt ?? default)} left",
                1 => secondary is null ? "no second window"
                    : $"{LimitCaption(secondary)}  ·  {ResetIn(secondary.ResetsAt ?? default)} left",
                _ => ctxFrac >= 0 && st is not null
                    ? $"context  ·  {st.ContextUsed / 1000}K of {st.ContextMax / 1000}K" : "context  ·  no session",
            };
            // The hole is a CIRCLE, so the room for text is a chord, not a bounding box. A fixed size
            // overflowed "42%" onto the inner arc and would be worse at "100%", so it is measured down
            // a ladder until the figure fits the chord at its own height.
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
                // clear of the outer arc's BAND, and of the extra 3.2 it gains when hovered - which is
                // exactly when this line is on screen
                g.DrawString(cap2, underF, ub,
                    new RectangleF(RingCx - 74, RingCy + RingOuter + RingBand / 2f + 7f, 148, 16), mid);
        }

        // ---- the key. Caption and figure share a baseline; the sub-line sits on the next one down.
        bool KeyHover(int i) => WidgetInput.Over
            && WidgetInput.Mouse.X >= KeyX - 20 && WidgetInput.Mouse.X < ColR - 8
            && WidgetInput.Mouse.Y >= Row0 + i * RowPitch - 16 && WidgetInput.Mouse.Y < Row0 + i * RowPitch + 20;

        // The caption stays grey and only the FIGURE takes colour - the label is not the reading, and
        // colouring both turns the row into a block of one hue you have to decode. `hot` does the same
        // to one token inside the sub-line.
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

        // Rows take the next free slot rather than a fixed index, so one with nothing to say can be
        // left out and the rest close up.
        int slot = 0;

        // Unlike Claude's 5-hour window, Codex reports whatever windows the plan actually has - the
        // rollout may carry one, two or none. A missing one is therefore not a failed fetch, so it
        // vanishes rather than holding a slot to show a dash.
        if (primary is { })
        {
            int s = slot++;
            Key(s, UsageColor(pFrac), LimitCaption(primary),
                KeyHover(s) ? $"{pFrac * 100:0.#}%" : Pct(pFrac),
                primary.ResetsAt is { } pr
                    ? (KeyHover(s) ? $"resets {pr.ToLocalTime():ddd HH:mm}" : $"{ResetIn(pr)} left")
                    : "",
                UsageColor(pFrac));
        }
        if (secondary is { })
        {
            int s = slot++;
            Key(s, UsageColor(sFrac), LimitCaption(secondary),
                KeyHover(s) ? $"{sFrac * 100:0.#}%" : Pct(sFrac),
                secondary.ResetsAt is { } sr
                    ? (KeyHover(s) ? $"resets {sr.ToLocalTime():ddd HH:mm}" : $"{ResetIn(sr)} left")
                    : "",
                UsageColor(sFrac));
        }
        if (primary is null && secondary is null)
            Key(slot++, Dim, "usage", "—", "nothing reported yet");

        if (ctxFrac >= 0 && st is not null)
        {
            long maxK = st.ContextMax / 1000, usedK = Math.Min(st.ContextUsed / 1000, maxK);
            string maxLabel = maxK >= 1000 ? $"{maxK / 1000f:0.#}M" : $"{maxK}K";
            Key(slot, ctxCol, "context", $"{usedK}K", $"of {maxLabel}  ·  {ctxFrac * 100:0}% used", ctxCol,
                $"{ctxFrac * 100:0}%");
        }
        else Key(slot, Dim, "context", "—", "no active session");

        // ---- right column, all three blocks on the same left and right edge
        DrawNet(g, ColR, 74, RightEdge - ColR, 38, a);
        ExitBlock.Draw(g, a, keySub, keyCap, ColR, RightEdge,
            CodexNetMon.Snapshot().api, CodexNetMon.Empty, CodexNetMon.Lost);

        // A permanent "updated 4m ago" is a timestamp nobody asked for sitting in the corner, so the
        // age waits for the pointer. The word stays: a lone glyph is a guess about what it does.
        var rr = RefreshRect(w, h);
        bool rHover = WidgetInput.Over && rr.Contains(WidgetInput.Mouse);
        using (var rb = new SolidBrush(Mul(rHover ? White : Dim, a * (rHover ? 1f : 0.65f))))
        using (var rsf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
        {
            string label = rHover
                ? (CodexLimits.LastSuccess == DateTimeOffset.MinValue ? "never read  ·  ⟳ refresh"
                   : $"read {AgeText(DateTime.UtcNow - CodexLimits.LastSuccess)}  ·  ⟳ refresh")
                : "⟳ refresh";
            g.DrawString(label, keySub, rb, rr, rsf);
        }

        DrawNetHover(g, a); // last: the exit block would otherwise paint over the top of it
    }

    // Short captions, because the key column is 90px wide and "5-hour limit" does not fit beside its
    // figure. The window length is the honest name for it; "plan" covers whatever else a plan reports.
    private static string LimitCaption(CodexLimit limit) => limit.WindowMinutes switch
    {
        300 => "5-hour",
        10_080 => "weekly",
        _ => "plan",
    };

    // One element doing two jobs, in one slot so the title never shifts: while a prompt can be
    // interrupted it is the red stop button, and the rest of the time it is the status lamp in
    // whatever colour the state is. Drawing a button when there is nothing to cancel would be faking
    // an affordance, so the idle form has no ring and no square - just the lamp.
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

    // what the hover needs, stashed by DrawNet so the tooltip can be painted after everything else
    private (int[] net, int[] api, float x0, float step, int first, int count,
             float top, float bottom, float right)? _hover;

    // connection-to-OpenAI graph: green = your internet (GET google.com/generate_204), blue = the path
    // to chatgpt.com. Two series on one axis fight each other however they are drawn - as lines they
    // crossed, as filled areas they hid each other - so they are mirrored: your internet grows up from
    // a centre rule, the path to OpenAI grows down, one capsule per sample. Nothing overlaps, the
    // shared scale keeps them comparable, and a dropped sample is a full-height bar in red on whichever
    // side lost it, which is the question this graph exists to answer: whose fault is it.
    private void DrawNet(Graphics g, float colX, float topY, float colW, float colH, float a)
    {
        var (net, api) = CodexNetMon.Snapshot();
        int n = net.Length;

        bool hasData = false;
        foreach (var v in net) if (v != CodexNetMon.Empty) { hasData = true; break; }
        if (!hasData) foreach (var v in api) if (v != CodexNetMon.Empty) { hasData = true; break; }

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

        // Scale off three times the MEDIAN, not the maximum. A cold TLS handshake costs ~1450ms against
        // a steady ~85, and scaling to either the max or p90 lets that handful of spikes flatten every
        // honest sample into a stub. The median ignores them, so typical latency sits around a third of
        // the height and a spike clips at full height - which is what a spike should look like.
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

        // One origin for BOTH lanes: the samples are taken together, so spreading each series over its
        // own filled range would slide them out of time wherever one had a gap the other did not.
        int first = n;
        for (int i = 0; i < n; i++)
            if (net[i] != CodexNetMon.Empty || api[i] != CodexNetMon.Empty) { first = i; break; }
        int count = n - first;
        float slot = count > 0 ? span / count : span;
        float X(int i) => colX + 2f + i * slot + slot / 2f;

        // 0.94, so a sample at or over the cap stops just short of the edge: a shape welded to the
        // boundary reads as clipped rather than as a spike.
        float Mag(int v) => v == CodexNetMon.Lost ? half
            : v == CodexNetMon.Empty ? 1.2f
            : Math.Max(1.6f, half * 0.94f * Math.Clamp(v / (float)cap, 0.02f, 1f));

        // oldest sample at 0.45 alpha, newest at full: it reads as depth, and points the eye at "now"
        // without spending a label saying which end that is
        float Age(int i) => count < 2 ? 1f : 0.45f + 0.55f * (i / (float)(count - 1));

        using (var rule = new Pen(Mul(Dim, a * 0.22f), 1f))
            g.DrawLine(rule, colX, mid, colX + colW, mid);

        // capped: with a cold buffer the slices are wide, and an uncapped bar turns into a fat blob -
        // at which point a tall sample is a circle and the row reads as scattered pills
        float barW = Math.Clamp(slot - 2.2f, 2f, 5.5f);
        for (int i = 0; i < count; i++)
        {
            void Cap(int v, Color col, bool up)
            {
                if (v == CodexNetMon.Empty) return;
                bool lost = v == CodexNetMon.Lost;
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

        // The tooltip IS the axis now - it reads out any bar you point at - so the scale figure that
        // used to ride the legend was a developer's number sitting in the corner. The unit rides the
        // last figure instead of floating alone at the right edge.
        int lastN = LastSample(net), lastA = LastSample(api);
        string tn = Fx.NetLabel + " " + (lastN == CodexNetMon.Empty ? "…" : lastN == CodexNetMon.Lost ? ":(" : lastN.ToString());
        string ta = Fx.ApiLabel + " " + (lastA == CodexNetMon.Empty ? "…" : lastA == CodexNetMon.Lost ? ":(" : lastA + " ms");
        using (var f = new Font("Segoe UI", 13f, GraphicsUnit.Pixel))
        {
            float bl = topY - 8;
            using (var b = new SolidBrush(Mul(lastN == CodexNetMon.Lost ? Red : Green, a)))
                Text(g, tn, f, b, colX, bl);
            float wN = g.MeasureString(tn, f, PointF.Empty, StringFormat.GenericTypographic).Width;
            using (var b = new SolidBrush(Mul(Dim, a * 0.7f)))
                Text(g, "·", f, b, colX + wN + 6, bl);
            using (var b = new SolidBrush(Mul(lastA == CodexNetMon.Lost ? Red : Blue, a)))
                Text(g, ta, f, b, colX + wN + 18, bl);
        }

        _hover = (net, api, colX + 2f, slot, first, count, topY, topY + colH, colX + colW);
    }

    // the tooltip is the precise read: both values, how many samples each side lost, and whose fault a
    // drop was - the one thing the shape alone cannot say
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
        if (vN == CodexNetMon.Empty && vA == CodexNetMon.Empty) return;

        float gx = x0 + (idx - first) * step;
        using (var guide = new Pen(Mul(White, a * 0.30f), 1f) { DashStyle = DashStyle.Dot })
            g.DrawLine(guide, gx, top, gx, bottom);

        int lostN = 0, cntN = 0, lostA = 0, cntA = 0;
        for (int i = 0; i < net.Length; i++)
        {
            if (net[i] != CodexNetMon.Empty) { cntN++; if (net[i] == CodexNetMon.Lost) lostN++; }
            if (api[i] != CodexNetMon.Empty) { cntA++; if (api[i] == CodexNetMon.Lost) lostA++; }
        }
        string F(int v) => v == CodexNetMon.Lost ? ":(" : v == CodexNetMon.Empty ? "–" : $"{v} ms";
        var lines = new List<(string t, Color c)>
        {
            ($"{Fx.NetLabel} {F(vN)}   {Fx.ApiLabel} {F(vA)}", White),
            ($"{Fx.LossLabel}  {Fx.NetLabel} {lostN}/{cntN}  ·  {Fx.ApiLabel} {lostA}/{cntA}", Dim),
            ("google.com  ·  chatgpt.com", Dim),
        };
        if (vA == CodexNetMon.Lost && vN >= 0) lines.Add(("OpenAI's side :(", Amber));
        else if (vN == CodexNetMon.Lost) lines.Add(("your internet :(", Red));

        using var f2 = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        float bw2 = 0;
        foreach (var l in lines) bw2 = Math.Max(bw2, g.MeasureString(l.t, f2).Width);
        bw2 += 16;
        float bh2 = lines.Count * 15 + 10;
        float bx = Math.Clamp(gx - bw2 / 2f, Pad, right - bw2);
        float by = bottom + 8;
        if (by + bh2 > 214) by = top - bh2 - 8;   // no room below: hang it above the lanes
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
        for (int i = s.Length - 1; i >= 0; i--) if (s[i] != CodexNetMon.Empty) return s[i];
        return CodexNetMon.Empty;
    }

    // in front of the title, where the lamp used to be, and clear of the pushpin the controller paints
    // at (9,4,24,24) - the two were overlapping on the Claude twin, so a press meant for one landed on
    // the other
    private static RectangleF CancelRect(int w, int h) => new(42, 16, 34, 34);

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
            (RefreshRect(w, h), (Action<PointF>)(_ => { _store.ForceRefresh(); CodexLimits.ForceRefresh(); })),
        };

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

    private static double ContextFrac(CodexSnapshot? st) =>
        st is null || st.ContextMax <= 0 ? 0 : Math.Clamp((double)st.ContextUsed / st.ContextMax, 0, 1);

    private static Color StateColor(string? state) => state switch
    {
        "working" => Green,
        "compacting" => Blue,
        "waiting_input" => Amber,
        _ => Color.FromArgb(140, 255, 255, 255),
    };

    // ring mirrors the CLI spinner's colours, except its normal orange → green (orange = icon colour,
    // it would vanish): green = working, yellow = deep thinking / needs input, red = error, white = idle
    // primary (5-hour) window first, secondary (weekly) as the stand-in; 0 draws nothing rather than
    // implying an empty budget
    private static float UsageFrac()
        => CodexLimits.FiveHour >= 0 ? CodexLimits.FiveHour : CodexLimits.Week >= 0 ? CodexLimits.Week : 0f;

    // the twin of the Claude ring: the states whose colour is the message stay exactly as they are, and
    // everything else rides the situation (see Fx.MoodRing)
    private static bool RingIsTheMessage(CodexSnapshot? st)
        => CodexNetMon.ApiDown || CodexNetMon.NetDown || LimitHit || Compacting(st);

    private static Color RingBase(CodexSnapshot? st)
        => CodexNetMon.ApiDown || CodexNetMon.NetDown ? Red
         : LimitHit ? White                 // out of juice: nothing can run, so the ring reads idle. Amber implied
                                            // activity and left the pill looking busy while it was waiting on a reset.
         : st?.State == "waiting_input" ? Fx.SlotColor("asking")
         : Compacting(st) ? Blue
         : JustCompacted(st) ? Mint
         : Shown(st) == "working" ? Fx.SlotColor(ToolSlot(st?.CurrentTool))
         : White;

    private Color RingColor(CodexSnapshot? st)
    {
        var b = RingBase(st);
        if (RingIsTheMessage(st)) return b;
        bool hueIsFree = st?.State != "waiting_input"
            && (Shown(st) != "working" || string.IsNullOrEmpty(st?.CurrentTool));
        return Fx.MoodRing(b, Mood(st), hueIsFree);
    }

    private static string Pct(float f) => $"{(int)Math.Round(f * 100)}%";

    private static Color LerpC(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    // one ramp for both panels, in Fx: they had drifted and the same figure wore two colours
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

    // the state→slot mapping on its own, with no session behind it: what the tests pin, since the routing
    // is the part with logic in it. Nothing situational to feed it, so it gets the empty context.
    internal static string DisplayText(string state, string? tool, bool apiDown, bool netDown) =>
        netDown ? "net error :(" : apiDown ? "api error :(" : state switch
        {
            "working" => ToolVerb(tool, new MoodContext()),
            "compacting" => "compacting…",
            "waiting_input" => "your move ;)",
            _ => Moods.Line("idle"),
        };

    // Codex sits at its "limit reached" prompt still flagged "working" — swap mood + show when
    // it comes back instead of an ever-growing turn timer (same treatment as the CC widget)
    private static bool LimitHit => CodexLimits.FiveHour >= 0.99f || CodexLimits.Week >= 0.99f;

    private static string LimitReset()
    {
        var r = ResetIn(CodexLimits.FiveHour >= 0.99f ? CodexLimits.FiveHourReset : CodexLimits.WeekReset);
        return r.Length > 0 ? "back in " + r : "";
    }

    // minimal mood line when nothing is running
    private static string IdleMood(CodexSnapshot? st, in MoodContext ctx) =>
        CodexNetMon.NetDown ? Moods.Line("offline")
        : CodexNetMon.ApiDown ? Moods.Line("apiDown")
        : JustCompacted(st) ? Moods.Line("compacted")
        : CodexLimits.FiveHour >= 0.95f ? Moods.Line("outOfCredit")
        : Moods.Line("idle", ctx);

    private static bool JustCompacted(CodexSnapshot? st) =>
        st?.CompactedAt is { } t && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(20);

    // an outage overrides whatever the verb was — even mid-work "writing…" becomes the error
    private static string? OutageText() =>
        CodexNetMon.NetDown ? Moods.Line("netError") : CodexNetMon.ApiDown ? Moods.Line("apiError") : null;

    // codex has emitted both custom_tool_call and function_call names; dotted prefixes are already stripped.
    // The twin of ClaudeCodeWidget.ToolSlot, and the same single mapping: the words and the ring's colour
    // both come off the slot, so they cannot disagree about what is happening.
    internal static string? ToolSlot(string? tool) => tool switch
    {
        "exec" or "shell" or "shell_command" or "local_shell" or "exec_command" or "container" => "running",
        "apply_patch" or "edit" or "write_file" => "patching",
        "read_file" or "view" or "cat" => "reading",
        "grep" or "rg" or "find" or "list_dir" or "ls" => "digging",
        "web_search" or "search" => "searching",
        "browser" or "fetch" or "open_url" => "fetching",
        "view_image" or "screenshot" => "peeking",
        "update_plan" or "plan" => "plotting",
        "spawn" or "agent" or "subagent" or "thread_spawn" => "delegating",
        "request_user_input" or "ask" => "asking",
        "wait" or "poll" or "watch" => "watching",
        null or "" => "unknown",
        _ when tool.StartsWith("mcp", StringComparison.OrdinalIgnoreCase) => "consulting",
        _ => null,
    };

    // the situation rides along, same as the Claude twin: a slot with a set for what is going on switches
    // to it rather than repeating one word for four minutes
    private static string ToolVerb(string? tool, in MoodContext ctx)
        => ToolSlot(tool) is { } slot ? Moods.Line(slot, ctx) : Moods.PrettyTool(tool);

    // how long the current turn has been going, as a span rather than the display string
    private static TimeSpan? Running(CodexSnapshot? st) =>
        st?.StartedAt is { } t ? DateTimeOffset.UtcNow - t : null;

    // the twin of ClaudeCodeWidget.Mood: every field is a figure the panel already draws, so the voice
    // and the rings cannot disagree about the situation
    private MoodContext Mood(CodexSnapshot? st) => new(
        Running(st), (float)ContextFrac(st), UsageFrac(),
        st?.PromptTokens ?? 0, ToolRuns(st), DateTime.Now.Hour);

    // tool hand-offs inside the current turn, keyed by the turn's own startedAt so a new turn resets it
    private DateTimeOffset? _runsTurn;
    private string? _runsTool;
    private int _runs;

    private int ToolRuns(CodexSnapshot? st)
    {
        var stamp = st?.StartedAt;
        if (stamp != _runsTurn) { _runsTurn = stamp; _runsTool = null; _runs = 0; }
        var tool = st?.CurrentTool;
        if (!string.IsNullOrEmpty(tool) && tool != _runsTool) { _runsTool = tool; _runs++; }
        return _runs;
    }

    // how long the current turn (or compact) has been running
    private static string Elapsed(CodexSnapshot? st)
    {
        if ((Shown(st) != "working" && !Compacting(st)) || st?.StartedAt is not { } t) return "";
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 1) return "";
        return d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.Seconds}s";
    }
}
