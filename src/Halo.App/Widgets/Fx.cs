using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace Halo.Widgets;

internal static class Fx
{
    public static readonly Color White = Color.FromArgb(238, 255, 255, 255);

    public const string NetLabel = "net";
    public const string ApiLabel = "api";
    public const string LossLabel = "loss";

    public static string CleanText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        try { return s.IsNormalized(System.Text.NormalizationForm.FormKC) ? s : s.Normalize(System.Text.NormalizationForm.FormKC); }
        catch { return s; }
    }

    public static bool IsRtl(string? s)
    {
        if (s == null) return false;
        foreach (var c in s) if (c >= 0x0590 && c <= 0x08FF) return true;
        return false;
    }

    private static readonly ConditionalWeakTable<Bitmap, object> AccentCache = new();

    public static Color AccentOf(Bitmap? icon)
    {
        if (icon is null) return White;
        if (AccentCache.TryGetValue(icon, out var cached)) return (Color)cached;
        var accent = Accent(icon);
        AccentCache.AddOrUpdate(icon, accent);
        return accent;
    }

    private static readonly Bitmap GlowTex = BuildGlowTex();

    private static Bitmap BuildGlowTex()
    {
        const int n = 128;

        var bmp = new Bitmap(n, n, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var bytes = new byte[data.Stride * n];
        var rnd = new Random(1);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x - n / 2f) / (n / 2f), dy = (y - n / 2f) / (n / 2f);
                float t = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy));

                float f = MathF.Pow(1f - t, 1.8f);
                float a = f * (255f + rnd.Next(-11, 12));
                int i = y * data.Stride + x * 4;
                byte av = (byte)Math.Clamp((int)a, 0, 255);
                bytes[i] = bytes[i + 1] = bytes[i + 2] = av;
                bytes[i + 3] = av;
            }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

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
            Matrix00 = accent.R / 255f,
            Matrix11 = accent.G / 255f,
            Matrix22 = accent.B / 255f,
            Matrix33 = alpha * fade / 255f,
        });

        ia.SetWrapMode(WrapMode.Clamp);
        g.DrawImage(GlowTex, new Rectangle((int)(cx - rx), (int)(cy - ry), (int)(rx * 2), (int)(ry * 2)),
            0, 0, GlowTex.Width, GlowTex.Height, GraphicsUnit.Pixel, ia);
        g.InterpolationMode = oldInterp;
        g.Clip = old;
    }

    public static void PillBar(Graphics g, int w, int h, float fade, float frac, Color accent, float strength)
    {
        if (accent == White || fade <= 0.01f || strength <= 0f) return;
        frac = Math.Clamp(frac, 0f, 1f);

        using var pp = PillPath(w, h, h / 2f, 0.5f);

        RgbToHsv(accent, out float th, out float ts, out float tv);
        var track = HsvToRgb(th, ts * 0.42f, Math.Max(0.16f, tv * 0.34f));

        using (var tb = new SolidBrush(Alpha(track, fade * strength * (0.34f + 0.28f * strength))))
            g.FillPath(tb, pp);
        if (frac <= 0.001f) return;

        float fill = w * frac;
        var solid = Alpha(accent, fade * 0.52f * strength);

        if (fill > 6f)
            Glow(g, w, h, fade, fill * 0.45f, h * 0.44f, Math.Max(fill, h * 1.2f), h * 1.9f,
                 (int)(16 * strength), accent);

        if (frac >= 0.999f) { using (var fb = new SolidBrush(solid)) g.FillPath(fb, pp); }
        else
        {

            float soft = Math.Clamp(2.5f / w, 0.0008f, 0.02f);
            float cut = Math.Clamp(fill / w, soft + 0.0005f, 0.9985f);
            using var lb = new LinearGradientBrush(new RectangleF(0, 0, w, h), solid, Color.FromArgb(0, accent),
                       LinearGradientMode.Horizontal);
            lb.InterpolationColors = new ColorBlend(4)
            {
                Positions = new[] { 0f, cut - soft, cut, 1f },
                Colors = new[] { solid, solid, Color.FromArgb(0, accent), Color.FromArgb(0, accent) },
            };
            g.FillPath(lb, pp);
        }

        if (fill > 4f && strength >= 0.4f)
        {
            using var sheen = new LinearGradientBrush(new RectangleF(0, -0.5f, Math.Max(w, 1), h + 1f),
                Color.White, Color.White, LinearGradientMode.Vertical);
            sheen.InterpolationColors = new ColorBlend(4)
            {
                Positions = new[] { 0f, 0.34f, 0.70f, 1f },
                Colors = new[]
                {
                    Alpha(Color.White, fade * 0.14f * strength),
                    Alpha(Color.White, fade * 0.05f * strength),
                    Color.FromArgb(0, 255, 255, 255),
                    Alpha(Color.FromArgb(0, 0, 0), fade * 0.10f * strength),
                },
            };
            var oldC = g.Clip;
            g.SetClip(new RectangleF(0, 0, fill, h), CombineMode.Intersect);
            g.FillPath(sheen, pp);
            g.Clip = oldC;
        }

        if (fill > 8f && strength >= 0.5f)
        {
            float lipW = 26f, x0 = Math.Max(0f, fill - lipW);
            using var lip = new LinearGradientBrush(new RectangleF(x0 - 0.5f, 0, (fill - x0) + 1f, h),
                Color.FromArgb(0, accent), Alpha(accent, fade * 0.5f * strength), LinearGradientMode.Horizontal);
            var lipBlend = new ColorBlend(3)
            {
                Positions = new[] { 0f, 0.94f, 1f },
                Colors = new[] { Color.FromArgb(0, accent), Alpha(accent, fade * 0.5f * strength), Color.FromArgb(0, accent) },
            };
            lip.InterpolationColors = lipBlend;
            var old = g.Clip;
            g.SetClip(new RectangleF(x0, 0, fill - x0, h), CombineMode.Intersect);
            g.FillPath(lip, pp);
            g.Clip = old;
        }

        if (fill > 6f)
            Glow(g, w, h, fade, fill, h / 2f, h * 1.0f, h * 1.45f, (int)(26 * strength), accent);
    }

    public static float CenterLift(Font f)
    {
        try
        {
            var ff = f.FontFamily;
            var st = f.Style;
            float em = ff.GetEmHeight(st);
            if (em <= 0) return 0f;
            float line = (ff.GetCellAscent(st) + ff.GetCellDescent(st)) / em;
            float baseline = ff.GetCellAscent(st) / em;
            const float capRatio = 0.70f;
            float visual = baseline - capRatio / 2f;
            return (visual - line / 2f) * f.Size;
        }
        catch { return 0f; }
    }

    private static readonly Dictionary<string, float> _inkOffsets = new();

    public static float InkCentreOffset(Font f, string s)
    {
        if (string.IsNullOrEmpty(s)) return 0f;
        string key = f.FontFamily.Name + "|" + f.Style + "|" + f.Size.ToString("0.##") + "|" + s;
        lock (_inkOffsets)
        {
            if (_inkOffsets.TryGetValue(key, out float v)) return v;
            float off = 0f;
            try
            {
                using var path = new GraphicsPath();
                using var sf = new StringFormat(StringFormat.GenericTypographic);
                path.AddString(s, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, sf);
                var b = path.GetBounds();
                if (b.Height > 0) off = -(b.Top + b.Height / 2f);
            }
            catch { }
            _inkOffsets[key] = off;
            return off;
        }
    }

    public static float CapCentreOffset(Font f) => InkCentreOffset(f, "H");

    public static Color Alpha(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);

    private static GraphicsPath PillClip(int w, int h) => PillPath(w, h, Math.Min(h / 2f, 30f));

    public static GraphicsPath PillPath(int w, int h, float r) => PillPath(w, h, r, 0f);

    public static GraphicsPath PillPath(int w, int h, float r, float inset)
    {
        float x0 = inset, y0 = inset, x1 = w - inset, y1 = h - inset;
        float d = Math.Min(r, Math.Min(x1 - x0, y1 - y0) / 2f) * 2f;
        var p = new GraphicsPath();
        p.AddLine(x0, y0, x1, y0);
        p.AddArc(x1 - d, y1 - d, d, d, 0, 90);
        p.AddArc(x0, y1 - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

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
                    if (p.A < 128) continue;
                    RgbToHsv(p, out _, out float s, out float v);
                    if (v < 0.2f || v > 0.98f) continue;
                    float score = s * (v < 0.85f ? v : 1.7f - v);
                    if (score > best) { best = score; pick = p; }
                }
            if (best <= 0.05f) return White;
            RgbToHsv(pick, out float ph, out float ps, out float pv);
            return HsvToRgb(ph, Math.Min(1f, ps * 1.1f), Math.Max(pv, 0.85f));
        }
        catch { return White; }
    }

    public static Bitmap Badge(Bitmap icon, char ch)
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
        g.DrawString(ch.ToString(), f, wb, new RectangleF(x, y - d * 0.02f, d, d), sf);
        return b;
    }

    public static Color Shade(Color c, int step)
    {
        if (step <= 0) return c;
        RgbToHsv(c, out float h, out float s, out float v);
        return HsvToRgb(h, Math.Min(1f, s * (1f + 0.22f * step)), Math.Max(0.35f, v * (1f - 0.26f * step)));
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

    private static Bitmap? _flagGhost;
    private static Bitmap? _flagGhostFor;

    public static Bitmap FlagGhost(Bitmap flag)
    {
        if (_flagGhost != null && ReferenceEquals(_flagGhostFor, flag)) return _flagGhost;
        const int fw = 420, fh = 264, amp = 12;
        const int oh = fh + amp * 2;
        using var scaled = new Bitmap(fw, fh, PixelFormat.Format32bppArgb);
        using (var sg = Graphics.FromImage(scaled))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            sg.DrawImage(flag, new Rectangle(0, 0, fw, fh), 0, 0, flag.Width, flag.Height, GraphicsUnit.Pixel);
        }
        var src = scaled.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var sb = new byte[src.Stride * fh];
        System.Runtime.InteropServices.Marshal.Copy(src.Scan0, sb, 0, sb.Length);
        int stride = src.Stride;
        scaled.UnlockBits(src);

        var bmp = new Bitmap(fw, oh, PixelFormat.Format32bppPArgb);
        var dst = bmp.LockBits(new Rectangle(0, 0, fw, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var ob = new byte[dst.Stride * oh];
        for (int x = 0; x < fw; x++)
        {
            float ph = x / (float)fw * MathF.Tau * 2.4f;
            float dy = amp * MathF.Sin(ph);
            float shade = 1f + 0.10f * MathF.Cos(ph);
            float ex = (x - fw / 2f) / (fw / 2f);
            float fadeX = 1f - ex * ex;
            for (int y = 0; y < oh; y++)
            {
                float sy = y - amp - dy;
                int y0 = (int)MathF.Floor(sy);
                if (y0 < -1 || y0 >= fh) continue;
                float fr = sy - y0;
                int ia = Math.Clamp(y0, 0, fh - 1) * stride + x * 4;
                int ib = Math.Clamp(y0 + 1, 0, fh - 1) * stride + x * 4;

                float aa = (y0 >= 0 ? sb[ia + 3] : 0) * (1f - fr) + (y0 + 1 < fh ? sb[ib + 3] : 0) * fr;
                float ey = (sy - fh / 2f) / (fh / 2f);
                float fadeY = Math.Max(0f, 1f - ey * ey);
                float alpha = aa / 255f * fadeX * fadeY;
                if (alpha <= 0.004f) continue;
                int o = y * dst.Stride + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    float ch = (sb[ia + c] * (1f - fr) + sb[ib + c] * fr) * shade;
                    ob[o + c] = (byte)(Math.Min(ch, 255f) * alpha);
                }
                ob[o + 3] = (byte)(alpha * 255f);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(ob, 0, dst.Scan0, ob.Length);
        bmp.UnlockBits(dst);
        var old = _flagGhost;
        _flagGhost = bmp;
        _flagGhostFor = flag;
        old?.Dispose();
        return bmp;
    }

    public static void DrawFlagGhost(Graphics g, System.Drawing.Bitmap? flag, int w, int h, float a)
    {
        if (flag is null) return;
        var ghost = FlagGhost(flag);
        const int gw = 210;
        int gh = ghost.Height * gw / ghost.Width;
        DrawFlagGhost(g, flag, new RectangleF((w - gw) / 2f, (h - gh) / 2f + 4, gw, gh), a);
    }

    public static void DrawFlagGhost(Graphics g, System.Drawing.Bitmap? flag, RectangleF dest, float a)
    {
        if (flag is null) return;
        var ghost = FlagGhost(flag);
        float strength = dest.Width >= 160 ? 0.16f : dest.Width >= 90 ? 0.22f : 0.30f;
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix { Matrix33 = strength * a });
        var oldInterp = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(ghost, Rectangle.Round(dest), 0, 0, ghost.Width, ghost.Height, GraphicsUnit.Pixel, ia);
        g.InterpolationMode = oldInterp;
    }

    public static void DrawSeekArrow(Graphics g, RectangleF chip, bool forward, float alpha, string label = "10")
    {
        var c = Color.FromArgb((int)(238 * alpha), 255, 255, 255);
        float cx = chip.X + chip.Width / 2f, cy = chip.Y + chip.Height / 2f, r = chip.Width * 0.30f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const float gap = 80f;
        using (var pen = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 270f + gap / 2f, 360f - gap);

        float deg = forward ? 270f - gap / 2f : 270f + gap / 2f;
        float th = deg * MathF.PI / 180f;
        var p = new PointF(cx + r * MathF.Cos(th), cy + r * MathF.Sin(th));
        var dir = forward ? new PointF(-MathF.Sin(th), MathF.Cos(th)) : new PointF(MathF.Sin(th), -MathF.Cos(th));
        var perp = new PointF(-dir.Y, dir.X);
        float ah = chip.Width * 0.13f, aw = chip.Width * 0.10f;
        using (var b = new SolidBrush(c))
        using (var tri = new GraphicsPath())
        {
            tri.AddPolygon(new[]
            {
                new PointF(p.X + dir.X * ah, p.Y + dir.Y * ah),
                new PointF(p.X - dir.X * ah * 0.4f + perp.X * aw, p.Y - dir.Y * ah * 0.4f + perp.Y * aw),
                new PointF(p.X - dir.X * ah * 0.4f - perp.X * aw, p.Y - dir.Y * ah * 0.4f - perp.Y * aw),
            });
            g.FillPath(b, tri);
        }
        using var f = new Font("Segoe UI Semibold", chip.Width * 0.26f, GraphicsUnit.Pixel);
        using var tb = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, f, tb, new RectangleF(chip.X, chip.Y + 0.5f, chip.Width, chip.Height), sf);
    }

    public static void DrawCcMark(Graphics g, RectangleF chip, float alpha)
    {
        using var f = new Font("Segoe UI Semibold", chip.Width * 0.34f, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Color.FromArgb((int)(238 * alpha), 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("CC", f, b, chip, sf);
    }

    public static void DrawPipMark(Graphics g, RectangleF chip, float alpha)
    {
        var c = Color.FromArgb((int)(238 * alpha), 255, 255, 255);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = chip.Width * 0.44f, h = w * 0.72f;
        var f = new RectangleF(chip.X + (chip.Width - w) / 2f, chip.Y + (chip.Height - h) / 2f, w, h);
        using (var op = Rounded(f, 2f))
        using (var pen = new Pen(c, 1.5f))
            g.DrawPath(pen, op);

        var a = new PointF(f.X + w * 0.30f, f.Y + h * 0.30f);
        var b = new PointF(f.Right - w * 0.22f, f.Bottom - h * 0.26f);
        var d = new PointF(b.X - a.X, b.Y - a.Y);
        float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        d = new PointF(d.X / len, d.Y / len);
        using (var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, a, b);
        float ah = w * 0.28f;
        var perp = new PointF(-d.Y, d.X);
        using var tb = new SolidBrush(c);
        using var tri = new GraphicsPath();
        tri.AddPolygon(new[]
        {
            b,
            new PointF(b.X - d.X * ah + perp.X * ah * 0.55f, b.Y - d.Y * ah + perp.Y * ah * 0.55f),
            new PointF(b.X - d.X * ah - perp.X * ah * 0.55f, b.Y - d.Y * ah - perp.Y * ah * 0.55f),
        });
        g.FillPath(tb, tri);
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

    internal static Color UsageColor(float f) =>
        f <= 0.5f ? UsageGreen
        : f <= 0.75f ? HueLerp(UsageGreen, UsageAmber, (f - 0.5f) / 0.25f)
        : HueLerp(UsageAmber, UsageRed, Math.Clamp((f - 0.75f) / 0.25f, 0f, 1f));

    private static readonly Color UsageGreen = Color.FromArgb(62, 207, 92);
    private static readonly Color UsageAmber = Color.FromArgb(255, 176, 32);
    private static readonly Color UsageRed = Color.FromArgb(229, 72, 77);

    private static readonly Color RingHot = Color.FromArgb(255, 122, 36);

        internal static Color MoodRing(Color state, in Halo.Agents.MoodContext ctx)
    {

        float squeeze = MathF.Max(Ramp(ctx.ContextFrac, 0.55f, 0.95f), Ramp(ctx.UsageFrac, 0.70f, 0.98f));
        float drag = ctx.Running is { } r ? Ramp((float)r.TotalMinutes, 2f, 12f) : 0f;

        var target = HueLerp(UsageAmber, RingHot, squeeze);

        float pull = MathF.Max(0.85f * squeeze, 0.25f * drag * (1f - squeeze));
        var c = HueLerp(state, target, pull);

        if (ctx.Hour is >= 0 and <= 5) c = Scale(c, 0.86f);

        return Color.FromArgb(state.A, c.R, c.G, c.B);
    }

    private static float Ramp(float v, float from, float to)
        => to <= from ? 0f : Math.Clamp((v - from) / (to - from), 0f, 1f);

    private static Color Scale(Color c, float k) => Color.FromArgb(
        c.A, (int)Math.Clamp(c.R * k, 0, 255), (int)Math.Clamp(c.G * k, 0, 255), (int)Math.Clamp(c.B * k, 0, 255));

    private static Color HueLerp(Color a, Color b, float t)
    {
        var (h1, s1, v1) = ToHsv(a);
        var (h2, s2, v2) = ToHsv(b);
        float dh = h2 - h1;
        if (dh > 180) dh -= 360; else if (dh < -180) dh += 360;
        return FromHsv(h1 + dh * t, s1 + (s2 - s1) * t, v1 + (v2 - v1) * t);
    }

    private static (float h, float s, float v) ToHsv(Color c)
    {
        float r = c.R / 255f, g2 = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g2, b)), min = Math.Min(r, Math.Min(g2, b)), d = max - min;
        float h = d == 0 ? 0
            : max == r ? 60 * (((g2 - b) / d) % 6)
            : max == g2 ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g2) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(float h, float s, float v)
    {
        h = (h % 360 + 360) % 360;
        float c = v * s, x = c * (1 - MathF.Abs((h / 60) % 2 - 1)), m = v - c;
        var (r, g2, b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x)
            : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        return Color.FromArgb(255, (int)((r + m) * 255), (int)((g2 + m) * 255), (int)((b + m) * 255));
    }

}
