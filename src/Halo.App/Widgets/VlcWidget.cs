using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;

namespace Halo.Widgets;

// Classic VLC (3.x) publishes NO SMTC session — the universal media path never sees it. This widget
// detects VLC by its window title ("movie.mkv - VLC media player") and controls it by injecting its
// default hotkeys (Space, Alt+arrows, V) into the focused window. ponytail: no play-state/position
// (title carries neither) — filename + transport only; upgrade path is VLC's Lua HTTP interface.
internal static class VlcMonitor
{
    public static volatile string? Name;   // playing file name (null = no vlc / idle vlc)
    public static IntPtr Hwnd;
    public static string? ExePath;
    public static int Version;

    private const string Suffix = " - VLC media player";
    private static Timer? _timer;
    private static readonly System.Text.StringBuilder Buf = new(512);

    public static void Poke()
    {
        if (_timer != null) return;
        VlcHttp.EnsureConfigured(); // logon: VLC is usually not running yet → a clean, un-clobbered write
        _timer = new Timer(_ => Scan(), null, 700, 1000);
    }

    private static void Scan()
    {
        try
        {
            string? name = null; IntPtr hwnd = IntPtr.Zero;
            Halo.Interop.Win32.EnumWindows((h, _) =>
            {
                if (!Halo.Interop.Win32.IsWindowVisible(h)) return true;
                int len = Halo.Interop.Win32.GetWindowTextLengthW(h);
                if (len < Suffix.Length || len > 400) return true; // bare "VLC media player" (idle) excluded
                Buf.Clear();
                if (Halo.Interop.Win32.GetWindowTextW(h, Buf, Buf.Capacity) == 0) return true;
                string t = Buf.ToString();
                if (!t.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return true;
                try
                {
                    Halo.Interop.Win32.GetWindowThreadProcessId(h, out uint pid);
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (!p.ProcessName.Equals("vlc", StringComparison.OrdinalIgnoreCase)) return true;
                    if (Name != t) ExePath = p.MainModule?.FileName; // icon only re-resolved on change
                }
                catch { return true; }
                name = Fx.CleanText(t.Substring(0, t.Length - Suffix.Length)); // fold decorative Unicode → real glyphs
                hwnd = h;
                return false;
            }, IntPtr.Zero);

            Hwnd = hwnd;
            bool justClosed = name == null && Name != null;
            if (name != Name) { Name = name; Interlocked.Increment(ref Version); }
            if (name != null) VlcHttp.Poll();                    // refresh rate + play/pause from VLC
            else if (justClosed) VlcHttp.EnsureConfigured();     // VLC just closed → safe to (re)write vlcrc for next launch
        }
        catch { }
    }
}

internal sealed class VlcWidget : IWidget
{
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Orange = Color.FromArgb(255, 136, 0); // VLC cone

    private readonly MediaSessions _sessions;
    private readonly float[] _hover = new float[NBtn];

    // VLC's http status is polled about once a second, so a bar drawn straight off it would step rather than
    // move. Remember when each new reading arrived and add the elapsed time while it is playing.
    private int _seenTime = -1;
    private long _seenAt;
    private bool _scrubbing, _wasDown;
    private float _scrubFrac, _seekHover, _fracShown = -1f;
    private long _dtTick;

    private float Dt()
    {
        long now = Environment.TickCount64;
        float dt = _dtTick == 0 ? 1f / 60f : Math.Clamp((now - _dtTick) / 1000f, 0.001f, 0.1f);
        _dtTick = now;
        return dt;
    }

    private static float Ease(float cur, float target, float dt, float tau)
        => cur + (target - cur) * (1f - MathF.Exp(-dt / MathF.Max(0.0001f, tau)));

    // seconds elapsed right now: the last reading plus however long ago it was read
    private float Elapsed()
    {
        int seen = VlcHttp.Time;
        if (seen < 0) { _seenTime = -1; return -1f; }
        if (seen != _seenTime) { _seenTime = seen; _seenAt = Environment.TickCount64; }
        float extra = VlcHttp.Online && VlcHttp.Playing ? (Environment.TickCount64 - _seenAt) / 1000f : 0f;
        return seen + extra * (float)Math.Max(0.25, VlcHttp.Rate);
    }

    private float Progress()
    {
        int len = VlcHttp.Length;
        float el = Elapsed();
        if (len <= 0 || el < 0f) return -1f;
        return Math.Clamp(el / len, 0f, 1f);
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1
        ? ((int)t.TotalHours) + t.ToString(@"\:mm\:ss") : t.ToString(@"m\:ss");

    private static RectangleF SeekRect(int w) { float tx = 26 + 132 + 22; return new RectangleF(tx, 108, w - tx - 26, 18); }

    public VlcWidget(MediaSessions sessions)
    {
        _sessions = sessions;
        VlcMonitor.Poke();
    }

    // yield to the real SMTC widget if a VLC build that publishes one is running (Store/4.x)
    private bool SmtcHasVlc()
    {
        for (int i = 0; i < MediaSessions.MaxSlots; i++)
            if (_sessions.SlotApp(i).Contains("vlc")) return true;
        return false;
    }

    public string Icon => "";
    public Bitmap? IconImage => AppIcon.ForAumid(VlcMonitor.ExePath);
    public bool IsActive => VlcMonitor.Name != null && !SmtcHasVlc();
    // repaint when rate / play-state / subtitle-state change too (VlcHttp.Poll refreshes them ~1×/s)
    public int Version => VlcMonitor.Version + (VlcHttp.Online
        ? (int)(VlcHttp.Rate * 100) + (VlcHttp.Playing ? 1 : 0) + (VlcHttp.SubsOn ? 2 : 0) : 0);
    public Color? Ring => IsActive ? Orange : null;
    // same as the media widget: the ring around the icon is how far through the file you are
    public float RingProgress => IsActive ? Progress() : -1f;

    // [−10s] [play/pause] [+10s] [speed] [CC]. Preferred path = VLC's http interface: exact rate, real
    // play/pause state, no desync (see VlcHttp). When it's not up yet (VLC not restarted since Halo enabled
    // it) we fall back to VLC's default hotkeys: Alt+Left / Space / Alt+Right / =]  / V.
    private static readonly double[] SpeedPresets = { 1.0, 1.25, 1.5, 2.0 };
    // hotkey fallback only: reset to 1.0 (=) then tap ] (fine, ×1.1) k times. Real rates land at
    // 1.00/1.21/1.46/1.95, so the labels are honest about it (no exact-rate promise without http).
    private static readonly (string label, int taps)[] Speeds = { ("1×", 0), ("1.2×", 2), ("1.45×", 4), ("1.95×", 7) };
    private int _speedIdx;
    private const int NBtn = 5;
    private static RectangleF[] BtnRects(int w, int h)
    {
        const float size = 40, gap = 14;
        float colL = 26 + 132 + 22, cx = (colL + (w - 26)) / 2f, total = NBtn * size + (NBtn - 1) * gap, x0 = cx - total / 2f;
        var r = new RectangleF[NBtn];
        for (int i = 0; i < NBtn; i++) r[i] = new RectangleF(x0 + i * (size + gap), 158, size, size);
        return r;
    }

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
    {
        var r = BtnRects(w, h);
        var seek = SeekRect(w);
        var list = new List<(RectangleF, Action<PointF>)>();
        // http only: without it there is no position to seek to and no length to scale by, and a bar that
        // cannot be trusted is worse than no bar
        if (VlcHttp.Online && VlcHttp.Length > 0)
            list.Add((seek, pt => VlcHttp.SeekTo((pt.X - seek.X) / Math.Max(1f, seek.Width))));
        list.AddRange(new (RectangleF, Action<PointF>)[]
        {
            (r[0], _ => Seek(-10)),                                  // −10s  (http seek, or Alt+Left)
            (r[1], _ => Play()),                                     // play/pause (http, or Space)
            (r[2], _ => Seek(10)),                                   // +10s  (http seek, or Alt+Right)
            (r[3], _ => CycleSpeed()),                               // cycle speed (http exact, or =]×k)
            (r[4], _ => Subtitle()),                                 // cycle subtitles (http key action, or V hotkey)
        });
        return list;
    }

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        if (fade <= 0.01f) return;
        string? name = VlcMonitor.Name;
        if (name == null) return;
        var icon = IconImage;

        const float artX = 26, artY = 26, artSize = 132;
        Fx.Glow(g, w, h, fade, artX + artSize / 2f, artY + artSize / 2f, w * 0.85f, h * 1.2f, 34, Orange);
        using (var path = Fx.Rounded(new RectangleF(artX, artY, artSize, artSize), 14f))
        {
            if (icon != null)
            {
                g.SetClip(path);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float inset = artSize * 0.14f; // cone breathes inside the tile
                g.DrawImage(icon, artX + inset, artY + inset, artSize - inset * 2, artSize - inset * 2);
                g.ResetClip();
            }
            else { using var b = new SolidBrush(Mul(Color.FromArgb(40, 255, 255, 255), fade)); g.FillPath(b, path); }
        }

        float tx = artX + artSize + 22, tw = w - tx - 26;
        using var titleF = new Font("Segoe UI Semibold", 21f, GraphicsUnit.Pixel);
        using var bodyF = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using (var tb = new SolidBrush(Mul(White, fade)))
        using (var sf = new StringFormat(StringFormat.GenericTypographic)
        { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap | (Fx.IsRtl(name) ? StringFormatFlags.DirectionRightToLeft : 0) })
            g.DrawString(name, titleF, tb, new RectangleF(tx, 40, tw, 30), sf);
        // "VLC media player" said nothing the cone in the tile does not already say. VLC, unlike SMTC, hands
        // over the REAL stream resolution, so that is preferred over whatever the filename claims; the rest
        // (publisher, source, size) comes from the same parsers the media panel uses.
        string? res = VlcHttp.Online ? VlcHttp.Resolution : null;   // MetaLine labels it
        string info = MediaWidget.MetaLine(name, null, MediaFileInfo.Size(name, VlcMonitor.Poke) is { } sz
            ? MediaFileInfo.Human(sz) : null, res);
        using (var lb = new SolidBrush(Mul(Dim, fade)))
            g.DrawString(info, bodyF, lb, tx, 76);

        // ── seek bar: same behaviour as the media panel's, including the press-and-drag that follows the
        // cursor and commits once on release. VLC had no bar at all before, only ±10s buttons.
        float dt = Dt();
        var seek = SeekRect(w);
        float frac = Progress();
        if (frac >= 0f)
        {
            if (_fracShown < 0f) _fracShown = frac;
            var hit = seek; hit.Inflate(6f, 10f);
            bool on = WidgetInput.Over && hit.Contains(WidgetInput.Mouse);
            if (WidgetInput.Down && !_wasDown && on && VlcHttp.Online) _scrubbing = true;
            if (_scrubbing)
            {
                _scrubFrac = Math.Clamp((WidgetInput.Mouse.X - seek.X) / Math.Max(1f, seek.Width), 0f, 1f);
                if (!WidgetInput.Down) { VlcHttp.SeekTo(_scrubFrac); _scrubbing = false; _fracShown = _scrubFrac; }
            }
            _wasDown = WidgetInput.Down;
            _seekHover = Ease(_seekHover, _scrubbing ? 1f : 0f, dt, 0.07f);
            _fracShown = _scrubbing ? _scrubFrac : Ease(_fracShown, frac, dt, 0.10f);

            const float barCy = 118.5f, bhRest = 5f;
            float bh = bhRest * (1f + 2f * _seekHover);
            float by = barCy - bh / 2f;
            using (var tb2 = new SolidBrush(Mul(Color.FromArgb(46, 255, 255, 255), fade)))
                g.FillRectangle(tb2, tx, by, tw, bh);
            if (_fracShown > 0f)
                using (var fb2 = new SolidBrush(Mul(White, fade)))
                    g.FillRectangle(fb2, tx, by, tw * _fracShown, bh);

            int len = VlcHttp.Length;
            using var timeF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
            using var eb = new SolidBrush(Mul(Dim, fade));
            float ty = barCy + bh / 2f + 3f;
            var shown = TimeSpan.FromSeconds(_scrubbing ? _scrubFrac * len : Math.Max(0f, Elapsed()));
            g.DrawString(Fmt(shown), timeF, eb, tx, ty);
            var total = Fmt(TimeSpan.FromSeconds(len));
            var ts = g.MeasureString(total, timeF);
            g.DrawString(total, timeF, eb, tx + tw - ts.Width, ty);
        }
        else _wasDown = WidgetInput.Down;

        // transport chips (same glass look as the media widget)
        var rects = BtnRects(w, h);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            var hit = r; hit.Inflate(4f, 4f);
            bool hov = WidgetInput.Over && hit.Contains(WidgetInput.Mouse);
            _hover[i] += ((hov ? 1f : 0f) - _hover[i]) * 0.35f;
            float t = _hover[i], sc = 1f + 0.09f * t, d = r.Width * sc;
            var rr = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
            if (i != 4) // CC is a bare mark, no glass chip
            {
                using (var fb = new SolidBrush(Mul(Color.FromArgb((int)(15 + 19 * t), 255, 255, 255), fade)))
                    g.FillEllipse(fb, rr);
                using (var pen = new Pen(Mul(Color.FromArgb((int)(34 + 30 * t), 255, 255, 255), fade), 1f))
                    g.DrawEllipse(pen, rr);
            }
            float a = fade * (0.8f + 0.2f * t);
            switch (i)
            {
                case 0: Fx.DrawSeekArrow(g, rr, forward: false, a); break;
                // http knows the real transport state → show pause when playing; offline shows a bare play mark
                case 1:
                    bool showPause = VlcHttp.Online && VlcHttp.Playing;
                    DrawGlyphPath(g, rr, ((char)(showPause ? 0xE769 : 0xE768)).ToString(), 22f, a, showPause ? 0f : 1.5f);
                    break;
                case 2: Fx.DrawSeekArrow(g, rr, forward: true, a); break;
                case 3: DrawSpeedLabel(g, rr, SpeedLabel(), a); break;
                // subtitles: bright CC when on; dimmed CC with a slash when off (offline = state unknown → bright)
                case 4:
                    bool subsOff = VlcHttp.Online && !VlcHttp.SubsOn;
                    Fx.DrawCcMark(g, rr, a * (subsOff ? 0.32f : 1f));
                    if (subsOff) DrawSubOffSlash(g, rr, a);
                    break;
            }
        }
    }

    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        string? name = VlcMonitor.Name;
        if (name == null) return;
        float sz = h - 14f, x = 9, y = (h - sz) / 2f;
        float prog = Progress();
        if (prog >= 0f) Fx.PillBar(g, w, h, fade, prog, Orange, 0.34f);
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 30, Orange);
        var icon = IconImage;
        if (icon != null)
        {
            using var path = Fx.Rounded(new RectangleF(x, y, sz, sz), sz * 0.28f);
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, x + 2, y + 2, sz - 4, sz - 4);
            g.ResetClip();
        }
        // no ring around the icon: the pill's background already carries the same fraction
        using var f = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, fade));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap | (Fx.IsRtl(name) ? StringFormatFlags.DirectionRightToLeft : 0), LineAlignment = StringAlignment.Center };
        g.DrawString(name, f, b, new RectangleF(x + sz + 10, 0, w - (x + sz + 10) - 12, h), sf);
    }

    // label the actual VLC rate when http is live (never lies); fall back to the honest hotkey-step label
    private string SpeedLabel() => VlcHttp.Online
        ? VlcHttp.Rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×"
        : Speeds[_speedIdx].label;

    private static void Seek(int seconds)
    {
        if (VlcHttp.Online) VlcHttp.Seek(seconds);
        else KeyInject.Send(VlcMonitor.Hwnd, (byte)(seconds < 0 ? 0x25 : 0x27), alt: true); // Alt+Left / Alt+Right
    }

    private static void Play()
    {
        if (VlcHttp.Online) VlcHttp.TogglePlay();
        else KeyInject.Send(VlcMonitor.Hwnd, 0x20); // Space
    }

    private static void Subtitle()
    {
        if (VlcHttp.Online) VlcHttp.CycleSubtitle();
        else KeyInject.Send(VlcMonitor.Hwnd, (byte)'V');
    }

    private void CycleSpeed()
    {
        if (VlcHttp.Online) { VlcHttp.SetRate(VlcHttp.NextPreset(VlcHttp.Rate, SpeedPresets)); return; }
        // hotkey fallback: reset to 1.0 (=) then tap ] k times — self-syncs even if speed was changed in VLC
        _speedIdx = (_speedIdx + 1) % Speeds.Length;
        var seq = new byte[1 + Speeds[_speedIdx].taps];
        seq[0] = 0xBB;                                        // = : back to 1.0× first
        for (int i = 1; i < seq.Length; i++) seq[i] = 0xDD;   // ] = faster (fine, ×1.1)
        KeyInject.SendSeq(VlcMonitor.Hwnd, seq);              // one ordered burst — parallel Sends raced
    }

    // diagonal strike across the CC mark = subtitles off
    private static void DrawSubOffSlash(Graphics g, RectangleF r, float a)
    {
        float pad = r.Width * 0.14f;
        using var pen = new Pen(Mul(White, a * 0.9f), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, r.Left + pad, r.Bottom - pad, r.Right - pad, r.Top + pad);
    }

    private static void DrawSpeedLabel(Graphics g, RectangleF r, string label, float a)
    {
        using var f = new Font("Segoe UI Semibold", 13.5f, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, a * 0.92f));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, f, b, r, sf);
    }

    private static readonly FontFamily Fluent = new("Segoe Fluent Icons");
    private static void DrawGlyphPath(Graphics g, RectangleF r, string glyph, float px, float fade, float dx = 0)
    {
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(glyph, Fluent, (int)FontStyle.Regular, px, PointF.Empty, sf);
        path.Flatten();
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        using var m = new Matrix();
        m.Translate(MathF.Round(r.X + (r.Width - b.Width) / 2f - b.X + dx),
                    MathF.Round(r.Y + (r.Height - b.Height) / 2f - b.Y));
        path.Transform(m);
        using var br = new SolidBrush(Mul(White, fade * 0.92f));
        g.FillPath(br, path);
    }

    private static Color Mul(Color c, float a) => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);
}
