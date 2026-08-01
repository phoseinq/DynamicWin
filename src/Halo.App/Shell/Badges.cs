using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.Widgets;

namespace Halo.Shell;

// The tiles Halo's OWN banners wear. A mirrored toast arrives with its app's icon; a battery warning or a
// hourly chime has nobody to borrow one from, so each draws its own: a vivid rounded gradient with one
// Fluent glyph on it. The tile is also what gives NotifBanner's glow a colour to pull (Fx.AccentOf), which
// is why a flat grey placeholder was never an option.
//
// Lifted out of NotchController, which had grown a dozen of these factories in the middle of the alert
// logic. The glyphs are no longer chosen from memory of the MDL2 chart either - `--render-fluent` prints a
// labelled contact sheet of the installed font and every code point below was picked off one, which is how
// the battery tile turned out to have been a battery with a CROSS through it (0xE996, "unknown") for as
// long as it has existed.
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

        // Beside Windows' own app icons a flat gradient reads as a placeholder, and these sit in the very
        // same row of banners. Two cheap passes fix it: a broad highlight falling across the top, and a
        // hairline rim. Both are clipped to the tile, or the ellipse below sprays over the rounded corners.
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

        // Filled path rather than Fx.GlyphCentred: at 30px the hinting DrawString would buy back is worth
        // less than being able to lay the same outline down twice, once dark and a pixel low, so the glyph
        // sits ON the tile instead of in it. Centred on its own ink, because metric-centred Fluent glyphs
        // read visibly off - two glyphs of this font share a line box and share nothing else.
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

    // The language flip's tile: the same recipe with two letters instead of a glyph. The hue is derived
    // from the code so every language is its own colour and the banner's glow follows.
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

    // One tile per thing that can go wrong, which is the point: cpu and ram used to share the processor
    // die, and "context is nearly full" wore the same gauge as "you have spent your weekly limit", so three
    // different pieces of news arrived looking like one repeated banner. Hue is not decoration either - it
    // is the banner's glow, so severity is readable before the words are.
    internal static Bitmap BatteryLow() => Local(0xE852, 35);        // a fifth of a battery, amber
    internal static Bitmap BatteryDead() => Local(0xE851, 4);        // nearly empty, red
    internal static Bitmap Cpu() => Local(0xE950, 18);               // the processor die, running hot
    internal static Bitmap Memory() => Local(0xE964, 318);           // a dimm, so ram stops borrowing the cpu's tile
    internal static Bitmap NetSlow() => Local(0xEB63, 40, 34f);      // wifi under a warning
    internal static Bitmap NetDown() => Local(0xEB5E, 4, 34f);       // wifi struck through: nothing is getting out
    // a robot rather than a server rack: the rack read as a printer at this size, and the news is about the
    // agent being unreachable rather than about any box in particular
    internal static Bitmap ApiDown() => Local(0xE99A, 348, 33f);
    internal static Bitmap Limit() => Local(0xE945, 285);            // a bolt, for the window you burn through
    internal static Bitmap LimitLong() => Local(0xE787, 258);        // a calendar, for the window measured in days
    internal static Bitmap Context() => Local(0xEC4A, 55, 34f);      // a dial: how full the conversation is
    internal static Bitmap Clock() => Local(0xE917, 205);
    internal static Bitmap Shot() => Local(0xE722, 200, 28f);        // camera
    internal static Bitmap Clip() => Local(0xE8C8, 155, 28f);        // copy

    // The chime carries the sky in its tile, which is why its line no longer has to. Falls back to the
    // clock when there is no reading - a banner with no weather in it must not imply one.
    internal static Bitmap Hourly()
    {
        if (Almanac.Latest is not { } wx) return Clock();
        var (glyph, hue) = Almanac.SkyBadge(wx.Code, wx.Day);
        return Local(glyph, hue, 32f);
    }

    // dev-only: every tile in a row for --render-badges. The sky tiles are listed by hand rather than
    // through Hourly(), which would only ever draw today's weather.
    internal static Bitmap[] All() =>
    [
        BatteryLow(), BatteryDead(), Cpu(), Memory(), NetSlow(), NetDown(), ApiDown(),
        Limit(), LimitLong(), Context(), Clock(), Shot(), Clip(),
        Local(0xE706, 30, 32f), Local(0xE708, 232, 32f), Local(0xE753, 220, 32f), Local(0xEA38, 188, 32f),
    ];
}
