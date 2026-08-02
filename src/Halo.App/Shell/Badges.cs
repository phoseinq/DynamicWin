using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.Widgets;

namespace Halo.Shell;

internal static class Badges
{
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons");

    internal static Bitmap Local(int glyphCp, int hue, float glyphPx = 30f)
    {
        var b = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new RectangleF(3, 3, 58, 58);
        using var tile = Fx.Rounded(box, 17f);
        using (var lg = new LinearGradientBrush(box,
                   Fx.HsvToRgb(hue, 0.62f, 0.96f), Fx.HsvToRgb((hue + 24) % 360, 0.74f, 0.78f), 90f))
            g.FillPath(lg, tile);

        var clipped = g.Save();
        g.SetClip(tile);
        using (var sheenPath = new GraphicsPath())
        {
            sheenPath.AddEllipse(-14f, -40f, 92f, 74f);
            using var sheen = new PathGradientBrush(sheenPath)
            {
                CenterPoint = new PointF(26f, -10f),
                CenterColor = Color.FromArgb(78, 255, 255, 255),
                SurroundColors = [Color.FromArgb(0, 255, 255, 255)],
            };
            g.FillPath(sheen, sheenPath);
        }
        g.Restore(clipped);
        using (var rim = new Pen(Color.FromArgb(42, 255, 255, 255), 1f))
            g.DrawPath(rim, tile);

        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(((char)glyphCp).ToString(), GlyphFont, (int)FontStyle.Regular, glyphPx, PointF.Empty, sf);
        path.Flatten();
        var gb = path.GetBounds();
        if (gb.Width <= 0 || gb.Height <= 0) return b;
        using (var m = new Matrix())
        {
            m.Translate(MathF.Round(32f - gb.Width / 2f - gb.X), MathF.Round(32f - gb.Height / 2f - gb.Y));
            path.Transform(m);
        }
        using (var shadow = new Matrix())
        {
            shadow.Translate(0f, 1.4f);
            using var lowered = (GraphicsPath)path.Clone();
            lowered.Transform(shadow);
            using var sb = new SolidBrush(Color.FromArgb(58, 0, 0, 0));
            g.FillPath(sb, lowered);
        }
        using (var wb = new SolidBrush(Color.FromArgb(248, 255, 255, 255)))
            g.FillPath(wb, path);
        return b;
    }

    internal static Bitmap Language(string code)
    {
        int hue = ((code.Length > 0 ? code[0] : 'A') * 37 + (code.Length > 1 ? code[1] : 0) * 17) % 360;
        var b = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var box = new RectangleF(3, 3, 58, 58);
        using (var lg = new LinearGradientBrush(box,
                   Fx.HsvToRgb(hue, 0.60f, 0.96f), Fx.HsvToRgb((hue + 20) % 360, 0.72f, 0.78f), 90f))
        using (var p = Fx.Rounded(box, 17f))
            g.FillPath(lg, p);
        using var f = new Font("Segoe UI Semibold", 25f, GraphicsUnit.Pixel);
        using var wb = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(code, f, wb, new RectangleF(0, 0, 64, 64), sf);
        return b;
    }

    internal static Bitmap BatteryLow() => Local(0xE852, 35);
    internal static Bitmap BatteryDead() => Local(0xE851, 4);
    internal static Bitmap Cpu() => Local(0xE950, 18);
    internal static Bitmap Memory() => Local(0xE964, 318);
    internal static Bitmap NetSlow() => Local(0xEB63, 40, 34f);
    internal static Bitmap NetDown() => Local(0xEB5E, 4, 34f);

    internal static Bitmap ApiDown() => Local(0xE99A, 348, 33f);
    internal static Bitmap Limit() => Local(0xE945, 285);
    internal static Bitmap LimitLong() => Local(0xE787, 258);
    internal static Bitmap Context() => Local(0xEC4A, 55, 34f);
    internal static Bitmap Clock() => Local(0xE917, 205);
    internal static Bitmap Shot() => Local(0xE722, 200, 28f);
    internal static Bitmap Clip() => Local(0xE8C8, 155, 28f);

    internal static Bitmap Hourly()
    {
        if (Almanac.Latest is not { } wx) return Clock();
        var (glyph, hue) = Almanac.SkyBadge(wx.Code, wx.Day);
        return Local(glyph, hue, 32f);
    }

    internal static Bitmap[] All() =>
    [
        BatteryLow(), BatteryDead(), Cpu(), Memory(), NetSlow(), NetDown(), ApiDown(),
        Limit(), LimitLong(), Context(), Clock(), Shot(), Clip(),
        Local(0xE706, 30, 32f), Local(0xE708, 232, 32f), Local(0xE753, 220, 32f), Local(0xEA38, 188, 32f),
    ];
}
