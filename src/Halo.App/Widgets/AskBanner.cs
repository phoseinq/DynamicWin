using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.ClaudeCode;

namespace Halo.Widgets;

internal static class AskBanner
{
    internal const int W = 470;

    internal const int DeskTint = 200;
    private const float Pad = 20f;
    private const float IconD = 19f, IconGap = 8f;
    private const float EyebrowTop = 18f, EyebrowH = 16f, EyebrowPx = 11.5f;
    private const float TitleTop = 44f, TitlePx = 19f, TitleLineH = 25f;
    private const float TargetPx = 13f, TargetH = 19f;
    private const float TitleGap = 14f;

    private const float RowGap = 8f, RowRadius = 16f, RowPadX = 14f, RowPadY = 11f;
    private const float MinRowH = 50f;

    private const float NumD = 32f, NumGap = 11f, NumPx = 16f;
    private const float BottomPad = 20f;
    private const float LabelPx = 15f, DescPx = 12.5f;

    private const float LabelLineH = 20f, DescLineH = 17f, LabelDescGap = 2f;
    private const int TitleMaxLines = 3, LabelMaxLines = 2, DescMaxLines = 3;

    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(175, 255, 255, 255);
    private static readonly Color DimClear = Color.FromArgb(228, 255, 255, 255);
    private static readonly Color TargetInk = Color.FromArgb(222, 255, 255, 255);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);

    private static bool HasTarget(PendingAsk ask) => !ask.IsQuestion && !string.IsNullOrEmpty(ask.Target);

    internal static readonly AskOption Other = new("Chat about this", "say it in your own words");

    internal static bool IsOther(AskOption option) => ReferenceEquals(option, Other);

    private static bool HasOther(PendingAsk ask) => ask.IsQuestion;

    internal sealed record AskRow(
        RectangleF Rect, RectangleF Body, RectangleF Label, RectangleF Desc, AskOption Option);
    internal sealed record AskLayout(
        RectangleF Title, RectangleF Target, IReadOnlyList<AskRow> Rows, int Height);

    private static readonly object LayoutLock = new();
    private static Graphics? _measure;
    private static PendingAsk? _memoAsk;
    private static int _memoW;
    private static AskLayout? _memo;

    internal static AskLayout Layout(PendingAsk ask, int w)
    {
        lock (LayoutLock)
        {
            if (_memo != null && ReferenceEquals(_memoAsk, ask) && _memoW == w) return _memo;
            _memo = Build(ask, w);
            _memoAsk = ask;
            _memoW = w;
            return _memo;
        }
    }

    private static AskLayout Build(PendingAsk ask, int w)
    {
        float inner = w - Pad * 2;
        using var tf = new Font("Segoe UI Semibold", TitlePx, GraphicsUnit.Pixel);
        var title = new RectangleF(Pad, TitleTop, inner, Lines(Title(ask), tf, inner, TitleMaxLines) * TitleLineH);
        var target = new RectangleF(Pad, title.Bottom, inner, HasTarget(ask) ? TargetH : 0f);

        float bodyX = Pad + NumD + NumGap;
        float textX = bodyX + RowPadX, textW = w - Pad - RowPadX - textX;
        float y = target.Bottom + TitleGap;

        using var lf = new Font("Segoe UI Semibold", LabelPx, GraphicsUnit.Pixel);
        using var df = new Font("Segoe UI", DescPx, GraphicsUnit.Pixel);
        var rows = new List<AskRow>();
        var options = new List<AskOption>(ask.Options);
        if (HasOther(ask)) options.Add(Other);
        foreach (var option in options)
        {
            float labelH = Lines(option.Label, lf, textW, LabelMaxLines) * LabelLineH;
            bool hasDesc = !string.IsNullOrWhiteSpace(option.Description);
            float descH = hasDesc ? Lines(option.Description, df, textW, DescMaxLines) * DescLineH : 0f;
            float stack = labelH + (hasDesc ? LabelDescGap + descH : 0f);
            float rowH = MathF.Max(MinRowH, stack + RowPadY * 2);

            float top = y + (rowH - stack) / 2f;
            var label = new RectangleF(textX, top, textW, labelH);
            rows.Add(new AskRow(
                new RectangleF(Pad, y, inner, rowH),
                new RectangleF(bodyX, y, w - Pad - bodyX, rowH),
                label,
                hasDesc ? new RectangleF(textX, label.Bottom + LabelDescGap, textW, descH) : RectangleF.Empty,
                option));
            y += rowH + RowGap;
        }

        float bottom = rows.Count > 0 ? rows[^1].Rect.Bottom : y + MinRowH;
        return new AskLayout(title, target, rows, (int)MathF.Ceiling(bottom + BottomPad));
    }

    private static int Lines(string? text, Font font, float width, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 1f) return 1;
        try
        {
            _measure ??= Graphics.FromImage(new Bitmap(1, 1, PixelFormat.Format32bppPArgb));
            _measure.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var sf = Wrap(StringAlignment.Near);
            _measure.MeasureString(text, font, new SizeF(width, 4000f), sf, out _, out int lines);
            return Math.Clamp(lines, 1, max);
        }
        catch { return 1; }
    }

    internal static List<(RectangleF Rect, AskOption Option)> Chips(PendingAsk ask, int w)
    {
        var result = new List<(RectangleF, AskOption)>();
        foreach (var row in Layout(ask, w).Rows) result.Add((row.Rect, row.Option));
        return result;
    }

    internal static int Height(PendingAsk ask, int w) => Layout(ask, w).Height;

    internal static void Draw(Graphics g, int w, int h, float a, PendingAsk ask, int hover,
        int tint = DeskTint, string? typed = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        bool seeThrough = tint < DeskTint;
        var layout = Layout(ask, w);
        DrawEyebrow(g, w, a, ask, seeThrough);

        using (var tf = new Font("Segoe UI Semibold", TitlePx, GraphicsUnit.Pixel))
        using (var sf = Wrap(StringAlignment.Center))
            InkRtl(g, Title(ask), tf, Slack(layout.Title), sf, White, a, seeThrough);

        if (HasTarget(ask))
            using (var gf = new Font("Consolas", TargetPx, GraphicsUnit.Pixel))
            using (var sf = Centre())
                Ink(g, ask.Target!, gf, layout.Target, sf, TargetInk, a, seeThrough);

        for (int i = 0; i < layout.Rows.Count; i++)
        {
            var row = layout.Rows[i];
            bool typing = typed != null && IsOther(row.Option);
            DrawRow(g, row, i + 1, a, typing || i == hover, Accent(ask, row.Option.Label), seeThrough,
                typing ? typed : null);
        }
    }

    private static void DrawEyebrow(Graphics g, int w, float a, PendingAsk ask, bool seeThrough)
    {
        string label = Eyebrow(ask);
        using var ef = new Font("Segoe UI Semibold", EyebrowPx, GraphicsUnit.Pixel);
        float textW = g.MeasureString(label, ef, int.MaxValue, StringFormat.GenericTypographic).Width;
        var icon = ClaudeCodeWidget.PlainIcon;
        float groupW = textW + (icon != null ? IconD + IconGap : 0f);
        float x = (w - groupW) / 2f;

        if (icon != null)
        {
            DrawRoundIcon(g, icon, x, EyebrowTop + (EyebrowH - IconD) / 2f, IconD, a);
            x += IconD + IconGap;
        }
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        Ink(g, label, ef, new RectangleF(x, EyebrowTop, textW + 4, EyebrowH), sf,
            seeThrough ? DimClear : Dim, a, seeThrough);
    }

    private static Color Accent(PendingAsk ask, string label)
    {
        if (ask.IsQuestion) return Amber;
        return label switch { "allow" => Green, "deny" => Red, _ => Amber };
    }

    private static void DrawRow(Graphics g, AskRow row, int number, float a, bool hover, Color accent,
        bool seeThrough, string? typed)
    {
        var r = row.Rect;
        var numRect = new RectangleF(r.X, r.Y + (r.Height - NumD) / 2f, NumD, NumD);
        DrawVessel(g, row.Body, a, hover, accent, seeThrough);

        DrawBead(g, numRect, a, hover, seeThrough);

        DrawGlyph(g, number.ToString(), numRect,
            Color.FromArgb((int)(a * (hover ? 225 : 170)), 255, 255, 255), seeThrough ? a : 0f);

        using var sf = Wrap(StringAlignment.Near);
        using var lf = new Font("Segoe UI Semibold", LabelPx, GraphicsUnit.Pixel);
        using var df = new Font("Segoe UI", DescPx, GraphicsUnit.Pixel);

        if (typed != null)
        {

            string shown = Tail(g, typed, lf, row.Label.Width - CaretW - 2f);

            bool rtl = Fx.IsRtl(shown);
            using var tsf = rtl ? Wrap(StringAlignment.Near) : null;
            var fmt = tsf ?? sf;
            if (rtl) fmt.FormatFlags |= StringFormatFlags.DirectionRightToLeft;

            Ink(g, shown, lf, Slack(row.Label), fmt, White, a, seeThrough);
            float run = shown.Length == 0 ? 0f
                : g.MeasureString(shown, lf, int.MaxValue, StringFormat.GenericTypographic).Width;
            float caretX = rtl ? row.Label.Right - run - CaretW - 1f : row.Label.X + run + 1f;
            using (var cb = new SolidBrush(Mul(accent, a)))
                g.FillRectangle(cb, caretX, row.Label.Y + 1f, CaretW, LabelLineH - 4f);
            if (row.Desc.Height > 0f)
                Ink(g, "enter to send   esc to go back", df, Slack(row.Desc), sf,
                    seeThrough ? DimClear : Dim, a, seeThrough);
            return;
        }

        InkRtl(g, row.Option.Label, lf, Slack(row.Label), sf, White, a, seeThrough);
        if (row.Desc.Height <= 0f) return;

        InkRtl(g, row.Option.Description, df, Slack(row.Desc), sf, seeThrough ? DimClear : Dim, a, seeThrough);
    }

    private const float CaretW = 2f;

    private static string Tail(Graphics g, string text, Font f, float width)
    {
        if (text.Length == 0 || width <= 4f) return text;
        int start = 0;
        while (start < text.Length &&
               g.MeasureString(text[start..], f, int.MaxValue, StringFormat.GenericTypographic).Width > width)
            start++;
        return text[start..];
    }

    private static readonly PointF[] Halo =
        [new(-1f, 0f), new(1f, 0f), new(0f, -1f), new(0f, 1f)];

    private static void InkRtl(Graphics g, string text, Font f, RectangleF r, StringFormat sf, Color c,
        float a, bool seeThrough)
    {
        if (!Fx.IsRtl(text)) { Ink(g, text, f, r, sf, c, a, seeThrough); return; }
        using var rsf = new StringFormat(sf) { FormatFlags = sf.FormatFlags | StringFormatFlags.DirectionRightToLeft };
        Ink(g, text, f, r, rsf, c, a, seeThrough);
    }

    private static void Ink(Graphics g, string text, Font f, RectangleF r, StringFormat sf, Color c,
        float a, bool seeThrough)
    {
        if (seeThrough)
        {
            using var sh = new SolidBrush(Color.FromArgb((int)(a * 72), 0, 0, 0));
            foreach (var d in Halo)
                g.DrawString(text, f, sh, new RectangleF(r.X + d.X, r.Y + d.Y, r.Width, r.Height), sf);
        }
        using var b = new SolidBrush(Mul(c, a));
        g.DrawString(text, f, b, r, sf);
    }

    private static Color Body(float a, bool hover)
        => Color.FromArgb((int)(a * (hover ? 18 : 7)), 255, 255, 255);

    private static void DrawVessel(Graphics g, RectangleF r, float a, bool hover, Color accent, bool seeThrough)
    {
        using var path = Rounded(r, RowRadius);
        using (var fill = new SolidBrush(Body(a, hover)))
            g.FillPath(fill, path);

        var clip = g.Clip;
        g.SetClip(path);
        Streak(g, r.X + RowRadius, r.Right - RowRadius, r.Y + 2.6f, a * (hover ? 1.25f : 1f), 132, 1.5f);
        Streak(g, r.X + RowRadius * 1.6f, r.Right - RowRadius * 1.6f, r.Bottom - 2.6f,
            a * (hover ? 1.25f : 1f), 58, 1.2f);
        g.Clip = clip;

        using var rimBrush = new LinearGradientBrush(
            new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2),
            Color.FromArgb((int)(a * (hover ? 165 : 128)), 255, 255, 255),
            Color.FromArgb((int)(a * (hover ? 96 : 58)), 255, 255, 255), 90f);
        using var pen = hover ? new Pen(Mul(accent, a * 0.9f), 0.9f) : new Pen(rimBrush, 0.7f);
        g.DrawPath(pen, path);
    }

    private static void Streak(Graphics g, float x0, float x1, float y, float a, int peak, float width)
    {
        if (x1 - x0 < 4f) return;
        using var brush = new LinearGradientBrush(
            new RectangleF(x0, y - 2f, x1 - x0, 4f), Color.Transparent, Color.Transparent, 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors =
                [
                    Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb((int)Math.Clamp(a * peak, 0, 255), 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255),
                ],
                Positions = [0f, 0.42f, 1f],
            },
        };
        using var pen = new Pen(brush, width);
        g.DrawLine(pen, x0, y, x1, y);
    }

    private static void DrawBead(Graphics g, RectangleF box, float a, bool hover, bool seeThrough)
    {
        using var circle = new GraphicsPath();
        circle.AddEllipse(box);
        using (var fill = new SolidBrush(Body(a, hover)))
            g.FillPath(fill, circle);

        using var rimBrush = new LinearGradientBrush(
            new RectangleF(box.X, box.Y - 1, box.Width, box.Height + 2),
            Color.FromArgb((int)(a * (hover ? 168 : 130)), 255, 255, 255),
            Color.FromArgb((int)(a * (hover ? 92 : 56)), 255, 255, 255), 90f);
        using var rim = new Pen(rimBrush, 0.7f);
        g.DrawEllipse(rim, box);
    }

    private static void DrawGlyph(Graphics g, string text, RectangleF box, Color ink, float shadow)
    {
        try
        {
            using var path = new GraphicsPath();
            using var family = new FontFamily("Consolas");
            path.AddString(text, family, (int)FontStyle.Bold, NumPx, PointF.Empty, StringFormat.GenericTypographic);

            using var probe = (GraphicsPath)path.Clone();
            probe.Flatten();
            var b = probe.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            using var m = new Matrix();
            m.Translate(MathF.Round(box.X + (box.Width - b.Width) / 2f - b.X),
                        MathF.Round(box.Y + (box.Height - b.Height) / 2f - b.Y));
            path.Transform(m);
            if (shadow > 0.004f)
            {
                using var sb = new SolidBrush(Color.FromArgb((int)(shadow * 105), 0, 0, 0));
                foreach (var d in Halo)
                {
                    using var sm = new Matrix();
                    sm.Translate(d.X, d.Y);
                    using var sp = (GraphicsPath)path.Clone();
                    sp.Transform(sm);
                    g.FillPath(sb, sp);
                }
            }
            using var brush = new SolidBrush(ink);
            g.FillPath(brush, path);
        }
        catch { }
    }

    private static void DrawRoundIcon(Graphics g, Bitmap img, float x, float y, float d, float a)
    {
        var circle = new RectangleF(x, y, d, d);
        int s = Math.Max(1, (int)Math.Ceiling(d));
        using var scaled = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(scaled))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });
            int side = Math.Min(img.Width, img.Height);
            sg.DrawImage(img, new Rectangle(0, 0, s, s),
                (img.Width - side) / 2, (img.Height - side) / 2, side, side, GraphicsUnit.Pixel, ia);
        }

        using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
        tb.TranslateTransform(circle.X, circle.Y);
        using var p = new GraphicsPath();
        p.AddEllipse(circle);
        g.FillPath(tb, p);
    }

    private static StringFormat Wrap(StringAlignment align) => new(StringFormat.GenericTypographic)
    {
        Alignment = align,
        LineAlignment = StringAlignment.Near,
        FormatFlags = 0,
        Trimming = StringTrimming.EllipsisCharacter,
    };

    private static RectangleF Slack(RectangleF r) => new(r.X, r.Y, r.Width, r.Height + 3f);

    private static StringFormat Centre() => new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.EllipsisCharacter,
    };

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
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
