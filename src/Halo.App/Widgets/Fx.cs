using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace Halo.Widgets;

// shared visual effects: icon-accent extraction + the soft accent glow used across widgets/circles
internal static class Fx
{
    public static readonly Color White = Color.FromArgb(238, 255, 255, 255);

    private static readonly ConditionalWeakTable<Bitmap, object> AccentCache = new();

    // cached accent of an icon bitmap; near-white/grey icons yield White (callers skip the glow)
    public static Color AccentOf(Bitmap? icon)
    {
        if (icon is null) return White;
        if (AccentCache.TryGetValue(icon, out var cached)) return (Color)cached;
        var accent = Accent(icon);
        AccentCache.AddOrUpdate(icon, accent);
        return accent;
    }

    // dithered radial falloff baked once — a live PathGradientBrush at alpha ~34 quantizes
    // into visible rings ("پیکسلی"); per-pixel noise breaks the bands
    private static readonly Bitmap GlowTex = BuildGlowTex();

    private static Bitmap BuildGlowTex()
    {
        const int n = 128;
        // premultiplied (white premul by alpha = alpha in all channels) — GDI+ filtering a
        // non-premul source onto the premul layered-window surface sprays white garbage blocks
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var bytes = new byte[data.Stride * n];
        var rnd = new Random(1);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x - n / 2f) / (n / 2f), dy = (y - n / 2f) / (n / 2f);
                float t = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy));
                float a = MathF.Pow(1f - t, 1.8f) * 255f + rnd.Next(-5, 6);
                int i = y * data.Stride + x * 4;
                byte av = (byte)Math.Clamp((int)a, 0, 255);
                bytes[i] = bytes[i + 1] = bytes[i + 2] = av;
                bytes[i + 3] = av;
            }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    // very soft radial wash of the accent, clipped to the pill shape
    // (flat top flush to the screen edge + rounded bottom — same as LayeredNotch.PillPath;
    // an all-corners clip leaves a dark crescent at the top corners)
    public static void Glow(Graphics g, int w, int h, float fade, float cx, float cy,
        float rx, float ry, int alpha, Color accent)
    {
        if (accent == White || fade <= 0.01f) return;
        using var clip = PillClip(w, h);
        var old = g.Clip;
        g.SetClip(clip);
        var oldInterp = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix
        {
            Matrix00 = accent.R / 255f, // tint the white texture to the accent
            Matrix11 = accent.G / 255f,
            Matrix22 = accent.B / 255f,
            Matrix33 = alpha * fade / 255f,
        });
        ia.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(GlowTex, new Rectangle((int)(cx - rx), (int)(cy - ry), (int)(rx * 2), (int)(ry * 2)),
            0, 0, GlowTex.Width, GlowTex.Height, GraphicsUnit.Pixel, ia);
        g.InterpolationMode = oldInterp;
        g.Clip = old;
    }

    // flat top flush to the screen edge + rounded bottom (matches LayeredNotch.PillPath)
    private static GraphicsPath PillClip(int w, int h)
    {
        float r = Math.Min(h / 2f, 30f), d = r * 2;
        var p = new GraphicsPath();
        p.AddLine(0, 0, w, 0);
        p.AddArc(w - d, h - d, d, d, 0, 90);
        p.AddArc(0, h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // pick a vivid, mid-bright colour from the art (Apple-style accent), lifted to read on dark.
    public static Color Accent(Bitmap art)
    {
        try
        {
            using var small = new Bitmap(12, 12, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(art, 0, 0, 12, 12);
            }
            float best = -1f; Color pick = White;
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++)
                {
                    var p = small.GetPixel(x, y);
                    if (p.A < 128) continue; // transparent padding around logo marks
                    RgbToHsv(p, out _, out float s, out float v);
                    if (v < 0.2f || v > 0.98f) continue;
                    float score = s * (v < 0.85f ? v : 1.7f - v);
                    if (score > best) { best = score; pick = p; }
                }
            if (best <= 0.05f) return White; // grey/white icon → no usable accent
            RgbToHsv(pick, out float ph, out float ps, out float pv);
            return HsvToRgb(ph, Math.Min(1f, ps * 1.1f), Math.Max(pv, 0.85f));
        }
        catch { return White; }
    }

    public static void RgbToHsv(Color c, out float h, out float s, out float v)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max; s = max <= 0f ? 0f : d / max; h = 0f;
        if (d > 0f)
        {
            if (max == r) h = (g - b) / d % 6f;
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h *= 60f; if (h < 0f) h += 360f;
        }
    }

    public static Color HsvToRgb(float h, float s, float v)
    {
        float c = v * s, x = c * (1f - Math.Abs(h / 60f % 2f - 1f)), m = v - c;
        float r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromArgb(255, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
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
}
