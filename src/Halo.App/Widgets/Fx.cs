using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace Halo.Widgets;

// shared visual effects: icon-accent extraction + the soft accent glow used across widgets/circles
internal static class Fx
{
    public static readonly Color White = Color.FromArgb(238, 255, 255, 255);

    // The net-health graph labels. They live here because ClaudeCodeWidget and CodexWidget each drew
    // them four different ways ("net " + x, $"net {x}", …) — eight literals for two words. The pt-BR
    // pull request translated some of those spellings and not the neighbouring ones; one source now.
    public const string NetLabel = "net";
    public const string ApiLabel = "api";
    public const string LossLabel = "loss";

    // Media/app titles often arrive dressed in decorative Unicode — 𝗺𝗮𝘁𝗵-𝗯𝗼𝗹𝗱, 𝓈𝒸𝓇𝒾𝓅𝓉, ｆｕｌｌｗｉｄｔｈ,
    // ﷼ ligatures — that Segoe UI has no glyph for, so they render as tofu boxes. NFKC folds each to its
    // plain equivalent (𝗙𝗨𝗧𝗕𝗔𝗟𝗟𝗜 𝟭𝟴+ → FUTBALLI 18+, Arabic presentation forms → normal Persian);
    // ordinary Latin/Persian/CJK pass through untouched. Guards the rare malformed string Normalize rejects.
    public static string CleanText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        try { return s.IsNormalized(System.Text.NormalizationForm.FormKC) ? s : s.Normalize(System.Text.NormalizationForm.FormKC); }
        catch { return s; }
    }

    // any Hebrew..Arabic char → lay the line out right-to-left (Persian/Arabic titles read from the right)
    public static bool IsRtl(string? s)
    {
        if (s == null) return false;
        foreach (var c in s) if (c >= 0x0590 && c <= 0x08FF) return true;
        return false;
    }

    // A dash inside Persian text loses the space on ONE side. Reported from a live banner, and reproduced
    // with the same font and format the banner uses: "...mi-bini SP EMDASH SP tirgi..." draws the dash
    // welded to the following word. The dash is a bidi NEUTRAL, and resolving the neutral run swallows the
    // whitespace at the direction change. Four fixes were rendered side by side before this one: an ascii
    // hyphen does not have the problem at all, NBSP on both sides does NOT help, an RLM after the dash does
    // NOT help, and an RLM on BOTH sides restores both spaces - so the dash gets pinned into the RTL run
    // from either side. RLM is zero-width, so nothing that measured the original string moves.
    //
    // Display only, and only for text that is already RTL: this is the last step before drawing, never
    // something written back to whatever the string came from.
    public static string PinRtlDashes(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (s.IndexOf(EmDash) < 0 && s.IndexOf(EnDash) < 0) return s;
        if (!IsRtl(s)) return s;   // latin text lays the same dash out correctly on its own
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != EmDash && c != EnDash) { sb.Append(c); continue; }
            if (i == 0 || s[i - 1] != Rlm) sb.Append(Rlm);
            sb.Append(c);
            if (i + 1 >= s.Length || s[i + 1] != Rlm) sb.Append(Rlm);
        }
        return sb.ToString();
    }

    // escapes, not the characters: RLM is INVISIBLE, and a literal one sitting in source is unreviewable
    private const char EmDash = '\u2014', EnDash = '\u2013', Rlm = '\u200F';

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
                // Noise SCALED BY the falloff, not added to it. Added noise left every pixel outside the
                // inscribed circle sitting at alpha 1-5 instead of 0, so the texture's own square boundary
                // stayed faintly lit and the "soft glow" read as a rectangle inside the expanded pill.
                // Scaling keeps the de-banding where the gradient actually bands and lets the edge reach 0.
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

    // very soft radial wash of the accent, clipped to the pill shape
    // (flat top flush to the screen edge + rounded bottom — same as LayeredNotch.PillPath;
    // an all-corners clip leaves a dark crescent at the top corners)
    // alpha is a float, and deliberately so: it only ever reaches GDI+ through Matrix33 below, which is
    // float the whole way. Declaring it int quantised every caller AT THE CALL SITE - PillBar's breathing
    // glows landed on (int)(16*0.5*lit) and (int)(13*0.5*lit), which is four levels and three, so a pulse
    // meant to be a slow swell stepped visibly through a handful of brightnesses. Nothing about the drawing
    // needed the integer; the cast was the banding.
    public static void Glow(Graphics g, int w, int h, float fade, float cx, float cy,
        float rx, float ry, float alpha, Color accent)
    {
        if (accent == White || fade <= 0.01f) return;
        using var clip = PillClip(w, h);
        var old = g.Clip;
        // Intersect, NOT replace. SetClip(path) defaults to Replace, which threw away whatever clip the caller
        // had set around the call - so PillBar's "keep the glows inside the filled part" did nothing, and the
        // halo went on spilling past the wavefront. Drawn before the fill, that spill is a faint band lying
        // UNDER the bar and reaching further right than it: the reported "two bars, a pale one ahead and a
        // solid one behind". With no caller clip set, g.Clip is the whole surface and this is identical to
        // what it did before.
        g.SetClip(clip, CombineMode.Intersect);
        var oldInterp = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        // Do NOT fold the alpha into the colour channels here. It looks like it should be needed, because
        // the texture is premultiplied — but GDI+ un-premultiplies before applying a ColorMatrix and
        // re-premultiplies after, so scaling RGB by the alpha too applies it twice and crushes the tint to
        // grey (measured: a green accent came out 11,11,11 with zero saturation).
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix
        {
            Matrix00 = accent.R / 255f, // tint the white texture to the accent
            Matrix11 = accent.G / 255f,
            Matrix22 = accent.B / 255f,
            Matrix33 = alpha * fade / 255f,
        });
        // Clamp, not TileFlipXY: mirrored tiling samples the texture's far edge back in along the seams,
        // which is another way to put a straight line where the glow is supposed to have vanished.
        ia.SetWrapMode(WrapMode.Clamp);
        g.DrawImage(GlowTex, new Rectangle((int)(cx - rx), (int)(cy - ry), (int)(rx * 2), (int)(ry * 2)),
            0, 0, GlowTex.Width, GlowTex.Height, GraphicsUnit.Pixel, ia);
        g.InterpolationMode = oldInterp;
        g.Clip = old;
    }

    // "the pill IS the bar": instead of a separate progress bar, wash the whole silhouette with the app's
    // own accent — a deeper, duller shade as the track, the accent itself as the fill, and a brighter lip
    // at the wavefront so the leading edge reads as light rather than a cut. Lives here because both the
    // download pill (bold) and the agent pills (a whisper behind the content) need exactly this, and the
    // last thing this codebase needs is a third copy of the same drawing.
    // `strength` scales the whole effect; the caller draws its icon AFTERWARDS so the fill passes behind it.
    public static void PillBar(Graphics g, int w, int h, float fade, float frac, Color accent, float strength,
                               bool alive = false)
    {
        if (accent == White || fade <= 0.01f || strength <= 0f) return;
        frac = Math.Clamp(frac, 0f, 1f);
        // A dark cover art gives a dark accent, and every colour below is derived from it by taking value
        // AWAY - the track sits at v*0.34. On a black-ish album that is black paint on black glass: the bar
        // is drawn in full and simply cannot be seen. So the accent gets a floor before anything is derived
        // from it, hue kept (a dark red cover still gives a red bar) and a little saturation put back so the
        // lift doesn't just produce grey. Bright accents are already above the floor and pass through
        // untouched, which is why the amber filmstrip looks the same as before.
        RgbToHsv(accent, out float ah, out float asat, out float av);
        if (av < 0.62f)
            accent = HsvToRgb(ah, asat < 0.12f ? asat : Math.Max(asat, 0.42f), 0.62f);
        // Inset by a hair. Filling exactly the same path the shell uses for the window silhouette left a
        // saturated line of accent along the rounded bottom: two antialiased edges landing on the same
        // pixel row add up instead of blending, so the outermost row ended up at full colour. Half a pixel
        // in is invisible and keeps the bar strictly inside the glass.
        using var pp = PillPath(w, h, h / 2f, 0.5f);
        // The track was Shade(accent, 1), which is MORE saturated as well as darker — on a yellow accent
        // that is olive, and the whole pill read as dirty. A track wants to recede: same hue, most of the
        // saturation taken out, so the fill is the only saturated thing on the pill.
        RgbToHsv(accent, out float th, out float ts, out float tv);
        var track = HsvToRgb(th, ts * 0.42f, Math.Max(0.16f, tv * 0.34f));
        // a desaturated track needs more alpha to be seen at all, but the agent pills want it to stay a
        // whisper, so the extra weight is spent only where the effect is already bold
        using (var tb = new SolidBrush(Alpha(track, fade * strength * (0.34f + 0.28f * strength))))
            g.FillPath(tb, pp);
        if (frac <= 0.001f) return;

        // The fill used to be a rectangle drawn inside g.SetClip(pill). GDI+ clipping is region-based and
        // regions have no antialiasing, so the pill's curved LEFT edge came out visibly stair-stepped while
        // the straight right-hand wavefront looked fine. Filling the PATH itself keeps the silhouette
        // antialiased; the horizontal cut is done by a gradient that goes opaque→transparent over ~1px, so
        // the wavefront stays crisp without a clip.
        float fill = w * frac;
        // One breath, applied to everything the bar is already made of. The first attempt added a separate
        // bright band at the wavefront, and at this strength the fill sits around 18% alpha while that band
        // peaked near 36% - a stripe twice as bright as the bar it rides on, which is why it read as a
        // detached piece rather than as the bar being alive. Nothing is added now; the body, its halo, its
        // lip and its wavefront all rise and fall together.
        float breath = alive ? 0.5f - 0.5f * MathF.Cos(Environment.TickCount64 % 2400 / 2400f * MathF.Tau) : 0f;
        // Range widened twice: at the start of a track the fill is a few pixels wide and a gentle wash
        // over it is invisible, so the swing has to be big enough to read on a short bar too.
        float lit = alive ? 0.78f + 0.42f * breath : 1f;
        var solid = Alpha(accent, fade * 0.52f * strength * lit);

        // Two glows, not one. With a single glow at the wavefront everything behind it was one flat sheet
        // of colour — a printed block, not a lit surface. This wide, dim halo goes UNDER the fill, so the
        // colour varies across the filled body instead of sitting at one value; the tight one at the end
        // is the light riding the leading edge and is drawn last, on top.
        if (fill > 6f)
        {
            var oldG = g.Clip;
            g.SetClip(new RectangleF(0, 0, fill, h), CombineMode.Intersect);
            Glow(g, w, h, fade, fill * 0.45f, h * 0.44f, Math.Max(fill, h * 1.2f), h * 1.9f,
                 16 * strength * lit, accent);
            g.Clip = oldG;
        }

        if (frac >= 0.999f) { using (var fb = new SolidBrush(solid)) g.FillPath(fb, pp); }
        else
        {
            // ~2.5px of falloff at the wavefront rather than a quarter of a pixel. The razor cut read as a
            // sheet of colour scissored off; over a couple of pixels, with the lip behind it, the same edge
            // reads as light running out — and it is still far too narrow to look like a soft gradient.
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

        // A sheen down the filled part. Light falling off from the top is the whole difference between
        // "block of paint" and "lit glass", and it costs one gradient. Clipped by a straight-edged rect
        // only — the pill's curve still comes from the path, because a region clip has no antialiasing.
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
                    Alpha(Color.FromArgb(0, 0, 0), fade * 0.10f * strength),   // a touch of shadow at the foot
                },
            };
            var oldC = g.Clip;
            g.SetClip(new RectangleF(0, 0, fill, h), CombineMode.Intersect);
            g.FillPath(sheen, pp);
            g.Clip = oldC;
        }

        // A gentle lift toward the wavefront so the edge reads as light rather than a cut. It used to ramp up
        // to 94% of its width and then fall back to nothing over the last 6% - about two and a half pixels of
        // bright-then-dark right at the head, which is the "خط" that appeared between the head and the body.
        // There is no drop-back now: the light rises into the head and simply stops where the fill stops,
        // because the clip ends there. Dimmer, too - a head several times brighter than its own bar was the
        // other half of why this read as a separate object being dragged along.
        if (fill > 8f && strength >= 0.5f)
        {
            float lipW = Math.Min(38f, fill), x0 = fill - lipW;
            using var lip = new LinearGradientBrush(new RectangleF(x0 - 0.5f, 0, lipW + 1f, h),
                Color.FromArgb(0, accent), Alpha(accent, fade * 0.3f * strength * lit),
                LinearGradientMode.Horizontal);
            var old = g.Clip;
            g.SetClip(new RectangleF(x0, 0, lipW, h), CombineMode.Intersect); // straight edges only → no jaggies
            g.FillPath(lip, pp);
            g.Clip = old;
        }

        // the second glow: tight and bright, sitting on the wavefront
        // Clipped to the filled part like everything else. A glow centred ON the wavefront puts half of itself
        // PAST it, so after the fill's own crisp edge there was a soft detached blob lying on the empty track -
        // the head reading as two pieces. Nothing exists to the right of the wavefront now except the track.
        if (fill > 6f)
        {
            var oldG = g.Clip;
            g.SetClip(new RectangleF(0, 0, fill, h), CombineMode.Intersect);
            Glow(g, w, h, fade, fill, h / 2f, h * 1.1f, h * 1.45f,
                 13 * strength * lit, accent);
            g.Clip = oldG;
        }
    }

    // GDI+ centres the EM BOX, which reserves descender space, so a latin string with no descenders
    // ("outta juice", "back in 2h 5m") reads slightly low and the widgets compensated with a hardcoded
    // -1.5px lift. That is only right at one font size: these pills scale their text down to fit, so at
    // small sizes the fixed lift over-corrected and the text drifted upward. Derive the correction from
    // the font's own metrics instead, so it scales — returns how far to LIFT the rect.
    public static float CenterLift(Font f)
    {
        try
        {
            var ff = f.FontFamily;
            var st = f.Style;
            float em = ff.GetEmHeight(st);
            if (em <= 0) return 0f;
            float line = (ff.GetCellAscent(st) + ff.GetCellDescent(st)) / em; // line height, in ems
            float baseline = ff.GetCellAscent(st) / em;                        // baseline from line top
            const float capRatio = 0.70f;              // cap height of the Segoe UI faces used here
            float visual = baseline - capRatio / 2f;   // where the glyphs actually look centred
            return (visual - line / 2f) * f.Size;      // >0 means the text sits low by that much
        }
        catch { return 0f; }
    }

    // CenterLift works off font metrics, which is right for a run of latin text but wrong for an icon font:
    // the metrics describe the line box, and two glyphs of the SAME icon font can have completely different
    // ink heights. Measured in the copy-code pill at 4x: the page icon read 1.4px high while the check
    // beside it read spot on. So an icon is centred on its own ink instead, which is the same conclusion
    // LocalBadge reached for Fluent glyphs ("metric-centred Fluent glyphs read visibly off").
    //
    // Returns the offset to ADD to a DrawString origin's y so the glyph's ink centres on that y. Cached:
    // building a GraphicsPath per glyph per frame would be silly on the render path.
    private static readonly Dictionary<string, PointF> _inkOffsets = new();

    /// <summary>
    /// Both offsets to ADD to a DrawString origin (GenericTypographic, so the origin is the top-left of the
    /// line box) to put the glyph's ink centre exactly on that point.
    ///
    /// The horizontal one matters for the same reason the vertical one does: StringAlignment.Center centres
    /// the ADVANCE WIDTH, and an icon font's advance says no more about where its ink sits than its line box
    /// does. A fallback glyph centred that way sat visibly off inside the pill.
    /// </summary>
    public static PointF InkCentreOffsets(Font f, string s)
    {
        if (string.IsNullOrEmpty(s)) return PointF.Empty;
        string key = f.FontFamily.Name + "|" + f.Style + "|" + f.Size.ToString("0.##") + "|" + s;
        lock (_inkOffsets)
        {
            if (_inkOffsets.TryGetValue(key, out var v)) return v;
            var off = PointF.Empty;
            try
            {
                using var path = new GraphicsPath();
                using var sf = new StringFormat(StringFormat.GenericTypographic);
                path.AddString(s, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, sf);
                var b = path.GetBounds();
                if (b.Width > 0 && b.Height > 0)
                    off = new PointF(-(b.Left + b.Width / 2f), -(b.Top + b.Height / 2f));
            }
            catch { }
            _inkOffsets[key] = off;
            return off;
        }
    }

    public static float InkCentreOffset(Font f, string s) => InkCentreOffsets(f, s).Y;

    /// <summary>
    /// Stroke the first <paramref name="frac"/> of a path's perimeter, clockwise from its start point — a
    /// progress ring for something that is not a circle. The collapsed pill's art is a rounded square, so
    /// <c>DrawArc</c> has nothing to draw on; flattening the path and walking its length does.
    /// </summary>
    public static void PathProgress(Graphics g, GraphicsPath path, float frac, Pen pen)
    {
        if (frac <= 0f) return;
        using var flat = (GraphicsPath)path.Clone();
        flat.Flatten(null, 0.2f);
        var pts = flat.PathPoints;
        if (pts.Length < 2) return;

        float total = 0f;
        var seg = new float[pts.Length];              // length of the segment ENDING at i
        for (int i = 1; i < pts.Length; i++)
        {
            float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            seg[i] = MathF.Sqrt(dx * dx + dy * dy);
            total += seg[i];
        }
        if (total <= 0f) return;

        float want = Math.Clamp(frac, 0f, 1f) * total, run = 0f;
        for (int i = 1; i < pts.Length; i++)
        {
            if (run + seg[i] <= want) { g.DrawLine(pen, pts[i - 1], pts[i]); run += seg[i]; continue; }
            // the last segment is cut where the fraction actually falls, or the ring would advance in
            // visible steps of whatever the flattening happened to produce
            float k = seg[i] > 0f ? (want - run) / seg[i] : 0f;
            if (k > 0.001f)
                g.DrawLine(pen, pts[i - 1], new PointF(
                    pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k,
                    pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k));
            return;
        }
    }

    /// <summary>
    /// One icon-font glyph, centred on its own ink inside <paramref name="r"/> — the thing every fallback
    /// glyph wants and that three places had each worked out separately (the copy pill, LocalBadge, and the
    /// media art). DrawString rather than a filled path, so small glyphs keep their hinting.
    /// </summary>
    public static void GlyphCentred(Graphics g, RectangleF r, string glyph, Font f, Brush brush)
    {
        var off = InkCentreOffsets(f, glyph);
        using var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(glyph, f, brush, new PointF(r.X + r.Width / 2f + off.X, r.Y + r.Height / 2f + off.Y), sf);
    }

    // Same trick for text, but measured on a CAP reference rather than on the string itself: centring a
    // string on its own ink makes "Copied" (descender) and "482913" (none) sit at different heights, so the
    // label would bob as the state flips. One reference glyph keeps every string on one line.
    public static float CapCentreOffset(Font f) => InkCentreOffset(f, "H");

    public static Color Alpha(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);

    // flat top flush to the screen edge + rounded bottom (matches LayeredNotch.PillPath)
    private static GraphicsPath PillClip(int w, int h) => PillPath(w, h, Math.Min(h / 2f, 30f));

    // flat-top / rounded-bottom pill silhouette. Public so effects that wash the whole pill (the
    // compacting pulse) fill the TRUE outline — a fully-rounded rect over this flat top left two
    // dark crescents at the top corners ("از بالا حلاله").
    public static GraphicsPath PillPath(int w, int h, float r) => PillPath(w, h, r, 0f);

    // `inset` pulls the path in from every edge. Callers that FILL the same silhouette the window itself
    // is shaped by want a fraction of a pixel of inset, or the two antialiased edges stack on the same
    // pixel row and produce a hard line of colour along the rounded bottom.
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

    // icon + small dark corner badge with one character (session number / surface letter)
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

    // progressively deeper/more saturated shade per duplicate session, so twin green rings differ
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

    // wind-blown flag ghost for the panels: a gentle ripple distributed across the WHOLE flag
    // (several small waves, no single lifted section) + a smooth vignette — strongest in the
    // centre, gradually melting away toward the edges. Baked once per flag at 2x, drawn faint.
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
            float ph = x / (float)fw * MathF.Tau * 2.4f; // several small ripples spread evenly
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
                // alpha edges use the true (unclamped) rows so the waved outline fades in softly
                float aa = (y0 >= 0 ? sb[ia + 3] : 0) * (1f - fr) + (y0 + 1 < fh ? sb[ib + 3] : 0) * fr;
                float ey = (sy - fh / 2f) / (fh / 2f);
                float fadeY = Math.Max(0f, 1f - ey * ey);
                float alpha = aa / 255f * fadeX * fadeY;
                if (alpha <= 0.004f) continue;
                int o = y * dst.Stride + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    float ch = (sb[ia + c] * (1f - fr) + sb[ib + c] * fr) * shade;
                    ob[o + c] = (byte)(Math.Min(ch, 255f) * alpha); // premultiplied
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

    // draw the ghost centred in a panel, very faint
    public static void DrawFlagGhost(Graphics g, System.Drawing.Bitmap? flag, int w, int h, float a)
    {
        if (flag is null) return;
        var ghost = FlagGhost(flag);
        const int gw = 210;
        int gh = ghost.Height * gw / ghost.Width;
        DrawFlagGhost(g, flag, new RectangleF((w - gw) / 2f, (h - gh) / 2f + 4, gw, gh), a);
    }

    // Same ripple, placed where the caller wants it and at whatever size. A 210px watermark across the
    // middle of a panel competes with the text sitting on top of it; a small one parked in dead space
    // still says which exit the route is taking without being the first thing the eye lands on. Alpha
    // rises as it shrinks, because the ghost is faint enough to vanish entirely at a small size.
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

    // ±Ns seek mark, YouTube-style: a near-full circular arc with a gap at the top, an arrowhead at
    // the gap edge pointing in the seek direction, and the seconds number centred inside.
    public static void DrawSeekArrow(Graphics g, RectangleF chip, bool forward, float alpha, string label = "10")
    {
        var c = Color.FromArgb((int)(238 * alpha), 255, 255, 255);
        float cx = chip.X + chip.Width / 2f, cy = chip.Y + chip.Height / 2f, r = chip.Width * 0.30f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const float gap = 80f;
        using (var pen = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 270f + gap / 2f, 360f - gap);
        // arrowhead at the END of travel: forward = clockwise → head at the LEFT gap edge pointing
        // clockwise (right/up); backward = counterclockwise → head at the RIGHT gap edge pointing left/up
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

    // bare "CC" toggle mark (no chip circle — it's a toggle, not transport)
    public static void DrawCcMark(Graphics g, RectangleF chip, float alpha)
    {
        using var f = new Font("Segoe UI Semibold", chip.Width * 0.34f, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Color.FromArgb((int)(238 * alpha), 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("CC", f, b, chip, sf);
    }

    // clean miniplayer mark: a frame with a diagonal arrow shrinking INTO its bottom-right corner —
    // "خودش کوچیک می‌شه اون گوشه". No inner rect, no chip circle, small.
    public static void DrawPipMark(Graphics g, RectangleF chip, float alpha)
    {
        var c = Color.FromArgb((int)(238 * alpha), 255, 255, 255);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = chip.Width * 0.44f, h = w * 0.72f;
        var f = new RectangleF(chip.X + (chip.Width - w) / 2f, chip.Y + (chip.Height - h) / 2f, w, h);
        using (var op = Rounded(f, 2f))
        using (var pen = new Pen(c, 1.5f))
            g.DrawPath(pen, op);
        // arrow: upper-left third → bottom-right inner corner
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
    // How spent a usage window is, as a colour. Lives here because both agent panels draw the same
    // meaning and they had drifted: Claude ramped green->amber->red THROUGH HUE, Codex lerped
    // blue->amber in RGB, which passes through grey - so a Codex window at 61% rendered as a dead
    // grey ring while the same 61% on the Claude panel was clearly amber. Interpolating hue keeps
    // every value on the scale legible; a straight RGB lerp between opposing hues does not.
    internal static Color UsageColor(float f) =>
        f <= 0.5f ? UsageGreen
        : f <= 0.75f ? HueLerp(UsageGreen, UsageAmber, (f - 0.5f) / 0.25f)
        : HueLerp(UsageAmber, UsageRed, Math.Clamp((f - 0.75f) / 0.25f, 0f, 1f));

    private static readonly Color UsageGreen = Color.FromArgb(62, 207, 92);
    private static readonly Color UsageAmber = Color.FromArgb(255, 176, 32);
    private static readonly Color UsageRed = Color.FromArgb(229, 72, 77);

    // The warm end of the status ring. Deliberately an orange and NOT UsageRed: red on that ring means a
    // failure, and a session merely under pressure must never be able to arrive at it.
    private static readonly Color RingHot = Color.FromArgb(255, 122, 36);

    /// <summary>
    /// How many characters of ordinary lowercase text fit in <paramref name="avail"/> pixels at
    /// <paramref name="px"/>, measured with the real font rather than guessed from an average. The voice has
    /// to know its budget BEFORE it picks a line (see <c>MoodContext.MaxChars</c>), or the renderer ends up
    /// shrinking a too-long line until it is present but unreadable.
    /// </summary>
    internal static int FitChars(Graphics g, float avail, float px)
    {
        if (avail <= 4f || px <= 1f) return 0;
        try
        {
            using var f = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
            // a sample of the kind of text these lines actually are: one glyph would be meaningless on a
            // proportional face, and "iiii" or "MMMM" would each be wrong in a different direction
            const string sample = "the quick brown fox jumps over it";
            float em = g.MeasureString(sample, f, int.MaxValue, StringFormat.GenericTypographic).Width
                / sample.Length;
            return em > 0.5f ? (int)MathF.Floor(avail / em) : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// The colour for a mood slot — the ring's hue comes from the SAME slot the words come from, so the
    /// two cannot drift: whatever the pill is saying, the ring is the colour of that. Four flat states
    /// could not carry this; a dozen can, because each one is a thing the product also names out loud.
    ///
    /// Grouped by what the agent is doing to your work rather than by tool: running something is green,
    /// reading what is here is cyan, fetching from outside is teal, putting something back out is violet,
    /// looking through things is lime, laying a plan is gold, somebody else doing it is magenta, waiting on
    /// something outside is slate, your turn is pink, thinking is amber. A slot with no colour of its own is
    /// green, which is simply "working".
    ///
    /// TEN of them, and the number is bounded by measurement rather than taste: a test holds every pair 85
    /// apart in rgb and every modulated colour nearer its own calm self than anyone else's, and the first
    /// attempt at thirteen failed it — 27 degrees apart is one colour on a 2px ring. Ten passes because the
    /// pressure modulation is gentle now (it was overwriting the hue when this was first tried) and because
    /// each addition moved its neighbour: the lime went greener to make room for the gold.
    /// </summary>
    internal static Color SlotColor(string? slot) => slot switch
    {
        "running" => Color.FromArgb(62, 207, 92),                  // green: something is executing
        "reading" or "peeking" => Color.FromArgb(53, 208, 232),     // cyan: taking in what is here
        "fetching" or "searching" => Color.FromArgb(20, 190, 175),  // teal: taking in from outside
        "writing" or "patching" or "publishing"
            => Color.FromArgb(169, 139, 255),                      // violet: putting something back out
        // lime shifted greener to make room for the gold beside it: at (191,215,62) it was 70 units from the
        // gold and the two read as one colour on the ring
        "digging" or "reviewing" => Color.FromArgb(170, 220, 50),   // lime: looking through what is here
        "planning" or "plotting" or "skill"
            => Color.FromArgb(240, 196, 60),                       // gold: laying it out before doing it
        // a DEEP magenta, not the bright one it started as: "somebody else's turn" and "your turn" sit next
        // to each other on the wheel, and at full pressure the bright magenta came out nearer the pink than
        // its own calm colour. They differ in lightness as well as hue now, which is the part that survives
        // being saturated and dimmed.
        "delegating" or "consulting"
            => Color.FromArgb(190, 80, 175),                       // magenta: somebody else is doing it
        // deliberately the quietest hue here, because that is what the state means: nothing of ours is
        // running, we are waiting on something outside
        "watching" => Color.FromArgb(150, 160, 200),               // slate: waiting on something else
        "asking" => Color.FromArgb(255, 95, 138),                  // pink: this one is addressed to YOU
        "unknown" => Color.FromArgb(255, 150, 26),                 // amber: thinking, nothing to show yet
        "compacting" => Color.FromArgb(91, 157, 255),              // blue, as it has always been
        _ => Color.FromArgb(62, 207, 92),
    };

    /// <summary>
    /// The status ring, modulated by the situation it sits in. The ring was four flat colours while what
    /// it describes is a continuum - a context window filling up, a usage window emptying, a turn dragging
    /// on - so the state keeps setting the hue family (green on a tool, amber thinking, white idle) and
    /// pressure warms it from there. Everything here is a lerp, so it drifts over minutes rather than
    /// snapping between more colours you would have to learn; the caller is expected to keep the states
    /// whose colour IS the message (an outage, a spent limit, a running compact) out of it.
    /// </summary>
    internal static Color MoodRing(Color state, in Halo.Agents.MoodContext ctx, bool hueIsFree = false)
    {
        // each pressure ramps only across the band where it starts to matter: 0 below it, 1 at the top
        float squeeze = MathF.Max(Ramp(ctx.ContextFrac, 0.55f, 0.95f), Ramp(ctx.UsageFrac, 0.70f, 0.98f));
        float drag = ctx.Running is { } r ? Ramp((float)r.TotalMinutes, 2f, 12f) : 0f;
        float lift = MathF.Max(squeeze, 0.55f * drag);
        var c = state;

        // The rule that took three attempts to find: a hue that says WHICH activity this is may not be
        // repurposed by pressure, and a hue that says nothing may be. Warming everything toward orange
        // erased the slot at 0.85 and impersonated another slot at 0.6 - a squeezed green landing on the
        // lime of "digging" is worse than losing the signal, because it is wrong rather than vague.
        //
        // hueIsFree is true for exactly two states: thinking, whose amber means only "no news", and idle,
        // whose white means nothing at all. Those two have nothing to protect, so pressure gets their hue
        // outright - which is where it is most wanted anyway, since an idle pill on a nearly-full context
        // is precisely the moment you want the ring to catch your eye.
        if (hueIsFree)
        {
            var target = HueLerp(UsageAmber, RingHot, squeeze);
            // 0.85 turned the thinking amber fully orange from about 60% context on, and since thinking is
            // where a turn spends most of its time, "the ring is always orange" was the honest report of it.
            // Halved: amber stays amber, and the orange is kept for the top of the band where it is news.
            c = HueLerp(c, target, MathF.Max(0.45f * squeeze, 0.18f * drag * (1f - squeeze)));
        }

        // For everyone else pressure goes where it cannot be mistaken for a different state: the same lamp
        // turned up. Saturation only for the activity hues (white has none to add), value for all.
        RgbToHsv(c, out float h, out float s, out float v);
        c = HsvToRgb(h,
            Math.Clamp(s + (hueIsFree ? 0f : 0.10f) * lift, 0f, 1f),
            Math.Clamp(v + 0.10f * lift, 0f, 1f));

        // The small hours get a quieter ring, and the amount is bounded by the palette rather than by taste:
        // at 0.86 the dim moved a colour about as far as the distance between two neighbouring slot hues, so
        // a dimmed violet came out nearer the magenta than its own daytime self. Everything here has to stay
        // small enough that a modulated ring is still nearest ITSELF, which is what the test measures.
        if (ctx.Hour is >= 0 and <= 5) c = Scale(c, 0.93f);
        // hsv carries no alpha, and the idle ring is deliberately not fully opaque (238) - without this the
        // ring got BRIGHTER as the session tightened, which is not what any of the above is saying
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
