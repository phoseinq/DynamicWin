using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.Notifications;

namespace Halo.Widgets;

// The pill morphed into a notification banner: app icon + app name + title + one short summary
// line; the grabber bar at the bottom expands it into the full message. Pure rendering — the
// open/close/detail state machine lives in NotchController.
internal static class NotifBanner
{
    public const int W = 470, SummaryH = 112;
    private const float IconD = 52, IconX = 20;
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);

    private static float TextX => IconX + IconD + 14;

    // slightly smaller type overall, shrinking a touch more as the toast's text gets longer
    // (1.0 for short toasts → 0.86 for walls of text), so long titles/bodies fit gracefully
    private static float FontScale(NotifItem n)
    {
        int len = Math.Max(n.Title.Length, n.Body.Length / 2);
        return Math.Clamp(1f - 0.14f * (len - 28) / 50f, 0.86f, 1f);
    }

    // measured height of the detail state (clamped so a novel doesn't eat the screen)
    public static int DetailHeight(NotifItem n)
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            using var f = new Font("Segoe UI", 13f * FontScale(n), GraphicsUnit.Pixel);
            var sz = g.MeasureString(n.Body, f, (int)(W - TextX - 22));
            return Math.Clamp(72 + (int)sz.Height + 22, SummaryH + 26, 280);
        }
        catch { return SummaryH + 70; }
    }

    public static void Draw(Graphics g, int w, int h, float a, NotifItem n, float detail, bool detailOn)
    {
        if (a <= 0.01f) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; // grid-fit hinting → crisper title/body
        if (n.Kind == "language") { DrawCentered(g, w, h, n.Icon, n.Title, a); return; } // badge + language, centred
        var accent = Fx.AccentOf(n.Icon);
        if (accent != Fx.White)
            Fx.Glow(g, w, h, a, IconX + IconD / 2f, SummaryH * 0.45f, w * 0.75f, h * 1.8f, 26, accent);

        // a screenshot banner carries a wide 16:9 preview of the capture instead of a square app icon
        bool hasPreview = n.Preview != null;
        float thumbW = hasPreview ? 128f : IconD, thumbH = hasPreview ? 72f : IconD;
        float iy = (SummaryH - thumbH) / 2f;
        if (hasPreview) DrawThumb(g, n.Preview!, IconX, iy, thumbW, thumbH, a);
        else if (n.Icon != null) DrawAppIcon(g, n.Icon, IconX, iy, IconD, a);
        else
        {
            using var ring = new Pen(Mul(Dim, a), 1.6f);
            using var rp = Fx.Rounded(new RectangleF(IconX, iy, IconD, IconD), IconD * 0.26f);
            g.DrawPath(ring, rp);
            using var f0 = new Font("Segoe UI Semibold", IconD * 0.5f, GraphicsUnit.Pixel);
            using var b0 = new SolidBrush(Mul(White, a));
            using var sf0 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(n.App.Length > 0 ? n.App[..1].ToUpperInvariant() : "•", f0, b0,
                new RectangleF(IconX, iy, IconD, IconD), sf0);
        }

        float tx = IconX + thumbW + 16, tw = w - tx - 22;
        float k = FontScale(n);
        using var eyeF = new Font("Segoe UI", 10.5f * k, GraphicsUnit.Pixel);
        using var titleF = new Font("Segoe UI Semibold", 17.5f * k, GraphicsUnit.Pixel);
        using var bodyF = new Font("Segoe UI", 13f * k, GraphicsUnit.Pixel);

        // eyebrow row: APP NAME (uppercase, muted) on the left, arrival time pinned to the right
        using (var b = new SolidBrush(Mul(Dim, a * 0.82f)))
        {
            string time = RelTime(n.Time);
            float timeW = 0;
            if (time.Length > 0)
            {
                timeW = g.MeasureString(time, eyeF, 999, StringFormat.GenericTypographic).Width;
                using var rf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };
                g.DrawString(time, eyeF, b, new RectangleF(tx, 22, tw, 14), rf);
            }
            using var lf = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(n.App.ToUpperInvariant(), eyeF, b, new RectangleF(tx, 22, Math.Max(10, tw - timeW - 10), 14), lf);
        }
        // Persian/Arabic title/body flow right-to-left; latin runs inside them are reordered by GDI+
        // bidi (DirectionRightToLeft) — without it, mixed FA+EN text gets mangled.
        using (var b = new SolidBrush(Mul(White, a)))
        using (var f = LineFmt(n.Title))
            g.DrawString(n.Title, titleF, b, new RectangleF(tx, 41, tw, 26), f);

        // summary: one short truncated line — it cross-fades into the full wrapped text as the
        // pill grows (detail 0→1), so the reveal reads as one continuous motion
        if (detail < 0.999f && n.Body.Length > 0)
            using (var b = new SolidBrush(Mul(Dim, a * (1f - detail))))
            using (var f = LineFmt(n.Body))
                g.DrawString(n.Body, bodyF, b, new RectangleF(tx, 70, tw, 19), f);
        if (detail > 0.01f && n.Body.Length > 0)
            using (var b = new SolidBrush(Mul(Dim, a * detail)))
            using (var f = WrapFmt(n.Body))
                g.DrawString(n.Body, bodyF, b, new RectangleF(tx, 70, tw, h - 70 - 14), f);

        if (!detailOn && n.Body.Length > 0) // grabber bar = "there's more" (no close button by design)
        {
            bool hov = WidgetInput.Over && WidgetInput.Mouse.Y >= h - 20 && WidgetInput.Mouse.Y <= h
                && Math.Abs(WidgetInput.Mouse.X - w / 2f) < 40;
            using var b = new SolidBrush(Mul(White, a * (hov ? 0.75f : 0.35f) * (1f - detail)));
            using var p = Fx.Rounded(new RectangleF(w / 2f - 18, h - 9, 36, 4), 2f);
            g.FillPath(b, p);
        }
    }

    // app icons are square — clip to a rounded square (not a circle) so we don't frame the logo in
    // a visible disc. transparent icons keep their real silhouette; opaque ones get soft corners.
    private static void DrawAppIcon(Graphics g, Bitmap img, float x, float y, float d, float a)
    {
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
        tb.TranslateTransform(x, y);
        using var path = Fx.Rounded(new RectangleF(x, y, d, d), d * 0.26f);
        g.FillPath(tb, path);
    }

    // wide capture preview (screenshot banner): cover-fit the shot into a rounded 16:9 rect + hairline edge
    private static void DrawThumb(Graphics g, Bitmap img, float x, float y, float w, float h, float a)
    {
        int sw = Math.Max(1, (int)Math.Ceiling(w)), sh = Math.Max(1, (int)Math.Ceiling(h));
        using var scaled = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(scaled))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });
            float ar = (float)img.Width / img.Height, tr = w / h; // cover-fit: crop overflow, no letterbox
            int cw, ch, cx, cy;
            if (ar > tr) { ch = img.Height; cw = (int)(img.Height * tr); cx = (img.Width - cw) / 2; cy = 0; }
            else { cw = img.Width; ch = (int)(img.Width / tr); cx = 0; cy = (img.Height - ch) / 2; }
            sg.DrawImage(img, new Rectangle(0, 0, sw, sh), cx, cy, cw, ch, GraphicsUnit.Pixel, ia);
        }
        using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
        tb.TranslateTransform(x, y);
        using var path = Fx.Rounded(new RectangleF(x, y, w, h), 10f);
        g.FillPath(tb, path);
        using var pen = new Pen(Mul(Color.FromArgb(45, 255, 255, 255), a), 1f);
        g.DrawPath(pen, path);
    }

    // language-switch banner: badge + language name as one horizontally + vertically centred group,
    // over a soft colour glow pulled from the badge so it isn't a flat dark slab
    private static void DrawCentered(Graphics g, int w, int h, Bitmap? icon, string text, float a)
    {
        var accent = icon != null ? Fx.AccentOf(icon) : Fx.White;
        if (accent == Fx.White) accent = Color.FromArgb(255, 95, 145, 235); // never flat
        Fx.Glow(g, w, h, a, w / 2f, SummaryH * 0.5f, w * 0.9f, SummaryH * 1.7f, 34, accent);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var f = new Font("Segoe UI Semibold", 18f, GraphicsUnit.Pixel);
        var sz = g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic);
        float bd = icon != null ? 46f : 0f, gap = icon != null ? 14f : 0f;
        float x0 = (w - (bd + gap + sz.Width)) / 2f, cy = SummaryH / 2f;
        if (icon != null) DrawAppIcon(g, icon, x0, cy - bd / 2f, bd, a);
        using var b = new SolidBrush(Mul(White, a));
        using var sf = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
        g.DrawString(text, f, b, new RectangleF(x0 + bd + gap, cy - sz.Height / 2f, sz.Width + 6, sz.Height), sf);
    }

    // eyebrow timestamp: "now" while fresh, else the wall-clock the toast landed
    private static string RelTime(DateTime t)
        => (DateTime.Now - t) < TimeSpan.FromMinutes(1) ? "now" : t.ToString("HH:mm");

    // any Hebrew..Arabic-Extended char → treat the line as RTL
    private static bool IsRtl(string s)
    {
        foreach (var c in s) if (c >= 0x0590 && c <= 0x08FF) return true;
        return false;
    }

    // single-line, ellipsised, RTL when the text is Persian/Arabic (right-aligned, ellipsis on the left)
    private static StringFormat LineFmt(string s)
    {
        var sf = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        if (IsRtl(s)) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        return sf;
    }

    // wrapped (detail view) — same RTL rule, but allow wrapping
    private static StringFormat WrapFmt(string s)
    {
        var sf = new StringFormat(StringFormat.GenericTypographic);
        if (IsRtl(s)) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        return sf;
    }

    private static Color Mul(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);
}
