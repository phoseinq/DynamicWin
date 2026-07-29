using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Halo.ClaudeCode;

namespace Halo.Widgets;

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

    private static float TextTop(Font f, float baseline)
        => MathF.Round(baseline - f.FontFamily.GetCellAscent(f.Style) / (float)f.FontFamily.GetEmHeight(f.Style) * f.Size);

    private static void Text(Graphics g, string t, Font f, Brush b, float x, float baseline)
        => g.DrawString(t, f, b, MathF.Round(x), TextTop(f, baseline), StringFormat.GenericTypographic);

    private static readonly StringFormat AdvanceFmt =
        new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };

    private static float Advance(Graphics g, string t, Font f)
        => t.Length == 0 ? 0f : g.MeasureString(t, f, System.Drawing.Point.Empty, AdvanceFmt).Width;

    internal static RectangleF Rect(float colL, float colR) => new(colL, 120, colR - colL, 76);

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

                float amp = h * 0.15f, kx = MathF.Tau * 1.45f / w, ky = MathF.Tau * 0.55f / h;
                for (int x = 0; x < w; x++)
                {
                    float ramp = w < 2 ? 1f : x / (float)(w - 1);
                    ramp *= ramp * (3f - 2f * ramp);
                    for (int y = 0; y < h; y++)
                    {
                        float ang = kx * x - ky * y + phase;

                        float sw = (MathF.Sin(ang) + 0.45f * MathF.Sin(2f * ang + 1.1f)) / 1.45f;
                        float cw = (MathF.Cos(ang) + 0.90f * MathF.Cos(2f * ang + 1.1f)) / 1.90f;
                        float shift = amp * ramp * sw;

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

    internal static RectangleF DnsRowRect;

    internal static void Draw(Graphics g, float a, Font body, Font cap,
        float ColR, float RightEdge, int[] api, int empty, int lost)
    {

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

            ia.SetWrapMode(WrapMode.TileFlipXY);
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });

            var dst = new RectangleF(ColR, y - fh + 3, fw, fh);

            float sx = g.Transform.Elements[0];
            var fit = FlagFitted(flag, Math.Max(8, (int)MathF.Ceiling(fw * (sx > 0 ? sx : 1f))));

            fit = Waved(fit, Environment.TickCount64 % 7000L / 7000f * MathF.Tau);

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

                int mark = IpRep.Score(IpRep.Tor, IpRep.Abuser, IpRep.Bogon, IpRep.Vpn, IpRep.Proxy,
                                       IpRep.Datacenter, IpRep.Abuse, IpCountry.Split, dnsFresh && DnsLeak.Leaking);
                var markCol = MarkColour(mark);

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

        DnsRowRect = dnsRow >= 0 && DnsLeak.ForIp != null
            ? new RectangleF(ColR, y + 17 + dnsRow * 16 - 12, RightEdge - ColR, 16)
            : RectangleF.Empty;

        for (int i = 0; i < rows.Count; i++)
        {
            var (text, col, alpha, lead) = rows[i];
            if (text.Length == 0) continue;
            float by = TextTop(cap, y + 17 + i * 16);

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

    private static Color MarkColour(int mark)
    {
        float t = Math.Clamp(mark / 100f, 0f, 1f);
        var (from, to, k) = t < 0.5f ? (Red, Amber, t / 0.5f) : (Amber, Green, (t - 0.5f) / 0.5f);
        return Color.FromArgb(255,
            (int)(from.R + (to.R - from.R) * k),
            (int)(from.G + (to.G - from.G) * k),
            (int)(from.B + (to.B - from.B) * k));
    }

    private static string RouteQuality(int[] api, int empty, int lost)
    {
        int dropped = 0, seen = 0, last = empty;
        foreach (var v in api) { if (v == empty) continue; seen++; if (v == lost) dropped++; }
        for (int k = api.Length - 1; k >= 0; k--) if (api[k] != empty) { last = api[k]; break; }
        string ms = last == empty ? "…" : last == lost ? "dropped" : $"{last} ms";
        return seen == 0 ? ms : $"{ms}  ·  {dropped}/{seen} lost";
    }
}
