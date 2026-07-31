using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using Halo.ClaudeCode;

namespace Halo.Widgets;

// The banner you answer from. Same shell as NotifBanner - it is the same pill, morphed - but its body is
// a row of chips instead of a message, because the whole point is that the click IS the answer.
internal static class AskBanner
{
    internal const int W = 470;
    private const float Pad = 20f;
    private const float EyebrowTop = 18f, EyebrowH = 14f, EyebrowPx = 11f;
    private const float TitleTop = 38f, TitlePx = 17f;
    private const float TargetPx = 12.5f;
    private const float ChipH = 30f, ChipGap = 8f, ChipPadX = 13f, ChipMinW = 72f;
    private const float ChipsTop = 74f, BottomPad = 16f;

    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Target = Color.FromArgb(190, 255, 255, 255);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);

    // Layout is separate from painting so the hit-test and the drawing cannot disagree - a chip you can
    // see but not click is the worst possible bug in a thing whose only job is to be clicked.
    internal static List<(RectangleF Rect, AskOption Option)> Chips(Graphics g, PendingAsk ask, int w)
    {
        var result = new List<(RectangleF, AskOption)>();
        using var f = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
        float x = Pad, y = ChipsTop, right = w - Pad;
        foreach (var option in ask.Options)
        {
            float textW = g.MeasureString(option.Label, f, int.MaxValue, StringFormat.GenericTypographic).Width;
            float cw = Math.Max(ChipMinW, textW + ChipPadX * 2);
            if (x > Pad && x + cw > right) { x = Pad; y += ChipH + ChipGap; }   // wrap rather than clip
            result.Add((new RectangleF(x, y, cw, ChipH), option));
            x += cw + ChipGap;
        }
        return result;
    }

    internal static int Height(Graphics g, PendingAsk ask, int w)
    {
        var chips = Chips(g, ask, w);
        float bottom = ChipsTop + ChipH;
        foreach (var (rect, _) in chips) bottom = Math.Max(bottom, rect.Bottom);
        return (int)Math.Ceiling(bottom + BottomPad);
    }

    internal static void Draw(Graphics g, int w, int h, float a, PendingAsk ask, int hover)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using (var eb = new SolidBrush(Mul(Dim, a)))
        using (var ef = new Font("Segoe UI", EyebrowPx, GraphicsUnit.Pixel))
        using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(Eyebrow(ask), ef, eb, new RectangleF(Pad, EyebrowTop, w - Pad * 2, EyebrowH), sf);

        // the question as written, or - for a permission - the thing that is about to run
        using (var tb = new SolidBrush(Mul(White, a)))
        using (var tf = new Font("Segoe UI Semibold", TitlePx, GraphicsUnit.Pixel))
        using (var sf = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            g.DrawString(Title(ask), tf, tb, new RectangleF(Pad, TitleTop, w - Pad * 2, 24f), sf);

        if (!ask.IsQuestion && !string.IsNullOrEmpty(ask.Target))
            using (var gb = new SolidBrush(Mul(Target, a)))
            using (var gf = new Font("Consolas", TargetPx, GraphicsUnit.Pixel))
            using (var sf = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(ask.Target, gf, gb, new RectangleF(Pad, TitleTop + 22f, w - Pad * 2, 18f), sf);

        int i = 0;
        using var cf = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
        foreach (var (rect, option) in Chips(g, ask, w))
            DrawChip(g, rect, option.Label, cf, a, i++ == hover, Accent(ask, option.Label));
    }

    // green for allow, red for deny, amber for a question's options: the colour is the answer, so a
    // mis-click is visible before it is a click
    private static Color Accent(PendingAsk ask, string label)
    {
        if (ask.IsQuestion) return Amber;
        return label switch { "allow" => Green, "deny" => Red, _ => Amber };
    }

    private static void DrawChip(Graphics g, RectangleF r, string label, Font f, float a, bool hover, Color accent)
    {
        // flat-top PillPath is for the pill itself; a chip is a free-floating capsule, so a full round rect
        // is right here - it never touches the notch's top edge
        using var path = Capsule(r);
        using (var fill = new SolidBrush(Mul(accent, a * (hover ? 0.34f : 0.18f))))
            g.FillPath(fill, path);
        using (var pen = new Pen(Mul(accent, a * (hover ? 0.95f : 0.55f)), 1.4f))
            g.DrawPath(pen, path);
        using (var tb = new SolidBrush(Mul(White, a)))
        using (var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(label, f, tb, new RectangleF(r.X, r.Y - Fx.CenterLift(f), r.Width, r.Height), sf);
    }

    private static GraphicsPath Capsule(RectangleF r)
    {
        float d = r.Height;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    private static string Eyebrow(PendingAsk ask)
        => ask.IsQuestion ? "CLAUDE CODE ASKS" : $"CLAUDE CODE WANTS TO RUN {ask.Tool.ToUpperInvariant()}";

    private static string Title(PendingAsk ask)
        => !string.IsNullOrEmpty(ask.Question) ? ask.Question!
         : ask.IsQuestion ? "your move ;)" : "run this?";

    private static Color Mul(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);
}
