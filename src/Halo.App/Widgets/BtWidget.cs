using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Halo.Widgets;

internal sealed class BtWidget : IWidget
{
    /// <summary>How long after connecting the widget is allowed to grab focus as primary.</summary>
    private const int FlashMs = 6000;

    /// <summary>
    /// A battery reading older than this is not trusted: the device stays on the pill, but
    /// the percentage is withheld rather than shown as a number that may be hours stale.
    /// The source going quiet is a state of its own, not a reason to keep the last value.
    /// </summary>
    private const long StaleAfterMs = 15 * 60 * 1000;

    /// <summary>
    /// A refresh is only worth its cost while the widget is genuinely on screen. Kept short so
    /// that "visible" means now, not "was visible a while ago": an idle tucked pill pays nothing.
    /// </summary>
    private const long DrawnRecentlyMs = 5 * 1000;

    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Track = Color.FromArgb(46, 255, 255, 255);
    private static readonly FontFamily Fluent = new("Segoe Fluent Icons");

    private readonly object _lock = new();
    private string _id = "";
    private string _name = "";
    private int _pct;
    private int _glyph = 0xE702;
    private bool _connected;
    private long _flashUntilMs;
    private long _readAtMs;
    private int _version;
    private float _fillShown = -1f;
    private long _lastDrawnMs;

    /// <summary>
    /// Show <paramref name="id"/> as the featured device. The id is the identity: two devices
    /// reporting the same name are different devices, which is why nothing here matches on name.
    /// <paramref name="flash"/> is false for devices that were already connected at startup —
    /// they are shown, but they do not deserve to grab focus as if they had just arrived.
    /// </summary>
    public void Connect(string id, string name, int pct, bool flash)
    {
        lock (_lock)
        {
            bool isNewDevice = !_connected || !string.Equals(_id, id, StringComparison.Ordinal);
            _id = id;
            _name = name;
            _pct = Math.Clamp(pct, 0, 100);
            _readAtMs = Environment.TickCount64;
            _glyph = GlyphFor(name);
            _connected = true;
            // 0f, not -1f: DrawCollapsed treats a negative as "jump to the final value", and the
            // ring is supposed to grow from empty to the real charge when a device appears.
            if (isNewDevice) _fillShown = 0f;
            _flashUntilMs = isNewDevice && flash ? Environment.TickCount64 + FlashMs : 0;
            _version++;
        }
    }

    /// <summary>Silent battery-level refresh -- no re-flash. Ignored unless it is the featured device.</summary>
    public void UpdateBattery(string id, int pct)
    {
        lock (_lock)
        {
            if (!_connected || !string.Equals(_id, id, StringComparison.Ordinal)) return;
            _pct = Math.Clamp(pct, 0, 100);
            _readAtMs = Environment.TickCount64;
            _version++;
        }
    }

    /// <summary>Clears the pill. Ignored unless <paramref name="id"/> is the device being shown.</summary>
    public void Disconnect(string id)
    {
        lock (_lock)
        {
            if (!_connected || !string.Equals(_id, id, StringComparison.Ordinal)) return;
            _connected = false;
            _flashUntilMs = 0;
            _id = "";
            _version++;
        }
    }

    public bool IsActive { get { lock (_lock) return _connected; } }

    /// <summary>
    /// A connected device is ambient state, not an event: it stays reachable but must not keep
    /// the pill open on an idle desktop. See <see cref="IWidget.CountsAsContent"/>.
    /// </summary>
    public bool CountsAsContent => false;

    /// <summary>The id of the device on screen, or "" when nothing is shown.</summary>
    public string FeaturedId { get { lock (_lock) return _connected ? _id : ""; } }

    /// <summary>True for a short window after a fresh connect -- lets the shell flash it to primary
    /// once. Cleared by Disconnect, so a device that has already gone cannot hold focus.</summary>
    public bool JustConnected
    {
        get { lock (_lock) return _connected && Environment.TickCount64 < _flashUntilMs; }
    }

    /// <summary>Whether the battery reading is recent enough to be worth showing as a number.</summary>
    private bool PctFresh => Environment.TickCount64 - _readAtMs < StaleAfterMs;

    /// <summary>Whether a background refresh is worth its cost right now.</summary>
    public bool WorthRefreshing
    {
        get
        {
            lock (_lock)
                return _connected && Environment.TickCount64 - _lastDrawnMs < DrawnRecentlyMs;
        }
    }

    public int Version { get { lock (_lock) return _version; } }

    /// <summary>
    /// Frames are needed for as long as the ring is still travelling, not for a fixed window:
    /// a refresh that arrives long after connecting still has to animate to the new value, or
    /// the ring and the percentage would disagree until something unrelated forced a redraw.
    /// </summary>
    public bool Animating
    {
        get
        {
            lock (_lock)
                return _connected && (_fillShown < 0f || Math.Abs(_pct / 100f - _fillShown) > 0.004f);
        }
    }

    public string Icon => ((char)0xE702).ToString();

    private static int GlyphFor(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("airpod") || n.Contains("buds") || n.Contains("headphone") || n.Contains("headset")
            || n.Contains("hands-free") || n.Contains(" hf") || n.StartsWith("wh-") || n.StartsWith("wf-")
            || n.Contains("earbud") || n.Contains("pods")) return 0xE7F6;
        if (n.Contains("controller") || n.Contains("dualsense") || n.Contains("dualshock")
            || n.Contains("xbox") || n.Contains("gamepad")) return 0xE7FC;
        if (n.Contains("keyboard")) return 0xE765;
        if (n.Contains("mouse")) return 0xE962;
        if (n.Contains("speaker") || n.Contains("soundbar") || n.Contains("jbl")
            || n.Contains("boom") || n.Contains("sound")) return 0xE7F5;
        if (n.Contains("watch") || n.Contains("band")) return 0xEC92;
        return 0xE8EA;
    }

    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        int pct; int glyph; bool fresh;
        lock (_lock) { pct = _pct; glyph = _glyph; fresh = PctFresh; }

        // The pill tucks into a 96x12 tab and GDI+ rejects an arc once the radius reaches zero,
        // which threw on every frame while a device was shown. Nothing legible fits here anyway.
        // Note this returns *before* marking the widget as drawn: a tucked pill is not something
        // anyone is looking at, and it must not keep the battery refresh alive.
        if (h < 16) return;

        lock (_lock) _lastDrawnMs = Environment.TickCount64;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float target = pct / 100f;
        _fillShown = _fillShown < 0 ? target : _fillShown + (target - _fillShown) * 0.16f;
        if (Math.Abs(target - _fillShown) < 0.004f) _fillShown = target;
        float fill = Math.Clamp(_fillShown, 0f, 1f);
        // A stale reading is drawn neutral rather than in the charge colour: unknown, not empty.
        Color ringCol = fresh ? Charge(fill) : Dim;

        float sz = h - 12f, x = 9f, cy = h / 2f, cx = x + sz / 2f;
        Fx.Glow(g, w, h, fade, cx, cy, w * 0.6f, h * 2.0f, 34, ringCol);

        float ir = sz / 2f - 4.5f;
        using (var disc = new SolidBrush(Mul(Color.FromArgb(20, 255, 255, 255), fade)))
            g.FillEllipse(disc, cx - ir, cy - ir, ir * 2, ir * 2);
        DrawGlyph(g, new RectangleF(cx - ir, cy - ir, ir * 2, ir * 2), glyph, fade, White);

        float rr = sz / 2f - 1f;
        using (var tp = new Pen(Mul(Track, fade), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(tp, cx - rr, cy - rr, rr * 2, rr * 2, 0, 360);
        if (fill > 0.001f)
            using (var fp = new Pen(Mul(ringCol, fade), 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(fp, cx - rr, cy - rr, rr * 2, rr * 2, -90, 360f * fill);

        using var pf = new Font("Segoe UI Semibold", h * 0.42f, GraphicsUnit.Pixel);
        using var pb = new SolidBrush(Mul(White, fade));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString(fresh ? $"{pct}%" : "", pf, pb, new RectangleF(cx + sz, 0, w - (cx + sz) - 14, h), sf);
    }

    private static Color Charge(float fill)
        => Fx.HsvToRgb(Math.Clamp(fill, 0f, 1f) * 120f, 0.68f, 0.96f);

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        if (fade <= 0.01f) return;
        int pct, glyph; string name; bool fresh;
        lock (_lock)
        {
            pct = _pct; glyph = _glyph; name = _name; fresh = PctFresh;
            _lastDrawnMs = Environment.TickCount64;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float fill = pct / 100f;
        Color ringCol = fresh ? Charge(fill) : Dim;

        float cx = 70, cy = h / 2f, rr = 44;
        Fx.Glow(g, w, h, fade, cx, cy, w * 0.7f, h * 1.2f, 36, ringCol);
        using (var disc = new SolidBrush(Mul(Color.FromArgb(20, 255, 255, 255), fade)))
            g.FillEllipse(disc, cx - rr + 8, cy - rr + 8, (rr - 8) * 2, (rr - 8) * 2);
        DrawGlyph(g, new RectangleF(cx - rr + 8, cy - rr + 8, (rr - 8) * 2, (rr - 8) * 2), glyph, fade, White);
        using (var tp = new Pen(Mul(Track, fade), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(tp, cx - rr, cy - rr, rr * 2, rr * 2, 0, 360);
        using (var fp = new Pen(Mul(ringCol, fade), 4.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(fp, cx - rr, cy - rr, rr * 2, rr * 2, -90, 360f * fill);

        float tx = cx + rr + 22;
        using var nf = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var bf = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using (var nb = new SolidBrush(Mul(White, fade)))
            g.DrawString(name, nf, nb, tx, cy - 26);
        using (var bb = new SolidBrush(Mul(Dim, fade)))
            g.DrawString(fresh ? $"{pct}% battery" : "battery unknown", bf, bb, tx, cy + 4);
    }

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
        => Array.Empty<(RectangleF, Action<PointF>)>();

    private static readonly Dictionary<int, Bitmap> _glyphCache = new();
    private static Bitmap GlyphBitmap(int cp)
    {
        lock (_glyphCache)
        {
            if (_glyphCache.TryGetValue(cp, out var cached)) return cached;
            var b = RenderTight(cp);
            _glyphCache[cp] = b;
            return b;
        }
    }

    private static Bitmap RenderTight(int cp)
    {
        const int N = 128;
        var full = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var f = new Font(Fluent, N * 0.7f, GraphicsUnit.Pixel);
            using var br = new SolidBrush(Color.White);
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            sf.FormatFlags |= StringFormatFlags.NoClip;
            g.DrawString(((char)cp).ToString(), f, br, new RectangleF(0, 0, N, N), sf);
        }
        var ink = InkBounds(full);
        if (ink.Width <= 0 || ink.Height <= 0) return full;
        var tight = full.Clone(ink, PixelFormat.Format32bppArgb);
        full.Dispose();
        return tight;
    }

    private static Rectangle InkBounds(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * b.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (buf[y * stride + x * 4 + 3] > 16)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            return maxX < minX ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally { b.UnlockBits(data); }
    }

    private static void DrawGlyph(Graphics g, RectangleF r, int cp, float fade, Color tint)
    {
        var gb = GlyphBitmap(cp);
        float target = r.Height * 0.58f;
        float scale = target / Math.Max(gb.Width, gb.Height);
        float dw = gb.Width * scale, dh = gb.Height * scale;
        var dst = new RectangleF(r.X + (r.Width - dw) / 2f, r.Y + (r.Height - dh) / 2f, dw, dh);
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix(new[]
        {
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, fade * 0.95f, 0 },
            new float[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, 0, 1 },
        }));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(gb, new[] { dst.Location, new PointF(dst.Right, dst.Y), new PointF(dst.X, dst.Bottom) },
            new RectangleF(0, 0, gb.Width, gb.Height), GraphicsUnit.Pixel, ia);
    }

    private static Color Mul(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);
}
