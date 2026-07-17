using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Halo.Widgets;

// Now Playing via system media transport controls (Spotify, browsers, any player).
// All WinRT callbacks run off the UI thread: they only touch the locked snapshot + bump
// _version. GDI (art decode + draw) happens on the UI thread in DrawContent.
internal sealed class MediaWidget : IWidget
{
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Track = Color.FromArgb(46, 255, 255, 255);

    private readonly object _lock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;

    // snapshot (guarded by _lock)
    private string? _title, _artist, _trackKey, _appId;
    private bool _playing;
    private byte[]? _thumb;
    private TimeSpan _pos, _end;
    private DateTime _posAt;
    private int _version;

    // UI-thread-only album-art cache
    private string? _artKey;
    private Bitmap? _art;
    private Color _accent = White;

    public MediaWidget() => _ = InitAsync();

    public string Icon => "\uE768"; // Segoe MDL2 Play — ponytail: glyph, not album art (menu draws glyphs only)

    public bool IsActive { get { lock (_lock) { return _title != null; } } }
    public int Version { get { lock (_lock) { return _version; } } }

    // circle / dropdown = the real app icon (Spotify, Chrome, VLC...), falling back to album art
    public Bitmap? IconImage
    {
        get
        {
            string? id; lock (_lock) { id = _appId; }
            var app = AppIcon.ForAumid(id);
            if (app != null) return app;
            EnsureArt();
            return _art;
        }
    }

    // decode _thumb -> _art once per track; UI-thread only (DrawContent + IconImage both call it).
    // also derive the accent colour (Apple-style tint from the art) for the equalizer.
    private void EnsureArt()
    {
        byte[]? thumb; string? key;
        lock (_lock) { thumb = _thumb; key = _trackKey; }
        if (key != _artKey)
        {
            _art?.Dispose();
            _art = Decode(thumb);
            _artKey = key;
            _accent = _art != null ? Fx.Accent(_art) : White;
            _palette = Palette(_accent);
        }
    }

    private async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, __) => HookCurrent();
            HookCurrent();
        }
        catch { }
    }

    // ponytail: old sessions keep their handlers until GC drops them; a change is rare, so no unhook
    private void HookCurrent()
    {
        try
        {
            var s = _mgr?.GetCurrentSession();
            lock (_lock) { _session = s; _appId = s?.SourceAppUserModelId; }
            if (s == null) { Clear(); return; }
            s.MediaPropertiesChanged += (_, __) => RefreshProps(s);
            s.PlaybackInfoChanged += (_, __) => RefreshPlayback(s);
            s.TimelinePropertiesChanged += (_, __) => RefreshTimeline(s);
            RefreshProps(s);
            RefreshPlayback(s);
            RefreshTimeline(s);
        }
        catch { }
    }

    private async void RefreshProps(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            string title = props.Title ?? "";
            string artist = props.Artist ?? "";
            string key = title + "" + artist;
            byte[]? thumb = props.Thumbnail != null ? await ReadStream(props.Thumbnail) : null;
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return; // stale session
                bool trackChanged = key != _trackKey;
                _title = title.Length > 0 ? title : (artist.Length > 0 ? artist : null);
                _artist = artist;
                _trackKey = key;
                if (thumb != null || trackChanged) _thumb = thumb;
                _version++;
            }
        }
        catch { }
    }

    private void RefreshPlayback(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var info = s.GetPlaybackInfo();
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return;
                _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _version++;
            }
        }
        catch { }
    }

    private void RefreshTimeline(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var t = s.GetTimelineProperties();
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return;
                _pos = t.Position;
                _end = t.EndTime;
                _posAt = DateTime.UtcNow;
                _version++;
            }
        }
        catch { }
    }

    private void Clear()
    {
        lock (_lock)
        {
            if (_title == null) return;
            _title = _artist = _trackKey = null;
            _thumb = null;
            _pos = _end = TimeSpan.Zero;
            _version++;
        }
    }

    private static async Task<byte[]?> ReadStream(IRandomAccessStreamReference r)
    {
        try
        {
            using var s = await r.OpenReadAsync();
            uint size = (uint)s.Size;
            if (size == 0) return null;
            using var reader = new DataReader(s);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    private GlobalSystemMediaTransportControlsSession? Cur() { lock (_lock) { return _session; } }
    private void Toggle() { var s = Cur(); if (s != null) _ = s.TryTogglePlayPauseAsync(); }
    private void Prev() { var s = Cur(); if (s != null) _ = s.TrySkipPreviousAsync(); }
    private void Next() { var s = Cur(); if (s != null) _ = s.TrySkipNextAsync(); }
    private void SetVol(float f) { _meter.SetVolume(f); Bump(); }
    private void Mute() { _meter.ToggleMute(); Bump(); }
    private void Bump() { lock (_lock) { _version++; } }

    private void Seek(float f)
    {
        var s = Cur();
        TimeSpan end; lock (_lock) { end = _end; }
        if (s == null || end <= TimeSpan.Zero) return;
        try { _ = s.TryChangePlaybackPositionAsync((long)(Math.Clamp(f, 0f, 1f) * end.Ticks)); } catch { }
    }

    private static (RectangleF bar, RectangleF mute) VolLayout(int w) => (new RectangleF(62, 178, 96, 20), new RectangleF(24, 172, 32, 32));
    private static RectangleF SeekRect(int w) { float tx = 180; return new RectangleF(tx, 108, w - tx - 26, 18); }

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
    {
        var r = BtnRects(w, h);
        var (vbar, mute) = VolLayout(w);
        var seek = SeekRect(w);
        return new (RectangleF, Action<PointF>)[]
        {
            (seek, pt => Seek((pt.X - seek.X) / seek.Width)),
            (vbar, pt => SetVol((pt.X - vbar.X) / vbar.Width)),
            (mute, _ => Mute()),
            (r[0], _ => Prev()),
            (r[1], _ => Toggle()),
            (r[2], _ => Next()),
        };
    }

    private static RectangleF[] BtnRects(int w, int h)
    {
        const float artX = 26, artSize = 132, size = 40, gap = 18;
        float colL = artX + artSize + 22, colR = w - 26;
        float cx = (colL + colR) / 2f, total = 3 * size + 2 * gap, x0 = cx - total / 2f, y = 158;
        return new[]
        {
            new RectangleF(x0, y, size, size),
            new RectangleF(x0 + size + gap, y, size, size),
            new RectangleF(x0 + 2 * (size + gap), y, size, size),
        };
    }

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        if (fade <= 0.01f) return;
        string? title, artist; bool playing; TimeSpan pos, end; DateTime posAt;
        lock (_lock)
        {
            title = _title; artist = _artist; playing = _playing;
            pos = _pos; end = _end; posAt = _posAt;
        }
        if (title == null) return;

        EnsureArt();

        const float artX = 26, artY = 26, artSize = 132;
        Fx.Glow(g, w, h, fade, artX + artSize / 2f, artY + artSize / 2f, w * 0.85f, h * 1.2f, 38, _accent);
        DrawArt(g, artX, artY, artSize, fade);

        float tx = artX + artSize + 22, tw = w - tx - 26;
        using var titleF = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var bodyF = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var timeF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using (var tb = new SolidBrush(Mul(White, fade)))
            DrawLine(g, title, titleF, tb, tx, 34, tw);
        if (!string.IsNullOrEmpty(artist))
            using (var ab = new SolidBrush(Mul(Dim, fade)))
                DrawLine(g, artist, bodyF, ab, tx, 66, tw);

        // seek bar (extrapolate while playing so it advances between events)
        var now = playing ? pos + (DateTime.UtcNow - posAt) : pos;
        double frac = end > TimeSpan.Zero ? Math.Clamp(now / end, 0, 1) : 0;
        float by = 116, bh = 5;
        Fill(g, tx, by, tw, bh, Mul(Track, fade));
        if (frac > 0) Fill(g, tx, by, (float)(tw * frac), bh, Mul(White, fade));
        if (end > TimeSpan.Zero)
        {
            using var eb = new SolidBrush(Mul(Dim, fade));
            g.DrawString(Fmt(now), timeF, eb, tx, by + 8);
            var ts = g.MeasureString(Fmt(end), timeF);
            g.DrawString(Fmt(end), timeF, eb, tx + tw - ts.Width, by + 8);
        }

        // volume (left column, under the art): soft glass mute chip + a bar that breathes on hover
        var (vbar, mute) = VolLayout(w);
        bool muted = _meter.Muted();
        float vol = muted ? 0f : _meter.Volume();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var volHit = RectangleF.Union(vbar, mute); volHit.Inflate(4f, 6f);
        bool vHov = WidgetInput.Over && volHit.Contains(WidgetInput.Mouse);
        _volHover += ((vHov ? 1f : 0f) - _volHover) * 0.35f;
        float vt = _volHover;
        using (var fb = new SolidBrush(Mul(Color.FromArgb((int)(13 + 16 * vt), 255, 255, 255), fade)))
            g.FillEllipse(fb, mute);
        using (var pen = new Pen(Mul(Color.FromArgb((int)(28 + 26 * vt), 255, 255, 255), fade), 1f))
            g.DrawEllipse(pen, mute);
        DrawGlyphSoft(g, mute, muted ? "\uE74F" : "\uE767", 16f, muted ? fade * 0.55f : fade * (0.8f + 0.2f * vt));
        float vy = vbar.Y + vbar.Height / 2f, bh2 = 4f + 2f * vt;
        Fill(g, vbar.X, vy - bh2 / 2f, vbar.Width, bh2, Mul(Color.FromArgb(34, 255, 255, 255), fade));
        if (vol > 0)
            Fill(g, vbar.X, vy - bh2 / 2f, vbar.Width * vol, bh2,
                Mul(Color.FromArgb((int)(185 + 45 * vt), 255, 255, 255), fade));

        // transport = glass chips; a small eased grow + brighten on hover (frames ride the
        // mouse-move redraws while the cursor is over the open panel)
        var rects = BtnRects(w, h);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < 3; i++)
        {
            var r = rects[i];
            var hit = r; hit.Inflate(4f, 4f);
            bool hov = WidgetInput.Over && hit.Contains(WidgetInput.Mouse);
            _btnHover[i] += ((hov ? 1f : 0f) - _btnHover[i]) * 0.35f;
            float t = _btnHover[i], sc = 1f + 0.09f * t, d = r.Width * sc;
            var rr = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
            using (var fb = new SolidBrush(Mul(Color.FromArgb((int)(15 + 19 * t), 255, 255, 255), fade)))
                g.FillEllipse(fb, rr);
            using (var pen = new Pen(Mul(Color.FromArgb((int)(34 + 30 * t), 255, 255, 255), fade), 1f))
                g.DrawEllipse(pen, rr);
            string glyph = i == 0 ? "\uE892" : i == 1 ? (playing ? "\uE769" : "\uE768") : "\uE893";
            DrawGlyphSoft(g, rr, glyph, (i == 1 ? 22f : 17f) * sc, fade * (0.8f + 0.2f * t));
        }
    }

    private readonly float[] _btnHover = new float[3];
    private float _volHover;

    private static readonly FontFamily FluentFamily = new("Segoe Fluent Icons");

    // soft + truly centred: outline the glyph as a path and centre its ink bounds in the rect
    // (font-metric centring leaves Fluent glyphs visibly off inside the chips)
    private void DrawGlyphSoft(Graphics g, RectangleF r, string glyph, float px, float fade)
    {
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(glyph, FluentFamily, (int)FontStyle.Regular, px, PointF.Empty, sf);
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        using var m = new Matrix();
        m.Translate(r.X + (r.Width - b.Width) / 2f - b.X, r.Y + (r.Height - b.Height) / 2f - b.Y);
        path.Transform(m);
        using var br = new SolidBrush(Mul(White, fade * 0.92f));
        g.FillPath(br, path);
    }

    private void DrawArt(Graphics g, float x, float y, float size, float fade, float radius = 14f)
    {
        using var path = Rounded(new RectangleF(x, y, size, size), radius);
        if (_art != null)
        {
            // HQ-scale the (often small) thumbnail to a square, then fill the rounded path with it
            // as a texture so the corners are anti-aliased (SetClip gives jagged, "dirty" edges).
            int s = Math.Max(1, (int)Math.Ceiling(size));
            using var scaled = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(scaled))
            {
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.SmoothingMode = SmoothingMode.HighQuality;
                using var ia = new ImageAttributes();
                ia.SetWrapMode(WrapMode.TileFlipXY);            // no edge fringe
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
                int side = Math.Min(_art.Width, _art.Height);   // cover-fit to a centered square
                sg.DrawImage(_art, new Rectangle(0, 0, s, s),
                    (_art.Width - side) / 2, (_art.Height - side) / 2, side, side, GraphicsUnit.Pixel, ia);
            }
            using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
            tb.TranslateTransform(x, y);
            g.FillPath(tb, path);
        }
        else
        {
            using var b = new SolidBrush(Mul(Color.FromArgb(40, 255, 255, 255), fade));
            g.FillPath(b, path);
            DrawGlyph(g, new RectangleF(x, y, size, size), "\uE8D6", size * 0.5f, fade * 0.7f); // MusicInfo
        }
    }

    public bool Animating { get { lock (_lock) { return _title != null && _playing; } } }

    // Collapsed pill = album art on the left + an audio equalizer on the right (Dynamic-Island style).
    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        string? title; bool playing;
        lock (_lock) { title = _title; playing = _playing; }
        if (title == null) return;
        EnsureArt();
        float sz = h - 14f, x = 9, y = (h - sz) / 2f;
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 34, _accent);
        DrawArt(g, x, y, sz, fade, sz * 0.28f);
        DrawEqualizer(g, w - 14f, h / 2f, fade, playing);
    }

    private const int EqBars = 9;
    private readonly AudioMeter _meter = new();
    private readonly float[] _eq = new float[EqBars];
    private float _amp;

    // iOS Dynamic-Island waveform: many thin rounded bars, center-weighted (tall middle, dots at
    // the edges), driven by the real output level, tinted from the art.
    private void DrawEqualizer(Graphics g, float rightX, float cy, float fade, bool playing)
    {
        float peak = playing ? _meter.Peak() : 0f;
        _amp += (Math.Clamp((float)Math.Sqrt(peak) * 1.4f, 0f, 1f) - _amp) * 0.22f; // smoothed level
        const float barW = 2.6f, gap = 2.6f, maxH = 22f, minH = 2.6f;
        double t = Environment.TickCount / 1000.0;
        float totalW = EqBars * barW + (EqBars - 1) * gap;
        float x0 = rightX - totalW;
        for (int i = 0; i < EqBars; i++)
        {
            // center-weight envelope makes it read as a waveform blob, not a flat spectrum
            float env = 0.25f + 0.75f * (float)Math.Sin(Math.PI * (i + 0.5) / EqBars);
            float phase = 0.5f + 0.5f * (float)Math.Sin(t * (1.7 + i * 0.4) + i * 1.9);
            float target = minH + (maxH - minH) * _amp * env * (0.35f + 0.65f * phase);
            _eq[i] += (target - _eq[i]) * (target > _eq[i] ? 0.28f : 0.10f); // gentle rise, slow fall
            float bh = Math.Max(minH, _eq[i]);
            Color col = playing ? PaletteAt((float)i / (EqBars - 1)) : Color.FromArgb(120, 255, 255, 255);
            Fill(g, x0 + i * (barW + gap), cy - bh / 2f, barW, bh, Mul(col, fade));
        }
    }

    // soft multi-hue gradient built from the art accent (a gentle halo, not a harsh rainbow)
    private Color[] _palette = { White, White, White };

    private static Color[] Palette(Color accent)
    {
        Fx.RgbToHsv(accent, out float h, out float s, out float v);
        return new[] { Fx.HsvToRgb((h - 22f + 360f) % 360f, s, v), accent, Fx.HsvToRgb((h + 22f) % 360f, s, v) };
    }

    private Color PaletteAt(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        return f <= 0.5f ? LerpColor(_palette[0], _palette[1], f * 2f) : LerpColor(_palette[1], _palette[2], (f - 0.5f) * 2f);
    }

    private static Color LerpColor(Color a, Color b, float t)
        => Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private void DrawGlyph(Graphics g, RectangleF r, string glyph, float px, float fade)
    {
        using var f = new Font("Segoe Fluent Icons", px, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, fade));
        // centre via StringFormat (MeasureString padding sat glyphs off-centre in the glass chips)
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, f, b, r, sf);
    }

    private static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return new Bitmap(img); // detach from the stream
        }
        catch { return null; }
    }

    // one line, single-line ellipsis, right-aligned + RTL for Persian/Arabic titles
    private static void DrawLine(Graphics g, string text, Font f, Brush b, float x, float y, float w)
    {
        using var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        if (IsRtl(text)) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft; // Near => right edge, ellipsis on the left
        g.DrawString(text, f, b, new RectangleF(x, y, w, f.Height + 4), sf);
    }

    private static bool IsRtl(string s)
    {
        foreach (var c in s)
            if (c >= 0x0590 && c <= 0x08FF) return true; // Hebrew..Arabic Extended
        return false;
    }

    private static string Fmt(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

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
}
