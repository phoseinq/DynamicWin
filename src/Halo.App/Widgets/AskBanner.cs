using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.ClaudeCode;

namespace Halo.Widgets;

// The banner you answer from. Same shell as NotifBanner - it is the same pill, morphed - but its body is
// a list of options instead of a message, because the whole point is that the click IS the answer.
internal static class AskBanner
{
    internal const int W = 470;
    // At or above this the panel is opaque enough to be its own background. Below it the panel is a
    // window, and everything drawn on it - the capsule walls, every line of text - has to survive whatever
    // happens to be behind the pill. That is the only thing this flag decides.
    internal const int DeskTint = 200;
    private const float Pad = 20f;
    private const float IconD = 19f, IconGap = 8f;
    private const float EyebrowTop = 18f, EyebrowH = 16f, EyebrowPx = 11.5f;
    private const float TitleTop = 44f, TitlePx = 19f, TitleLineH = 25f;
    private const float TargetPx = 13f, TargetH = 19f;
    private const float TitleGap = 14f;

    // Each option gets its OWN glass vessel, with a big radius so a row reads as a rounded body rather
    // than a table cell. They carry no accent fill: the pill is already showing what is behind it, so a
    // solid swatch would sit ON the glass instead of being made of it. The accent lives in the number.
    private const float RowGap = 8f, RowRadius = 16f, RowPadX = 14f, RowPadY = 11f;
    private const float MinRowH = 50f;
    // The number lives OUTSIDE its option's vessel, in a disc of its own on the left. It is a marker for
    // the row, not content of it, and inside the glass it competed with the label for the same corner.
    // The row's hit-test still covers it: clicking the number is clicking the option.
    private const float NumD = 32f, NumGap = 11f, NumPx = 16f;
    private const float BottomPad = 20f;
    // The way out that is not an answer. The banner deliberately ignored every click that was not a row,
    // so the only exits were answering and waiting out the deadline - fine over an app, where the banner is
    // in the way of what you were doing anyway, and wrong on the desktop where it just sits there. A
    // deliberate target in the corner is not a gesture you can make by brushing past, which was the whole
    // objection. It does NOT answer: the question stays standing in the terminal, this takes the banner off
    // the screen.
    private const float CloseD = 24f;
    internal static RectangleF CloseRect(int w) => new(w - Pad - CloseD, EyebrowTop - 4f, CloseD, CloseD);
    private const float LabelPx = 15f, DescPx = 12.5f;
    // Own line heights rather than the font's: MeasureString is asked only how many lines the text takes,
    // and the rhythm between label and description stays a decision here instead of a font metric.
    private const float LabelLineH = 20f, DescLineH = 17f, LabelDescGap = 2f;
    private const int TitleMaxLines = 3, LabelMaxLines = 2, DescMaxLines = 3;

    // Ink runs hotter than it did on a solid panel. Dim at 155 was a recessive grey against near-black;
    // against a live backdrop it is just hard to read, and the hierarchy between label and description now
    // comes from weight and size rather than from fading the description toward the panel.
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(175, 255, 255, 255);
    private static readonly Color DimClear = Color.FromArgb(228, 255, 255, 255);
    private static readonly Color TargetInk = Color.FromArgb(222, 255, 255, 255);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);

    private static bool HasTarget(PendingAsk ask) => !ask.IsQuestion && !string.IsNullOrEmpty(ask.Target);

    // Claude Code's own question UI always lets you ignore the options, and a banner that only offered the
    // canned answers quietly removed that. Appended rather than sent by the hook: the hook forwards the
    // tool's options untouched, and these are the pill's, so inventing them there would put choices in the
    // payload that Claude never offered. Reference identity is how the click handler tells them apart - the
    // label is display text and a real option could carry the same one.
    //
    // What sits past the options has changed under us twice, and both times one row was made to stand for
    // it. It was a bare free-text field ("Something else / type your own answer"); then the box grew a
    // "Chat about this" row and this one was renamed to match. The box has BOTH now, and they are not the
    // same kind of thing: read out of Claude Code's own bundle, __other__ is type:"input" and __chat__ is
    // type:"text". So the single row was wrong three ways at once - it hid the free-text choice, it put a
    // caret and "enter to send" on a row that takes no words, and its keystroke reached neither reliably.
    internal static readonly AskOption FreeText = new("Type something", "answer in your own words");
    internal static readonly AskOption Chat = new("Chat about this", "leave the question and talk instead");

    internal static bool IsFreeText(AskOption option) => ReferenceEquals(option, FreeText);
    internal static bool IsChat(AskOption option) => ReferenceEquals(option, Chat);
    internal static bool IsBuiltIn(AskOption option) => IsFreeText(option) || IsChat(option);

    // Both rows are Halo's, not Claude's: the hook never invents an option Claude did not offer. In an
    // ordinary terminal the box shows both - __other__ unconditionally, and the chat row whenever Ink's
    // accessibility mode is off, which is its default. The one shape that differs is a question whose
    // options carry previews: that renders a different component with a Notes field and no numbered rows
    // at all, which is why the pill needs to be told (see AskEnvelope.HasPreview).
    internal static IReadOnlyList<AskOption> BuiltInsFor(PendingAsk ask)
        => ask.IsQuestion && !ask.HasPreview ? [FreeText, Chat] : [];

    // Layout is separate from painting so the hit-test and the drawing cannot disagree - a row you can see
    // but not click is the worst bug available to a surface whose only job is to be clicked.
    internal sealed record AskRow(
        RectangleF Rect, RectangleF Body, RectangleF Label, RectangleF Desc, AskOption Option);
    internal sealed record AskLayout(
        RectangleF Title, RectangleF Target, IReadOnlyList<AskRow> Rows, int Height);

    // Every box here is sized to the text it holds instead of to a constant. The fixed 46px row was
    // ellipsising away descriptions the banner had the whole screen above it to grow into, and the fix is
    // not smaller type - the text IS the content, so the geometry follows it and the pill grows.
    //
    // Measuring needs a Graphics, which is why this replaced plain arithmetic. The property that mattered
    // about the old version is kept instead: callers may still recompute every frame, because a one-entry
    // memo keyed on the ask instance - stable for as long as the question is on screen - makes that cheap.
    // Nobody may cache a height on the caller's side; that is exactly how a hit-test drifts out of step
    // with what was painted.
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
        options.AddRange(BuiltInsFor(ask));
        foreach (var option in options)
        {
            float labelH = Lines(option.Label, lf, textW, LabelMaxLines) * LabelLineH;
            bool hasDesc = !string.IsNullOrWhiteSpace(option.Description);
            float descH = hasDesc ? Lines(option.Description, df, textW, DescMaxLines) * DescLineH : 0f;
            float stack = labelH + (hasDesc ? LabelDescGap + descH : 0f);
            float rowH = MathF.Max(MinRowH, stack + RowPadY * 2);

            // label and description are centred as one block, so a row with a description and a row
            // without still look like the same object at different sizes
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

    // How many lines the text wraps to, capped. A 1x1 surface held for the process lifetime rather than
    // built per call: this runs once per question, but building a Graphics to ask one question is the kind
    // of thing that ends up on the render path later.
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

    // tint is the panel the banner is painted on, and the vessels need it because a pane has to contrast
    // with its base and the base is not the same in both places. On the desktop the panel is near-opaque
    // black, so a white wash lightens into a pane. Over an app the panel is a WINDOW - that transparency is
    // the whole point - and a white wash there has nothing to lighten against: over anything bright the
    // rows vanished. Darkening the whole banner to fix that is just opacity wearing a glass costume, so
    // the density moves into the pane itself and the banner stays see-through.
    // typed is the free-text answer being composed, or null when the list is just a list. It is passed in
    // rather than held here because the keystrokes arrive at the window, and a widget that owned input
    // state would have to be told about focus, cancellation and the banner closing under it.
    internal static void Draw(Graphics g, int w, int h, float a, PendingAsk ask, int hover,
        int tint = DeskTint, string? typed = null, bool closeHover = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // AntiAlias, not AntiAliasGridFit, and only here. GridFit snaps stems to whole pixels, which is
        // right for text on a solid panel and wrong on this one: the panel is a window now, the glyph edge
        // is the only edge the eye has to work with, and hinted stems next to a halo read as a staircase.
        // Ungridfitted glyphs are what the reference glass surfaces do.
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        bool seeThrough = tint < DeskTint;
        var layout = Layout(ask, w);
        DrawEyebrow(g, w, a, ask, seeThrough);
        DrawClose(g, CloseRect(w), a, closeHover, seeThrough);

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
            // only the free-text row becomes a field. The chat row is a plain choice in the box - drawing a
            // caret and "enter to send" on it promised something it cannot do, and clicking it sent nothing.
            bool typing = typed != null && IsFreeText(row.Option);
            DrawRow(g, row, i + 1, a, typing || i == hover, Accent(ask, row.Option.Label), seeThrough,
                typing ? typed : null);
        }
    }

    // icon and label ride together as one centred group, so the pair stays optically centred whatever the
    // label says - centring them independently would leave the text off-centre by half the icon
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

    // green for allow, red for deny, amber for a question's options: the colour is the answer, so a
    // mis-click is visible before it is a click
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

        // The disc is glass too, not a coloured token: the colour was doing the same job twice - the row's
        // own outline already says which answer this is on hover, and a solid accent dot beside a
        // translucent pane read as the one opaque thing on the banner.
        DrawBead(g, numRect, a, hover, seeThrough);
        // Consolas, deliberately: a monospaced digit next to the proportional label reads as an index
        // rather than as part of the sentence, which is what a keyboard shortcut will want when one lands.
        // Ink-centred rather than StringFormat-centred, for the reason LayeredNotch.DrawGlyphCentered
        // already records: StringFormat centring sits a glyph high and left in its blob, and at this size
        // that is plainly visible. Glass ink too - a solid white numeral was the one opaque thing left.
        DrawGlyph(g, number.ToString(), numRect,
            Color.FromArgb((int)(a * (hover ? 225 : 170)), 255, 255, 255), seeThrough ? a : 0f);

        using var sf = Wrap(StringAlignment.Near);
        using var lf = new Font("Segoe UI Semibold", LabelPx, GraphicsUnit.Pixel);
        using var df = new Font("Segoe UI", DescPx, GraphicsUnit.Pixel);

        if (typed != null)
        {
            // The tail, not the head: the caret is where the attention is, so an answer longer than the row
            // scrolls under it rather than ellipsising the end the user is still writing.
            string shown = Tail(g, typed, lf, row.Label.Width - CaretW - 2f);
            // The first live test of this field was answered in Persian, which the LTR path drew with the
            // caret stranded on the wrong side. The direction flag is the whole fix and the alignment must
            // be left alone: DirectionRightToLeft already reverses what Near and Far mean, so setting Far
            // as well put the run back against the left edge with the caret floating away from it.
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
        // the description is the first thing to go over a bright window, so over an app it gives up some
        // of its recessiveness
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

    // Now that the capsule is empty, text lands on whatever is behind the pill, and a plate under it is the
    // thing that was rejected twice. So: a HALO, not a drop shadow. An offset copy puts a second hard edge
    // down one side of every stem - at 12-15px that is a visible fringe, and it is what read as
    // "pixelated". Four cardinal passes darken the backdrop evenly all round instead, and leave the glyph's
    // own antialiased edge as the only edge in the result.
    private static readonly PointF[] Halo =
        [new(-1f, 0f), new(1f, 0f), new(0f, -1f), new(0f, 1f)];

    // Persian reads right to left, and every string on this banner comes from Claude or from the user, so
    // any of them can be. DirectionRightToLeft is the whole fix: it flips what Near and Center mean and
    // puts the ellipsis on the correct end. Applied per STRING rather than per banner - a Persian question
    // over English option labels is the normal case here, not an edge one.
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

    // An EMPTY capsule, not a tinted one. Two directions were tried and both were the same mistake in
    // different clothes - a dark wash, then a light wash - and the answer to both was that the backdrop's
    // colour was being changed at all. A clear capsule changes almost nothing behind it; what makes it an
    // object is its WALL: a lit rim, a second stroke inside that reads as thickness, and a specular streak
    // where the light lands. Everything below is edges. The body is a rounding error on purpose.
    private static Color Body(float a, bool hover)
        => Color.FromArgb((int)(a * (hover ? 18 : 7)), 255, 255, 255);

    private static void DrawVessel(Graphics g, RectangleF r, float a, bool hover, Color accent, bool seeThrough)
    {
        using var path = Rounded(r, RowRadius);
        using (var fill = new SolidBrush(Body(a, hover)))
            g.FillPath(fill, path);

        // One line. A second stroke inside the rim was the textbook way to give glass a wall thickness,
        // and at this scale it does not read as thickness - it reads as two outlines, which is worse than
        // the flat one it was meant to improve on. The streaks below carry the wall instead.
        var clip = g.Clip;
        g.SetClip(path);
        Streak(g, r.X + RowRadius, r.Right - RowRadius, r.Y + 2.6f, a * (hover ? 1.25f : 1f), 132, 1.5f);
        Streak(g, r.X + RowRadius * 1.6f, r.Right - RowRadius * 1.6f, r.Bottom - 2.6f,
            a * (hover ? 1.25f : 1f), 58, 1.2f);
        g.Clip = clip;

        // lit at the top, dimming down the contour: an even outline is a stroke, an uneven one is an edge
        // catching light from above, which is where every light in this pill comes from
        using var rimBrush = new LinearGradientBrush(
            new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2),
            Color.FromArgb((int)(a * (hover ? 165 : 128)), 255, 255, 255),
            Color.FromArgb((int)(a * (hover ? 96 : 58)), 255, 255, 255), 90f);
        using var pen = hover ? new Pen(Mul(accent, a * 0.9f), 0.9f) : new Pen(rimBrush, 0.7f);
        g.DrawPath(pen, path);
    }

    // A bright line that fades out at both ends - the highlight sliding along a curved wall. A plain line
    // would be a drawn rule; the falloff is what makes it light.
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

    // The same empty-glass rule on a sphere: one lit rim, and nothing in the middle. A specular blob was
    // tried up and left of centre - correct for a solid bead, wrong here, because this bead has a digit
    // inside it and the highlight sat on the glyph like a smudge.
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

    // A bead like the option numbers wear, with a cross instead of a digit. Same empty-glass rule, so it
    // reads as part of the panel rather than a chrome button bolted onto it. The strokes carry the same
    // four-pass halo every other mark here does - at these tints the panel guarantees no contrast.
    private static void DrawClose(Graphics g, RectangleF box, float a, bool hover, bool seeThrough)
    {
        DrawBead(g, box, a, hover, seeThrough);
        float m = box.Width * 0.32f;
        float x0 = box.X + m, x1 = box.Right - m, y0 = box.Y + m, y1 = box.Bottom - m;
        if (seeThrough)
        {
            using var halo = new Pen(Color.FromArgb((int)(a * 105), 0, 0, 0), 1.7f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach (var d in Halo)
            {
                g.DrawLine(halo, x0 + d.X, y0 + d.Y, x1 + d.X, y1 + d.Y);
                g.DrawLine(halo, x1 + d.X, y0 + d.Y, x0 + d.X, y1 + d.Y);
            }
        }
        using var pen = new Pen(Color.FromArgb((int)(a * (hover ? 240 : 186)), 255, 255, 255), 1.7f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, x0, y0, x1, y1);
        g.DrawLine(pen, x1, y0, x0, y1);
    }

    // Centred on the glyph's real ink box, not on its advance width and line box. A digit carries side
    // bearings and sits above the baseline, so StringFormat centring lands it high and left of the disc
    // it is supposed to be in the middle of - visible at 28px, and the reason this exists.
    private static void DrawGlyph(Graphics g, string text, RectangleF box, Color ink, float shadow)
    {
        try
        {
            using var path = new GraphicsPath();
            using var family = new FontFamily("Consolas");
            path.AddString(text, family, (int)FontStyle.Bold, NumPx, PointF.Empty, StringFormat.GenericTypographic);
            // Measured on a FLATTENED copy. GetBounds on a curved path returns a box big enough to hold the
            // Bezier control points, which sit outside the ink on every round glyph and nowhere on a
            // straight one - so 1 and 2 centred correctly and 3 came out left of centre, by exactly the
            // overshoot of its own curves. Flattening turns those curves into segments, and the bounds
            // become the ink. The original path is what gets filled, so nothing is coarsened.
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
        // filled through a texture brush rather than drawn and clipped: a hard SetClip leaves the circle's
        // edge aliased, and at 16px that reads as a chewed outline
        using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
        tb.TranslateTransform(circle.X, circle.Y);
        using var p = new GraphicsPath();
        p.AddEllipse(circle);
        g.FillPath(tb, p);
    }

    // Wrapping needs LineLimit cleared - GenericTypographic sets it, and against a rect sized to an exact
    // number of lines that flag drops the last line whenever the metric rounds a hair over. Slack is the
    // other half of the same defence, and costs nothing because the rect below is empty anyway.
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
