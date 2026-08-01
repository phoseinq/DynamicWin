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
    private static readonly Color Mint = Color.FromArgb(82, 224, 163);   // just compacted: there is room again
    private const float MinVerbPx = 12.5f;   // below this the voice is present rather than readable
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
        ? new AgentNotice(Shown(status), ParseTime(status.CompactedAt), status.Message)
        : AgentNotice.None;
    public IEnumerable<int> OwnerPids => Live is { } st ? new[] { st.Pid, st.ConsolePid } : Array.Empty<int>();
    // text-emerge animation + the compacting pulse both need frames while collapsed. The flag's ripple needs
    // them too, but only while the pointer is actually on the panel: pinned open with the mouse elsewhere,
    // nobody is looking at it, and a permanent repaint for a flourish nobody can see is not worth the wake-ups.
    // RingsSettling keeps frames coming after the pointer leaves, or the lift would freeze half-raised and
    // the next hover would start from wherever it stopped.
    public bool Animating => _appear < 1f || Compacting(Live)
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
        //
        // The layout is measured BEFORE the words are chosen, because the gap is what decides how long a
        // line may be. It used to run the other way round: the voice picked a nineteen-character line, the
        // gap held twelve, and the renderer shrank the font until it fitted - 9px, which is present rather
        // than readable. Now the words are chosen to fit and the font stays legible.
        string el0 = LimitHit ? LimitReset() : Elapsed(st);
        if (Compacting(st) && !LimitHit && ContextPct(st!) is { Length: > 0 } ctx)
            el0 = el0.Length > 0 ? ctx + " · " + Coarse(el0) : ctx;
        float textX0 = x + sz + 11;
        if (st?.State == "waiting_input") textX0 += 16;
        using var elFont = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        float elW0 = el0.Length > 0
            ? g.MeasureString(el0, elFont, int.MaxValue, StringFormat.GenericTypographic).Width : 0;
        float avail0 = (w - 14) - textX0 - (elW0 > 0 ? elW0 + 10 : 0);
        // The budget only means something once the pill is at its settled collapsed size. Mid-morph w is
        // transient, and a line picked under a transient budget is then HELD for a minute - which is how
        // "idle", the shortest line in the set, ended up sitting on a full-width pill. A budget under eight
        // characters is not a real one either: it means the pill is animating, not that the words must fit
        // in eight characters.
        int fit = fade > 0.99f ? Fx.FitChars(g, avail0, MinVerbPx) : 0;
        var mood = Mood(st) with { MaxChars = fit >= 8 ? fit : 0 };
        string verb = OutageText() ?? (LimitHit ? "outta juice :(" : Shown(st) switch
        {
            "working" => ToolVerb(Glow(st).Tool, mood),
            "compacting" when Compacting(st) => Moods.Line("compacting", mood),
            "waiting_input" => "your move ;)",
            _ => IdleMood(st, mood),
        });
        string el = el0;  // limit shows regardless of session state; measured above, before the wording
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
            // the floor was 9px, which is where "the words are there" stops meaning "the words can be read".
            // Now that the wording is chosen against the space it will get (MoodContext.MaxChars), shrinking
            // this far should be rare - and when it does happen the line stays legible instead.
            if (m0.Width > avail && m0.Width > 0) px = Math.Max(MinVerbPx, px * avail / m0.Width);
        }
        using var f = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, fade * e));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = centred ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            // twin of the Codex pill: a line that still does not fit at MinVerbPx ends in an ellipsis
            // rather than being sliced through the middle of a glyph at the pill's edge
            Trimming = StringTrimming.EllipsisCharacter,
        };
        // the words end where the gap they were measured against ends. avail already excludes the timer,
        // so this is what stops a long line running under the clock and off the end of the pill.
        float originX = textX - 16f * (1f - e);   // the entrance: words slide out from behind the icon
        float rightEdge = textX + avail;
        var clip = g.Clip;
        g.SetClip(new RectangleF(x + sz + 2, 0, rightEdge - (x + sz + 2), h)); // text is born behind the icon
        // the old width was a flat avail + 16, where the 16 pays for that entrance shift — but it was paid
        // at every e, so a settled pill overhung its own budget by 16px. Tie it to the shift that earns it.
        float zoneW = (centred ? rightEdge - 34f : rightEdge) - originX; // centred moods lean toward the icon
        // lift comes from the font metrics (Fx.CenterLift), not a fixed pixel: px shrinks to fit
        g.DrawString(verb, f, b, new RectangleF(originX, -Fx.CenterLift(f), zoneW, h), sf);
        g.Clip = clip;

        if (elW > 0) // timer zone, right-aligned and dimmer so the verb stays the focus
            using (var eb = new SolidBrush(Mul(Dim, fade * e)))
            using (var esf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(el, tf2, eb, new RectangleF(w - 14 - elW - 4, -Fx.CenterLift(tf2), elW + 4, h), esf);

    }

    private static string? _cancelledCompactKey; // startedAt of a compact the user Esc'd out of

    public static void MarkCompactCancelled(string? startedAt) => _cancelledCompactKey = startedAt;

    private static string? _cancelledTurnKey; // startedAt of a turn the user interrupted

    public static void MarkTurnCancelled(string? startedAt) => _cancelledTurnKey = startedAt;

    // How long a tool-less "working" is believed. Long enough to cover a thinking block, short enough
    // that a stuck pill fixes itself while you are still looking at it.
    internal const int SettleAfterSeconds = 180;

    /// <summary>
    /// Whether a turn the file still calls "working" is actually over. Interrupting a turn leaves no
    /// trace at all: Claude Code writes status on lifecycle events and an Esc is not one, so the last
    /// thing written stays "working" with no tool - and a pid-backed status counts as live for as long
    /// as the process runs, so the pill sat on "hmm…" forever. Reported for both the Esc key and the
    /// panel's own stop button, which is not a coincidence: the button injects Esc, so the two paths
    /// are the same path and neither produced a hook.
    ///
    /// Two ways out, because there are two ways in. The button and the Esc watcher latch the turn's own
    /// startedAt, which is exact and self-clearing - the next turn carries a new stamp. Nothing can see
    /// an Esc typed into a terminal that is not the foreground agent host, so the backstop is time: a
    /// "working" with no tool name that has not been written to in SettleAfterSeconds is treated as
    /// over. It can only ever catch the thinking gap, because while a tool runs its name is on the
    /// status - and being wrong costs a wrongly-idle pill until the next hook, not a permanently stuck one.
    /// </summary>
    internal static bool TurnOver(CcStatus? st, DateTimeOffset now)
    {
        if (st is not { State: "working" }) return false;
        if (st.StartedAt is { Length: > 0 } && st.StartedAt == _cancelledTurnKey) return true;
        if (!string.IsNullOrEmpty(st.CurrentTool)) return false;
        return ParseTime(st.UpdatedAt) is { } u && now - u > TimeSpan.FromSeconds(SettleAfterSeconds);
    }

    // the state the panel should BELIEVE, which is not always the one on disk
    private static string? Shown(CcStatus? st) =>
        TurnOver(st, DateTimeOffset.UtcNow) ? "idle" : st?.State;

    private static bool Compacting(CcStatus? st) =>
        st?.State == "compacting" && st.StartedAt != _cancelledCompactKey
        && ParseTime(st.StartedAt) is { } t
        && DateTimeOffset.UtcNow - t < TimeSpan.FromMinutes(3); // backstop if the Esc guess misses

    // What is actually known while a compact runs.
    //
    // This used to be elapsed/expected against the PREVIOUS compact's duration, printed as "~47%" — a
    // progress bar for something that reports no progress. Nothing is written to the transcript between
    // pre-compact and post-compact, so there is no signal there to find, and the house rule is that a
    // figure Halo cannot obtain is not shown at all.
    //
    // The real number is the one the compact is ABOUT: how full the context is, straight out of the
    // transcript's own token usage, which is the same figure the panel's ring and the "context NN% full"
    // banner carry — one number, not three. It is deliberately not a progress reading: it holds while the
    // compact runs and DROPS when it lands, which is the event worth watching. Labelled, so it cannot be
    // read as one either.
    internal static string ContextPct(CcStatus st)
        => st.Session is { ContextMax: > 0 } ? $"ctx {(int)(ContextFrac(st) * 100)}%" : "";

    // Minutes only, once there are minutes. Two figures on the right of a 220px pill is one more than it
    // was designed for, and every character there is taken off the verb - which arrived truncated
    // mid-word the first time both were shown. During a compact the seconds are noise anyway: the question
    // is whether this is taking long, not exactly how long.
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

    // Everything on this panel is positioned from a BASELINE, not from a top-left corner. Mixing 13px and
    // 18px text in one row and giving both the same y puts their baselines ~4px apart, which is exactly
    // the "nothing lines up" the layout kept being accused of - and it is invisible until you look for it.
    // TextTop() converts a baseline into the top-left GDI+ actually wants, using the font's own ascent.
    // Past this the answers measurably drift and the fix is /compact, so it is worth one banner. Public
    // because the controller raises that banner and the panel colours its figures off the same number -
    // two places disagreeing about "nearly full" would be worse than either threshold.
    internal const float ContextWarnAt = 0.80f;

    // Context has its own ramp: 0 blue while there is room, 1 amber on the approach, 2 red past the line
    // where /compact stops being optional. Shares ContextWarnAt with the banner on purpose - the figure
    // turning red and the banner arriving are the same event, and pure so both can be pinned by a test.
    internal static int ContextBand(float frac)
        => frac >= ContextWarnAt ? 2 : frac >= ContextWarnAt - 0.15f ? 1 : 0;

    // The one place that turns a context fraction into a colour. It used to be two: the arc was a flat
    // Blue and the figure ran the band ramp, so at 86% the panel showed a red number inside a blue ring.
    // A negative fraction is "no session", which is the track's own grey job - the arc is skipped
    // entirely at that point, so the colour here only has to be something harmless.
    internal static Color ContextColour(double frac)
        => frac < 0 ? Blue : ContextBand((float)frac) switch { 2 => Red, 1 => Amber, _ => Blue };

    // (session id, fraction) for the alert. Null id = nothing live to warn about.
    internal (string? id, float frac) ContextState()
    {
        var st = Live;
        if (st?.Session is not { ContextMax: > 0 } ses) return (null, -1f);
        var id = st.Pid + ":" + st.StartedAt; // pids get recycled, so the start stamp is what makes it a session
        return (id, (float)Math.Clamp((double)ses.ContextUsed / ses.ContextMax, 0, 1));
    }

    private const int Pad = 22;
    private const float ColR = 356f, RightEdge = 538f;   // right column: graph, exit, freshness
    private const float RingCx = 84f, RingCy = 132f, RingOuter = 52f, RingBand = 8f, RingStep = 16f;
    private const float KeyX = 178f, KeyValX = 268f;     // key captions and their figures, both fixed
    private const float Row0 = 96f, RowPitch = 42f;      // baselines of the three key rows

    // Rounded, and that rounding is the whole reason the small rows got legible. AntiAliasGridFit hints the
    // glyph outlines onto the PIXEL GRID, and it was being handed a fractional origin: the ascent ratio puts
    // this at .915 of a pixel and the x comes out of MeasureString just as fractional, so every hinted stem
    // was resampled across two pixels and arrived soft and uneven. The error is a fixed fraction of a pixel,
    // so the smaller the font the larger the share of the glyph it eats - which is exactly why the 12-13px
    // rows looked worse than the title. Costs at most half a pixel of layout, buys back the hinting.
    private static float TextTop(Font f, float baseline)
        => MathF.Round(baseline - f.FontFamily.GetCellAscent(f.Style) / (float)f.FontFamily.GetEmHeight(f.Style) * f.Size);

    private static void Text(Graphics g, string t, Font f, Brush b, float x, float baseline)
        => g.DrawString(t, f, b, MathF.Round(x), TextTop(f, baseline), StringFormat.GenericTypographic);

    // typographic measure, so laying runs side by side matches what DrawString actually advanced - the
    // default MeasureString pads and the seams drift apart. MeasureTrailingSpaces because a run ending in
    // the separator's spaces otherwise measures short and the next run slides left onto the dot.
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
        // whole pixels, not 12.5: a half-pixel em cannot be grid-fitted, so the hinter rounds the size and
        // then every advance lands between pixels anyway. The three sub-13px fonts here were all 12.5.
        using var keySub = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var state = RingColor(st); // yellow while thinking, green on a tool - same as the collapsed ring

        // ---- header. The stop button is the status lamp: same circle, same slot, so the title never
        // shifts between them - red stop while a prompt can be interrupted, plain lamp otherwise.
        DrawCancel(g, w, h, a, state);
        using (var tb = new SolidBrush(Mul(White, a)))
            Text(g, "Claude Code", title, tb, 84, 40);
        // The line under the title is down to ONE job: the question Claude is actually waiting on. The verbs
        // ("hmmm", "googling :P"), the moods and the elapsed clock all left - narrating that something is
        // running, in the panel you opened because something is running, and the lamp beside the title
        // already says so in colour. A question is the one thing here that is addressed to you.
        if (st?.State == "waiting_input" && !string.IsNullOrEmpty(st.Message))
            using (var ab = new SolidBrush(Mul(Amber, a)))
                TextClipped(g, st.Message!, line, ab, 84, 62, ColR - 92);

        // ---- the object: three arcs, outer to inner - 5-hour, weekly, context
        double ctxFrac = st?.Session is { ContextMax: > 0 } ? ContextFrac(st) : -1;
        // One colour for the context reading, decided once. The arc used to be a flat Blue while the
        // figure below it went through ContextBand's blue/amber/red, so at 86% the number was red and
        // the ring it belongs to was still blue - the same value painted two different colours in the
        // same panel. The band, not UsageColor: context has its own thresholds because they are the
        // ones the /compact banner fires on, and the arc has to agree with the warning, not with the
        // usage rows beside it.
        var ctxCol = ContextColour(ctxFrac);
        var rings = new (float frac, Color col)[]
        {
            (Limits.FiveHour, Limits.FiveHour >= 0 ? UsageColor(Limits.FiveHour) : Dim),
            (Limits.Week,     Limits.Week     >= 0 ? UsageColor(Limits.Week)     : Dim),
            ((float)ctxFrac,  ctxCol),
        };
        // Which band the pointer is inside, by distance from the centre - the rings are concentric, so the
        // radius alone answers it and there is no need to test three shapes.
        int hotRing = -1;
        if (WidgetInput.Over)
        {
            float dx = WidgetInput.Mouse.X - RingCx, dy = WidgetInput.Mouse.Y - RingCy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            for (int i = 0; i < rings.Length; i++)
                if (MathF.Abs(dist - (RingOuter - i * RingStep)) <= RingBand / 2f + 3f) { hotRing = i; break; }
        }

        // Eased towards the target with a time constant rather than a per-frame step, so the lift takes the
        // same ~0.09s whatever fps tier the pill is running at.
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
            // the unhovered rings step back rather than the hovered one merely stepping forward: dimming the
            // others is what makes one of three concentric arcs actually read as picked out
            float other = hotRing >= 0 ? 1f - 0.35f * (1f - lift) : 1f;
            using var arc = new Pen(Mul(rings[i].col, a * other), band) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, RingCx - r, RingCy - r, r * 2, r * 2, -90f, sweep);
        }

        // The readout goes in the hole at the centre of the cluster, which was empty while the numbers it
        // belongs to sat over in the next column. Fades in with the same lift that thickens the ring, so
        // pointing at an arc and reading its figure are one movement.
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
            // The hole is a CIRCLE, so the room for text is a chord, not the bounding box: at radius 16 that
            // is ~30px across the middle and less above and below. A fixed 15px overflowed "42%" onto the
            // inner arc and would have been worse at "100%", so the size is picked by measuring down a
            // ladder until the figure fits the chord at its own height.
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
                // clear of the outer arc's BAND, not just its radius - and of the extra 3.2 the band gains
                // when that arc is the one being hovered, which is exactly when this line is on screen
                g.DrawString(cap2, underF, ub,
                    new RectangleF(RingCx - 74, RingCy + RingOuter + RingBand / 2f + 7f, 148, 16), mid);
        }

        // ---- the key. Caption and figure share a baseline; the sub-line sits on the next one down.
        bool KeyHover(int i) => WidgetInput.Over
            && WidgetInput.Mouse.X >= KeyX - 20 && WidgetInput.Mouse.X < ColR - 8
            && WidgetInput.Mouse.Y >= Row0 + i * RowPitch - 16 && WidgetInput.Mouse.Y < Row0 + i * RowPitch + 20;

        // The caption stays grey and only the FIGURE takes colour - the label is not the reading, and
        // colouring both turns the row into a block of one hue you have to decode. `hot` does the same to
        // one token inside the sub-line, so "7% used" can carry the state while "of 1M" stays quiet.
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

        // Rows take the next free slot rather than a fixed index, so one that has nothing to say can be
        // left out and the rest close up. Without this, hiding the weekly row would leave its gap behind.
        int slot = 0;

        if (Limits.FiveHour >= 0)
        {
            int s = slot++;
            string sub = KeyHover(s) ? $"resets {Limits.FiveHourReset.ToLocalTime():ddd HH:mm}"
                                     : $"{ResetIn(Limits.FiveHourReset)} left";
            // Credits ride the 5-hour row, but only once you point at it. They used to sit on the resting
            // line beside the countdown, which put a dollar figure on screen permanently for a number most
            // glances are not asking about. The richer form (remaining, or spent against the cap) shows when
            // the API exposes it; promotional balance on claude.ai is NOT returned to the Claude Code token,
            // hence the fallback to plain spend.
            if (Limits.CreditsUsed > 0 && KeyHover(s))
                sub += Limits.CreditsBalance >= 0 ? $"  ·  ${Limits.CreditsBalance:0.00} left"
                     : Limits.CreditsLimit > 0 ? $"  ·  ${Math.Max(0, Limits.CreditsLimit - Limits.CreditsUsed):0.00} of ${Limits.CreditsLimit:0}"
                     : $"  ·  ${Limits.CreditsUsed:0.00} used";
            Key(s, UsageColor(Limits.FiveHour), "5-hour",
                KeyHover(s) ? $"{Limits.FiveHour * 100:0.#}%" : Pct(Limits.FiveHour), sub,
                UsageColor(Limits.FiveHour));
        }
        // The em-dash already says "no figure"; spelling it out underneath was the same fact twice. This
        // row keeps the dash rather than vanishing: the 5-hour window always exists on a Claude account,
        // so a missing figure means the fetch failed, and that is worth seeing.
        else Key(slot++, Dim, "5-hour", "\u2014", "");

        // The weekly row disappears outright when there is no figure, instead of holding a slot to show a
        // dash. Unlike the 5-hour window this one may genuinely not apply to the account, so an empty row
        // here reports no failure - it just occupies space to say nothing.
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

        // ---- right column, all three blocks on the same left edge and the same right edge
        // 46 -> 38: two lanes need less than a mirrored axis did, and the 8px buys the exit block below a
        // bigger flag and a bottom margin it did not have
        DrawNet(g, ColR, 74, RightEdge - ColR, 38, a);
        ExitBlock.Draw(g, a, keySub, keyCap, ColR, RightEdge,
            NetMon.Snapshot().api, NetMon.Empty, NetMon.Lost);

        // A permanent "updated 4m ago" is a timestamp nobody asked for sitting in the corner, so the age
        // waits for the pointer. The word stays, though: a lone glyph is a guess about what it does, and
        // this one is a button - it has to read as pressable without being hovered first.
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

        DrawNetHover(g, a); // last: the exit block used to be painted over the top of it
    }

    // The exit block moved to ExitBlock: it reports the machine's network, not this agent's, and the
    // Codex panel shows the identical thing. Only the latency series is ours.
    internal static RectangleF ExitRect() => ExitBlock.Rect(ColR, RightEdge);

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

        // One origin for BOTH lanes: the samples are taken together, so spreading each series over its own
        // filled range would slide them out of time with each other wherever one had a gap the other did not.
        int first = n;
        for (int i = 0; i < n; i++)
            if (net[i] != NetMon.Empty || api[i] != NetMon.Empty) { first = i; break; }
        int count = n - first;
        // Sample positions: the centre of an equal slice of the column. Every style lands on the same x,
        // so the tooltip needs one formula, and the slices shrink as the buffer fills - which is what keeps
        // the shape spanning the whole width instead of packing against the right edge the way it used to.
        float slot = count > 0 ? span / count : span;
        float X(int i) => colX + 2f + i * slot + slot / 2f;

        // 0.94, so a sample at or over the cap stops just short of the edge: a shape welded to the boundary
        // reads as clipped rather than as a spike.
        float Mag(int v) => v == NetMon.Lost ? half
            : v == NetMon.Empty ? 1.2f
            : Math.Max(1.6f, half * 0.94f * Math.Clamp(v / (float)cap, 0.02f, 1f));

        // The oldest sample sits at 0.45 alpha and the newest at full. It reads as depth, and it points the
        // eye at "now" without spending a label saying which end that is.
        float Age(int i) => count < 2 ? 1f : 0.45f + 0.55f * (i / (float)(count - 1));

        void Rule(float alpha)
        {
            using var rule = new Pen(Mul(Dim, a * alpha), 1f);
            g.DrawLine(rule, colX, mid, colX + colW, mid);
        }

        // Mirrored capsules, one per sample: your internet growing up from a centre rule, the path to
        // Anthropic growing down. Nothing ever overlaps, the shared scale keeps the two comparable, and a
        // dropped sample is a full-height bar in red on whichever side lost it - whose fault is it being the
        // read this graph exists for. Chosen over a filled ridge and a dot field, both of which were built
        // and looked at before this one was kept.
        void Waveform()
        {
            Rule(0.22f);
            // capped: with a cold buffer the slices are wide, and an uncapped bar turns into a fat blob -
            // at which point a tall sample is a circle and the row reads as scattered pills
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

        // The legend used to carry the axis cap too ("ms · scale 1500"), on the grounds that a mirrored
        // profile has nothing to hang numbers off. The tooltip IS that axis now - it reads out any bar you
        // point at - so the cap was a developer's number sitting in the corner. The unit rides the last
        // figure instead of floating alone at the right edge.
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

    // the tooltip is the precise read: both values, how many samples each side lost, and whose fault a
    // drop was - the one thing the shape alone cannot say
    private void DrawNetHover(Graphics g, float a)
    {
        if (_hover is not { } hv) return;
        var (net, api, x0, step, first, count, top, bottom, right) = hv;
        var m = WidgetInput.Mouse;
        if (!WidgetInput.Over || m.X < x0 || m.X > right || m.Y < top - 10 || m.Y > bottom + 10) return;
        if (count <= 0) return;
        // nearest sample, not the one whose cell was landed in: the trace is points joined by lines now,
        // so the thing under the pointer is a vertex, and rounding is what picks the one being looked at
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
        for (int i = s.Length - 1; i >= 0; i--) if (s[i] != NetMon.Empty) return s[i];
        return NetMon.Empty;
    }

    // in front of the title, where the lamp used to be
    // clear of the pushpin the controller paints at (9,4,24,24) - the two were overlapping, so a press
    // meant for one landed on the other
    private static RectangleF CancelRect(int w, int h) => new(42, 16, 34, 34);

    // bottom-right of the band, right-aligned to the panel's padding
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
        // The dns row is only pressable when it is on screen, and it moves: how many rows sit above it
        // depends on whether the exits have split. So DrawExit records where it actually put the row and
        // this hands back that exact rect - a hit area derived a second time from the same row index would
        // be one more thing to keep in step, and the cursor reads this list too.
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

    // Everything the voice is allowed to know about this moment, and every part of it is already drawn
    // somewhere on the panel: the turn clock, the context bar, the usage ring, the turn's own prompt
    // size, how many tools it has reached for, and the wall clock. Built in one place so the collapsed
    // pill and anything else that asks cannot disagree about what the situation is.
    private MoodContext Mood(CcStatus? st) => new(
        Running(st), (float)ContextFrac(st), UsageFrac(),
        st?.Session?.PromptTokens ?? 0, ToolRuns(st), DateTime.Now.Hour, Glow(st).Target);

    // The hook clears currentTool the moment a tool finishes, and the gap that follows - the model writing
    // its next move - is many times longer than the call itself. So a ring keyed on the CURRENT tool spent
    // almost all of its life on the tool-less amber, and with pressure warming that amber the pill read as
    // permanently orange: a seven-colour palette that the eye never actually saw. The last tool is held for
    // a few seconds after it ends, for the words and the colour together, because the agent that just read
    // a file is still working on that file - and a state nobody ever sees is not a state.
    private const int AfterglowMs = 9_000;
    private string? _glowTool, _glowTarget, _glowTurn;
    private long _glowAt;

    private (string? Tool, string? Target) Glow(CcStatus? st)
    {
        var turn = st?.StartedAt;
        if (turn != _glowTurn) { _glowTurn = turn; _glowTool = _glowTarget = null; }   // a new turn starts cold
        if (st?.CurrentTool is { Length: > 0 } cur)
        {
            _glowTool = cur;
            _glowTarget = st.ToolTarget;
            _glowAt = Environment.TickCount64;
            return (cur, _glowTarget);
        }
        // idle, waiting, compacting: nothing is running, so there is nothing to hold over
        if (Shown(st) != "working") { _glowTool = _glowTarget = null; return (null, null); }
        return Environment.TickCount64 - _glowAt <= AfterglowMs ? (_glowTool, _glowTarget) : (null, null);
    }

    // Tool hand-offs inside the current turn, so the voice can notice a loop. Counted off the status the
    // pill already reads per frame rather than plumbed through the hooks, and keyed by the turn's own
    // startedAt, which resets it for free - a new turn always carries a new stamp. Idempotent within a
    // frame: the second call of the same frame sees the same tool name and does not double-count.
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

    // ring mirrors the CLI spinner's colours, except its normal orange → green (orange = icon colour,
    // it would vanish): green = running a tool, yellow = thinking / needs input, blue = compacting,
    // red = error, white = idle
    // The 5-hour window is the number that matters minute to minute; the weekly one stands in when the
    // 5-hour figure hasn't been fetched. 0 when neither is known, which draws nothing rather than
    // implying an empty budget.
    private static float UsageFrac()
        => Limits.FiveHour >= 0 ? Limits.FiveHour : Limits.Week >= 0 ? Limits.Week : 0f;

    // What the ring MEANS. The three states whose colour is itself the message are exempt from any
    // modulation below: an outage, a spent limit, and a running compact each own the pill's colour while
    // they last, and warming them would be reinterpreting a fact.
    private static bool RingIsTheMessage(CcStatus? st)
        => NetMon.ApiDown || NetMon.NetDown || LimitHit || Compacting(st);

    // The ring's colour comes from the same slot the words do (Fx.SlotColor), so a dozen states each get
    // their own hue instead of everything busy being green. waiting_input is the one state that reads as
    // urgent rather than as activity, so it takes the pink that means "this one is addressed to you" -
    // sharing amber with "thinking" hid the only state that is actually waiting on a human.
    private static Color RingBase(CcStatus? st, string? tool)
        => NetMon.ApiDown || NetMon.NetDown ? Red
         : LimitHit ? White                 // out of juice: nothing can run, so the ring reads idle. Amber implied
                                            // activity and left the pill looking busy while it was waiting on a reset.
         : st?.State == "waiting_input" ? Fx.SlotColor("asking")
         : Compacting(st) ? Blue
         : JustCompacted(st) ? Mint         // 20 seconds of "there is room again", which used to look idle
         : Shown(st) == "working" ? Fx.SlotColor(ToolSlot(tool))
         : White;

    // …and what it shows, which is that meaning ridden by the same situation the voice reads: warmer as
    // the context or usage window tightens, a little warmer again on a turn that is dragging, quieter in
    // the small hours. Instance rather than static because the situation includes ToolRuns, which is
    // per-session state.
    private Color RingColor(CcStatus? st)
    {
        var tool = Glow(st).Tool;
        var b = RingBase(st, tool);
        if (RingIsTheMessage(st)) return b;
        // only thinking and idle hand their hue over to pressure: the tool hues are carrying which activity
        // this is, and waiting_input's pink is carrying that it is your turn (see Fx.MoodRing)
        bool hueIsFree = st?.State != "waiting_input"
            && (Shown(st) != "working" || string.IsNullOrEmpty(tool));
        return Fx.MoodRing(b, Mood(st), hueIsFree);
    }

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

    // The part of the idle line that is actually news: offline, api down, out of juice, just compacted.
    // Null when none of it applies, i.e. when the only thing left to say is that nothing is wrong.
    private static string? Trouble(CcStatus? st) =>
        NetMon.NetDown ? Moods.Line("offline")
        : NetMon.ApiDown ? Moods.Line("apiDown")
        : JustCompacted(st) ? Moods.Line("compacted")
        : Limits.FiveHour >= 0.95f && !Limits.ExtraUsageOn && Limits.CreditsUsed >= 0 ? Moods.Line("outOfCredit")
        : null;

    // minimal mood line when nothing is running. The collapsed pill always needs SOMETHING under the icon
    // - a blank pill reads as broken - and this is where the product keeps its voice. The expanded panel
    // has no line of its own: the rings and the key rows are what it says instead.
    private static string IdleMood(CcStatus? st, in MoodContext ctx) => Trouble(st) ?? Moods.Line("idle", ctx);

    private static bool JustCompacted(CcStatus? st) =>
        DateTimeOffset.TryParse(st?.CompactedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
        && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(20);

    // an outage overrides whatever the verb was — even mid-work "writing…" becomes the error
    private static string? OutageText() =>
        NetMon.NetDown ? Moods.Line("netError") : NetMon.ApiDown ? Moods.Line("apiError") : null;

    // the tool maps to a mood slot rather than straight to a string, so the wording can be rewritten
    // without touching this table. An unrecognised tool keeps naming itself - there is no slot for
    // "whatever that was", and inventing one would read worse than the tool's own name.
    /// <summary>
    /// Which mood slot a tool belongs to, or null for one with no vocabulary. This is now the ONE place
    /// the mapping lives: the words come from the slot and so does the ring's colour, so the pill cannot
    /// end up saying "delegating…" in the green of a shell command. Null means the tool names itself.
    /// </summary>
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
        // an mcp tool is Halo asking somebody else's server, which is a state of its own and used to
        // arrive on the pill as raw punctuation
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "consulting",
        _ => null,
    };

    // The situation rides along so a slot with a set for it can switch to it - four minutes of the same
    // word is not information any more, and neither is "reading…" while the context bar sits at 91%.
    private static string ToolVerb(string? tool, in MoodContext ctx)
        => ToolSlot(tool) is { } slot ? Moods.Line(slot, ctx) : Moods.PrettyTool(tool);

    // how long the current turn has been going, as a span rather than the display string
    private static TimeSpan? Running(CcStatus? st) =>
        ParseTime(st?.StartedAt) is { } t ? DateTimeOffset.UtcNow - t : null;

    // how long the current turn (or compact) has been running
    private static string Elapsed(CcStatus? st)
    {
        if ((Shown(st) != "working" && !Compacting(st)) || string.IsNullOrEmpty(st?.StartedAt)) return "";
        if (!DateTimeOffset.TryParse(st.StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return "";
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 1) return "";
        return d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.Seconds}s";
    }
}
