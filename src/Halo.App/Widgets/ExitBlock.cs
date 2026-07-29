using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.ClaudeCode;

namespace Halo.Widgets;

// The exit block: whose network this machine leaves by, and - when the tool is routed through a proxy
// - whether it leaves by the same door as everything else.
//
// It lives here rather than in either agent widget because it is not about an agent at all. The
// address, the reputation mark and the dns test are properties of the MACHINE, measured once and
// identical whichever panel is open, so both widgets render the same block from the same code instead
// of one twin reaching into the other - which the two agent modules are deliberately not allowed to do.
// Only the latency series differs per agent, so that is passed in.
internal static class ExitBlock
{
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);
    private static readonly Color Track = Color.FromArgb(38, 255, 255, 255);
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);

    private static Color Mul(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);

    private static GraphicsPath Rounded(RectangleF r, float radius)
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

    // same grid-fitting rule as the panels: a fractional baseline resamples every hinted stem across
    // two pixels and the 12-13px rows arrive soft
    private static float TextTop(Font f, float baseline)
        => MathF.Round(baseline - f.FontFamily.GetCellAscent(f.Style) / (float)f.FontFamily.GetEmHeight(f.Style) * f.Size);

    private static void Text(Graphics g, string t, Font f, Brush b, float x, float baseline)
        => g.DrawString(t, f, b, MathF.Round(x), TextTop(f, baseline), StringFormat.GenericTypographic);

    private static readonly StringFormat AdvanceFmt =
        new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };

    private static float Advance(Graphics g, string t, Font f)
        => t.Length == 0 ? 0f : g.MeasureString(t, f, System.Drawing.Point.Empty, AdvanceFmt).Width;

    // The flag used to be the whole story: a country and nothing else. What you actually want to know
    // about an exit is whose network it is, and - when the API is routed through a proxy - whether the
    // tool is leaving by the same door as everything else. Both are measured; the second line only
    // appears when the two exits genuinely differ, so it stays quiet the rest of the time.
    internal static RectangleF Rect(float colL, float colR) => new(colL, 120, colR - colL, 76);

    // Bilinear reads a 2x2 neighbourhood, so squeezing a 320px flag straight down to the ~52 device px it
    // occupies throws away most of the source and the star came out as a blob. Scaling once through a
    // cached intermediate at the size actually drawn gives the filter something proportionate to chew on,
    // and takes the resample off the per-frame path while it is at it. Bicubic is safe here where it is
    // not on the layered surface: the flag is fully opaque, so there is no premultiplied dark-to-
    // transparent edge for its negative lobes to undershoot.
    private static Bitmap? _flagFit;
    private static Bitmap? _flagFitFrom;
    private static int _flagFitW;

    private static Bitmap FlagFitted(Bitmap src, int wantW)
    {
        if (_flagFit is { } cached && ReferenceEquals(_flagFitFrom, src) && _flagFitW == wantW) return cached;
        int h = Math.Max(1, (int)Math.Round(wantW * (double)src.Height / src.Width));
        var bmp = new Bitmap(wantW, h, PixelFormat.Format32bppPArgb);
        using (var gg = Graphics.FromImage(bmp))
        {
            gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gg.DrawImage(src, new Rectangle(0, 0, wantW, h));
        }
        var old = _flagFit;
        _flagFit = bmp;
        _flagFitFrom = src;
        _flagFitW = wantW;
        old?.Dispose();
        return bmp;
    }

    // The ripple. A cloth flag reads as waving from two cues: the stripes displace vertically, and the
    // facing towards the light changes with the slope. Both are done per column here - a vertical shift
    // plus a brightness term from the slope - which is why a flat red field still looks like it is moving.
    // Sampling clamps at the top and bottom edges instead of going transparent, so the silhouette stays the
    // rounded rectangle it is clipped to and only the CONTENT ripples; letting the edges undulate too fought
    // with the rounded corners rather than adding to them.
    private static Bitmap? _flagWave;

    private static Bitmap Waved(Bitmap src, float phase)
    {
        int w = src.Width, h = src.Height;
        if (_flagWave is null || _flagWave.Width != w || _flagWave.Height != h)
        {
            _flagWave?.Dispose();
            _flagWave = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }
        var dst = _flagWave;

        var sb = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var db = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unsafe
            {
                byte* sp = (byte*)sb.Scan0, dp = (byte*)db.Scan0;
                // The wavefronts are DIAGONAL: the phase advances with y as well as x, so the ripple crosses
                // the cloth at a slant instead of marching straight across in flat vertical columns. And the
                // amplitude ramps from nothing at the left edge to full at the right, because a flag is held
                // at the hoist and free at the fly - that ramp is most of what sells it as cloth rather than
                // as an image being wobbled.
                float amp = h * 0.15f, kx = MathF.Tau * 1.45f / w, ky = MathF.Tau * 0.55f / h;
                for (int x = 0; x < w; x++)
                {
                    float ramp = w < 2 ? 1f : x / (float)(w - 1);
                    ramp *= ramp * (3f - 2f * ramp);   // smoothstep: no visible kink where the cloth is held
                    for (int y = 0; y < h; y++)
                    {
                        float ang = kx * x - ky * y + phase;
                        // Two harmonics, not one. A single sine of 1.45 cycles is short enough that the eye
                        // memorises the shape and reads each pass as the animation restarting; adding the
                        // second one at double the rate and an offset phase makes the cloth wander instead of
                        // marching. The loop stays EXACTLY seamless either way - both terms have period Tau
                        // in `phase`, so the frame after the wrap is the frame that would have come next.
                        float sw = (MathF.Sin(ang) + 0.45f * MathF.Sin(2f * ang + 1.1f)) / 1.45f;
                        float cw = (MathF.Cos(ang) + 0.90f * MathF.Cos(2f * ang + 1.1f)) / 1.90f;
                        float shift = amp * ramp * sw;
                        // the slope is the derivative of the shift: where the cloth turns away it darkens
                        float shade = Math.Clamp(1f + 0.40f * ramp * cw, 0.58f, 1.38f);
                        float sy = Math.Clamp(y - shift, 0, h - 1.001f);
                        int y0 = (int)sy;
                        float f = sy - y0;
                        byte* p0 = sp + y0 * sb.Stride + x * 4;
                        byte* p1 = sp + Math.Min(y0 + 1, h - 1) * sb.Stride + x * 4;
                        byte* o = dp + y * db.Stride + x * 4;
                        for (int c = 0; c < 4; c++)
                        {
                            float v = (p0[c] * (1f - f) + p1[c] * f) * (c == 3 ? 1f : shade);
                            o[c] = (byte)Math.Clamp(v, 0f, 255f);
                        }
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(sb);
            dst.UnlockBits(db);
        }
        return dst;
    }

    // where the dns row landed this frame, so Buttons() can offer exactly that as a hit target
    internal static RectangleF DnsRowRect;

    internal static void Draw(Graphics g, float a, Font body, Font cap,
        float ColR, float RightEdge, int[] api, int empty, int lost)
    {
        // 26x17 was resolution-bound - the TR star had about six device pixels to live in - and 32x21
        // overshot; 28x18 keeps the detail without the flag becoming the loudest thing in the column.
        const float y = 140f, fw = 28f, fh = 18f;
        bool hov = WidgetInput.Over && Rect(ColR, RightEdge).Contains(WidgetInput.Mouse);
        var flag = IpCountry.Flag;
        if (flag != null)
        {
            var old = g.InterpolationMode;
            var oldPx = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var ia = new ImageAttributes();
            // TileFlipXY, or the sampler reaches past the source edge and pulls in transparent black,
            // which fringed the flag's own border darker than the outline drawn over it
            ia.SetWrapMode(WrapMode.TileFlipXY);
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });

            var dst = new RectangleF(ColR, y - fh + 3, fw, fh);
            // how many device pixels this rect really covers, so the cached copy is cut to the size the
            // panel is about to draw rather than a guess
            float sx = g.Transform.Elements[0];
            var fit = FlagFitted(flag, Math.Max(8, (int)MathF.Ceiling(fw * (sx > 0 ? sx : 1f))));
            // ~7s per cycle off the wall clock, so the ripple runs at the same speed whatever fps tier the
            // pill has dropped to. Longer than the 5s it started at: the shorter loop read as hurried, and
            // the faster a repeating pattern goes the more obviously it repeats.
            fit = Waved(fit, Environment.TickCount64 % 7000L / 7000f * MathF.Tau);

            // Rounded corners via a texture brush rather than SetClip: GDI+ clipping is hard-edged whatever
            // the smoothing mode, so a clipped rounded rect comes back with stair-stepped corners, while
            // FillPath antialiases them. The brush transform maps the cached bitmap exactly onto dst, which
            // lands it back at 1:1 in device space once the panel's own scale is applied.
            using var tex = new TextureBrush(fit, new Rectangle(0, 0, fit.Width, fit.Height), ia)
            { WrapMode = WrapMode.TileFlipXY };
            tex.Transform = new Matrix(dst.Width / fit.Width, 0, 0, dst.Height / fit.Height, dst.X, dst.Y);
            using (var shape = Rounded(dst, 4f))
            {
                g.FillPath(tex, shape);
                using var bd = new Pen(Mul(Track, a), 1f);
                g.DrawPath(bd, shape);
            }
            g.PixelOffsetMode = oldPx;
            g.InterpolationMode = old;
        }

        string who = IpCountry.Cc is { Length: > 0 } cc
            ? (IpCountry.Isp is { Length: > 0 } isp ? $"{cc}  ·  {isp}" : cc)
            : "locating…";
        using (var wb = new SolidBrush(Mul(White, a * 0.9f)))
        {
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(who, body, wb, new RectangleF(ColR + fw + 13, TextTop(body, y),
                RightEdge - ColR - fw - 13, body.Size * 1.6f), sf);
        }

        // Resting the block says who and where. Hovering it turns into the audit: the route, a mark out of
        // 100 with the findings that took the points off, and a real dns leak test. The rows are built as a
        // list and laid out in order, because how many there are depends on what is actually wrong - a fixed
        // slot per fact left holes wherever a fact did not apply.
        string? scored = IpCountry.Split ? IpCountry.ApiIp : IpCountry.Ip;
        if (hov)
        {
            IpRep.Want(scored);
            DnsLeak.Want(scored, IpCountry.Split ? IpCountry.ApiCc : IpCountry.Cc);
        }

        using var sf2 = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };

        var rows = new List<(string text, Color col, float alpha, string? lead)>();
        int dnsRow = -1;

        // the split is loud whether or not you are pointing at it: the tool leaving by a different door than
        // everything else is the one thing here you would want to notice without asking
        if (IpCountry.Split)
            rows.Add(($"api exits {IpCountry.ApiCc ?? "?"}  \u00b7  {IpCountry.ApiIp}", Amber, 0.9f, null));
        else if (!hov)
            rows.Add((IpCountry.Ip ?? "", Dim, 0.85f, null));

        if (hov)
        {
            rows.Add((IpCountry.Asn is { Length: > 0 } asn ? $"{asn}  \u00b7  {RouteQuality(api, empty, lost)}" : RouteQuality(api, empty, lost),
                      Dim, 0.85f, null));

            bool repFresh = string.Equals(IpRep.ForIp, scored, StringComparison.Ordinal) && IpRep.Verdict != null;
            bool dnsFresh = string.Equals(DnsLeak.ForIp, scored, StringComparison.Ordinal) && DnsLeak.Done;

            if (!repFresh) rows.Add(("checking exit\u2026", Dim, 0.6f, null));
            else
            {
                // the mark, then the findings behind it on the same line - the number alone would be a thing
                // to trust rather than a thing to read
                int mark = IpRep.Score(IpRep.Tor, IpRep.Abuser, IpRep.Bogon, IpRep.Vpn, IpRep.Proxy,
                                       IpRep.Datacenter, IpRep.Abuse, IpCountry.Split, dnsFresh && DnsLeak.Leaking);
                var markCol = MarkColour(mark);
                // Single-spaced separators here and on the dns row: a figure plus two findings does not fit
                // the column at the spacing the rest of the panel uses. If it still overruns, the abuse
                // label is dropped rather than ellipsised - an ellipsis would eat the verdict, which is the
                // finding that actually explains the mark, and the abuse term is already priced into it.
                string full = $"{mark}/100 \u00b7 {IpRep.Verdict}"
                    + (IpRep.Abuse is { Length: > 0 } ab ? $" \u00b7 abuse {ab}" : "");
                if (Advance(g, full, cap) > RightEdge - ColR) full = $"{mark}/100 \u00b7 {IpRep.Verdict}";
                rows.Add((full, markCol, 0.95f, $"{mark}/100"));
            }

            dnsRow = rows.Count;
            if (!dnsFresh)
                rows.Add((DnsLeak.Running ? "testing dns\u2026" : "dns \u2014", Dim, 0.6f, null));
            else
                rows.Add((DnsLeak.Leaking
                        ? $"dns leak \u00b7 {DnsLeak.Resolvers} resolvers in {DnsLeak.Where}"
                        : $"dns ok \u00b7 {DnsLeak.Resolvers} resolvers in {DnsLeak.Where}",
                    DnsLeak.Leaking ? Red : Green, 0.95f, DnsLeak.Leaking ? "dns leak" : "dns ok"));
        }

        // Press the dns row to run the test again. Recorded from the row's real position rather than
        // recomputed, because how far down it sits depends on whether the exits have split - and the hand
        // cursor reads the same rect, so what looks pressable and what is pressable cannot drift apart.
        DnsRowRect = dnsRow >= 0 && DnsLeak.ForIp != null
            ? new RectangleF(ColR, y + 17 + dnsRow * 16 - 12, RightEdge - ColR, 16)
            : RectangleF.Empty;

        for (int i = 0; i < rows.Count; i++)
        {
            var (text, col, alpha, lead) = rows[i];
            if (text.Length == 0) continue;
            float by = TextTop(cap, y + 17 + i * 16);
            // With a lead, only the VERDICT takes colour and the words after it stay grey - the same rule the
            // usage rows follow, because the label is not the reading.
            if (lead is { Length: > 0 } && text.StartsWith(lead, StringComparison.Ordinal))
            {
                using (var lb = new SolidBrush(Mul(col, a * alpha)))
                    Text(g, lead, cap, lb, ColR, y + 17 + i * 16);
                string rest = text.Substring(lead.Length);
                if (rest.Length > 0)
                    using (var rb2 = new SolidBrush(Mul(Dim, a * 0.85f)))
                        g.DrawString(rest, cap, rb2,
                            new RectangleF(ColR + Advance(g, lead, cap), by,
                                RightEdge - ColR - Advance(g, lead, cap), cap.Size * 1.6f), sf2);
                continue;
            }
            using var rb = new SolidBrush(Mul(col, a * alpha));
            g.DrawString(text, cap, rb, new RectangleF(ColR, by, RightEdge - ColR, cap.Size * 1.6f), sf2);
        }
    }

    // A continuous ramp red -> amber -> green rather than buckets. Bucketing put a 72 in the "fine" band
    // and painted it plain white, which reads as no colour at all; a ramp means every mark is somewhere
    // legible on the scale and neighbouring scores never land on the same swatch.
    private static Color MarkColour(int mark)
    {
        float t = Math.Clamp(mark / 100f, 0f, 1f);
        var (from, to, k) = t < 0.5f ? (Red, Amber, t / 0.5f) : (Amber, Green, (t - 0.5f) / 0.5f);
        return Color.FromArgb(255,
            (int)(from.R + (to.R - from.R) * k),
            (int)(from.G + (to.G - from.G) * k),
            (int)(from.B + (to.B - from.B) * k));
    }

    // the only honest quality figure available: what this route is measuring right now
    private static string RouteQuality(int[] api, int empty, int lost)
    {
        int dropped = 0, seen = 0, last = empty;
        foreach (var v in api) { if (v == empty) continue; seen++; if (v == lost) dropped++; }
        for (int k = api.Length - 1; k >= 0; k--) if (api[k] != empty) { last = api[k]; break; }
        string ms = last == empty ? "…" : last == lost ? "dropped" : $"{last} ms";
        return seen == 0 ? ms : $"{ms}  ·  {dropped}/{seen} lost";
    }
}
