using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Halo.Widgets;

// The pill saying hello - once when it is installed, and briefly at every login.
//
// The signature is a single continuous stroke: 21 cubic beziers, one pen-down to one pen-up, which is
// what makes the write-on possible at all. Drawing it is a dash pattern whose "on" length grows with
// time, exactly the trick the source SVG used (stroke-dasharray over pathLength) - GDI+ can do it
// natively, so this needs no rasteriser and no new dependency.
//
// Everything here is pure drawing off a 0..1 clock. The controller owns the timing, the same way it owns
// the notification and ask morphs, so this file can be eyeballed with --render-greeting on its own.
internal static class Greeting
{
    // Ink bounds of the path as authored: x -145.7..138.3, y -46.9..44.9, centred near the origin.
    private const float InkW = 284f, InkH = 92f;

    private static readonly float[] Stroke =
    [
        -145.66f, 43.747f, -145.66f, 43.747f, -86.107f, 10.264f, -81.851f, -26.162f,
        -79.424f, -46.943f, -98.573f, -44.137f, -101.426f, -23.013f, -103.757f, -5.755f,
        -109.596f, 40.561f, -109.596f, 40.561f, -109.596f, 40.561f, -103.979f, -0.034f,
        -85.851f, 1.753f, -65.936f, 4.083f, -91.979f, 40.05f, -69f, 40.305f,
        -48.573f, 40.532f, -27.639f, 22.688f, -26.873f, 10.943f, -25.99f, -2.599f,
        -44.362f, -4.886f, -50.022f, 11.966f, -55.226f, 27.461f, -43.584f, 44.902f,
        -23.54f, 40.581f, 7.341f, 33.922f, 22.483f, -10.827f, 23.936f, -26.077f,
        25.467f, -42.162f, 13.723f, -43.694f, 6.574f, -29.397f, -0.104f, -16.04f,
        -11.245f, 37.085f, 12.958f, 41.583f, 41.809f, 46.944f, 64.277f, -5.906f,
        67.086f, -23.779f, 69.802f, -41.066f, 58.656f, -45.952f, 50.234f, -30.673f,
        41.166f, -14.223f, 27.843f, 44.077f, 59.937f, 41.326f, 86.746f, 39.028f,
        76.916f, 2.264f, 102.898f, -0.05f, 114.562f, -1.088f, 119.386f, 9.92f,
        118.532f, 21.029f, 117.638f, 32.646f, 106.66f, 42.475f, 95.809f, 40.943f,
        85.898f, 39.544f, 80.838f, 25.973f, 83.425f, 17.072f, 86.617f, 6.094f,
        96.662f, 0.12f, 102.898f, -0.05f, 111.766f, -0.29f, 116.234f, 5.327f,
        124.149f, 5.199f, 131.179f, 5.086f, 138.27f, -2.922f, 138.27f, -2.922f,
    ];

    // English, because docs/decisions.md locks UI strings to English, and lowercase because that is the
    // register the rest of the pill speaks in.
    // Two beats, because it is two things: who this is, and that you are welcome to it. One line saying
    // both would have to be long, and a long line in a hand at this size stops reading as handwriting.
    internal static readonly string[] Lines = ["i'm halo", "welcome"];

    // Only fonts that ship with Windows itself are safe here - Bradley Hand, Brush Script and the rest of
    // the obvious candidates arrive with Office, so a machine without it would silently fall back to a
    // sans and the whole gesture would break. Ink Free is the closest monoline hand of the ones that are
    // actually always there; Segoe Script is the fallback, and the last resort is any font at all.
    private static readonly string[] Hands = ["Ink Free", "Segoe Script", "Segoe Print", "Gabriola"];

    internal static Font LineFont(float px)
    {
        foreach (var name in Hands)
        {
            try
            {
                var f = new Font(name, px, FontStyle.Regular, GraphicsUnit.Pixel);
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            catch { }
        }
        return new Font("Segoe UI", px, FontStyle.Italic, GraphicsUnit.Pixel);
    }

    // Where the ink is allowed to sit inside a pill of this size. Generous, and the same box for both the
    // signature and the line so the two are the same size to the eye: the first attempt fitted the
    // signature to the whole pill and its entry stroke ran off the left edge, because the round cap adds
    // half a pen width beyond the path bounds on every side.
    internal static RectangleF InkBox(float w, float h)
    {
        float mx = w * 0.11f, my = h * 0.20f;
        return new RectangleF(mx, my, w - mx * 2f, h - my * 2f);
    }

    private static GraphicsPath? _path;
    private static float _len;

    // Built once and reused. The length comes off a flattened copy because the write-on has to advance at
    // a constant speed along the ink - pacing by parameter instead makes the pen crawl through the tight
    // loops of the two l's and race across the long entry stroke.
    private static GraphicsPath Path()
    {
        if (_path is not null) return _path;
        var p = new GraphicsPath();
        var cur = new PointF(Stroke[0], Stroke[1]);
        for (int i = 2; i + 5 < Stroke.Length; i += 6)
        {
            var c1 = new PointF(Stroke[i], Stroke[i + 1]);
            var c2 = new PointF(Stroke[i + 2], Stroke[i + 3]);
            var to = new PointF(Stroke[i + 4], Stroke[i + 5]);
            p.AddBezier(cur, c1, c2, to);
            cur = to;
        }
        using (var probe = (GraphicsPath)p.Clone())
        {
            probe.Flatten(null, 0.15f);
            var pts = probe.PathPoints;
            float len = 0f;
            for (int i = 1; i < pts.Length; i++)
                len += MathF.Sqrt(MathF.Pow(pts[i].X - pts[i - 1].X, 2) + MathF.Pow(pts[i].Y - pts[i - 1].Y, 2));
            _len = len;
        }
        _path = p;
        return p;
    }

    private static RectangleF _bounds;

    // measured off the flattened copy, because GetBounds on a curved path returns a box big enough for the
    // bezier control points and every one of these 21 curves overshoots its own ink
    private static RectangleF Bounds()
    {
        if (_bounds.Width > 0f) return _bounds;
        using var probe = (GraphicsPath)Path().Clone();
        probe.Flatten(null, 0.15f);
        _bounds = probe.GetBounds();
        return _bounds;
    }

    // written: 0 = nothing on the page, 1 = the whole signature. alpha fades the finished mark out.
    internal static void DrawHello(Graphics g, RectangleF box, float written, float alpha, Color ink,
        float weight = 9f)
    {
        if (alpha <= 0.004f || written <= 0f) return;
        var path = Path();

        var save = g.Save();
        try
        {
            // Fit and centre on the MEASURED ink, not on the origin. The authored path runs x -145.7..138.3,
            // so its middle is 3.7 units left of 0 and centring on the origin hung the word off-centre in a
            // way that looked like a layout bug. The pen is counted too: a round cap puts half a pen width
            // of ink beyond the path on every side, which is what ran the entry stroke past the left edge.
            var mark = Bounds();
            float grow = weight;
            float scale = MathF.Min(box.Width / (mark.Width + grow), box.Height / (mark.Height + grow));
            g.TranslateTransform(box.X + box.Width / 2f, box.Y + box.Height / 2f);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-(mark.X + mark.Width / 2f), -(mark.Y + mark.Height / 2f));

            using var pen = new Pen(Color.FromArgb((int)(Math.Clamp(alpha, 0f, 1f) * ink.A), ink), weight)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
                DashCap = DashCap.Round,
            };
            if (written < 1f)
            {
                // The dash pattern REPEATS, so the gap has to be at least the whole remaining path or a
                // second copy of the stroke starts drawing further along - which showed up as a stray dot
                // floating past the end of the word. Pattern units are multiples of the pen width.
                float on = MathF.Max(0.001f, _len * written / weight);
                float off = _len * 2f / weight;
                pen.DashPattern = [on, off];
            }
            g.DrawPath(pen, path);
        }
        finally { g.Restore(save); }
    }

    // The other lines are written by the same hand, through Script's authored centrelines.
    //
    // Three cheaper arrivals were built and thrown away first, and all three failed for the same reason -
    // none of them is writing. A clip sweeping across cut glyphs down the middle, so "i'm" spent a beat
    // reading as "i'n". Fading glyph by glyph fixed the cutting but read as a machine dealing out letters.
    // A plain crossfade of the finished words was the calmest and the most obviously not a hand. A font
    // cannot be written on because a font has no centreline; that is the whole reason Script exists.
    internal static void DrawLine(Graphics g, string text, RectangleF box, float written, float alpha,
        Color ink, float weight)
        => Script.Draw(g, text, box, written, alpha, ink, weight);
}
