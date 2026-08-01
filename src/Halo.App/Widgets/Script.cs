using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Halo.Widgets;

// A single-stroke hand, so the pill can WRITE its other lines instead of fading them in.
//
// A font cannot do this. A font gives the outline of a letter, and tracing an outline draws a hollow
// shape being circled, not a letter being written - the whole effect depends on there being a centreline,
// one path down the middle of the mark, which is exactly what the hello signature is and exactly what no
// font contains. So the letters needed are authored here as centrelines, the same way Apple drew theirs.
//
// Metrics: pen starts at the baseline y = 0, x-height reaches up to -34, ascenders to -78. Up is
// negative, as in the source SVG. Each glyph is authored in its own space and shifted into place by its
// advance, so a word is a list of paths rather than one long path: the dash write-on has to be paced
// across the whole line, and a dash pattern restarts at every subpath, which would have drawn all the
// letters at once.
internal static class Script
{
    // Strokes are kept apart rather than merged into one path per letter. GDI+ restarts a dash pattern at
    // every subpath, so a letter drawn as one path with three strokes in it - m, i with its dot - had all
    // of them appearing at once, each at its own rate. That was the speed still reading as uneven after
    // the easing was taken out of the plan: the pacing was right and the drawing was not.
    internal readonly record struct Glyph(GraphicsPath[] Strokes, float[] Lengths, float Advance, float Length);

    private const float Space = 17f, Track = 9f;   // 5 was too tight - the letters ran into each other

    // Each entry is one or more strokes; a stroke is a start point followed by cubic triples, so its
    // length must be 2 + 6n numbers. Get that wrong and the tail is dropped in silence rather than
    // failing: "e" was authored one point short and lost its exit stroke, which turned the word "welcome"
    // into something that read as "welcomp". ScriptTests pins the arithmetic now.
    // Letters that need the pen lifted (the dot on an i) carry a second stroke rather than a joining line.
    private static readonly Dictionary<char, (float Advance, float[][] Strokes)> Hand = new()
    {
        ['h'] = (34f, [
            [0, -78, 1, -55, 3, -28, 4, 0],
            [4, -6, 8, -30, 17, -33, 24, -33, 31, -33, 32, -22, 32, -12, 32, -6, 32, -2, 34, 1],
        ]),
        ['a'] = (31f, [
            [26, -30, 20, -37, 7, -36, 5, -21, 3, -7, 12, -1, 19, -6, 24, -10, 26, -21, 26, -30],
            [26, -30, 26, -18, 26, -7, 29, 1],
        ]),
        ['l'] = (16f, [[3, -78, 2, -50, 0, -22, 2, -7, 3, -1, 7, 1, 12, -2]]),
        ['o'] = (30f, [[26, -19, 27, -31, 16, -37, 9, -31, 1, -24, 2, -6, 11, -2, 20, 2, 27, -8, 26, -19]]),
        ['i'] = (15f, [
            [4, -34, 2, -22, 1, -10, 3, -3, 5, 0, 9, 1, 13, -3],
            [5, -47, 6, -47, 7, -46, 6, -45],
        ]),
        ['\''] = (8f, [[3, -78, 5, -72, 4, -66, 1, -61]]),
        ['m'] = (48f, [
            [1, -33, 0, -20, 0, -9, 1, 0],
            [1, -8, 3, -28, 9, -34, 15, -34, 21, -34, 22, -24, 22, -13, 22, -6, 22, -2, 22, 0],
            [22, -9, 24, -28, 30, -34, 36, -34, 43, -34, 44, -23, 44, -12, 44, -6, 44, -2, 46, 1],
        ]),
        ['w'] = (43f, [[0, -33, 2, -14, 7, -3, 11, 1, 15, -3, 18, -19, 20, -30,
                        22, -19, 25, -3, 29, 1, 33, -3, 39, -16, 42, -33]]),
        ['e'] = (29f, [[2, -13, 10, -15, 19, -18, 27, -21, 29, -31, 22, -36, 14, -35,
                        4, -34, -1, -22, 3, -11, 7, -1, 19, 2, 27, -6]]),
        ['c'] = (28f, [[26, -26, 22, -34, 9, -36, 4, -23, -1, -10, 6, 1, 15, 1, 20, 1, 24, -3, 27, -7]]),
    };

    private static Dictionary<char, Glyph>? _built;

    private static Dictionary<char, Glyph> Built()
    {
        if (_built is not null) return _built;
        var map = new Dictionary<char, Glyph>();
        foreach (var (c, (advance, strokes)) in Hand)
        {
            var paths = new GraphicsPath[strokes.Length];
            var lens = new float[strokes.Length];
            float total = 0f;
            for (int k = 0; k < strokes.Length; k++)
            {
                var s = strokes[k];
                var path = new GraphicsPath();
                var cur = new PointF(s[0], s[1]);
                for (int i = 2; i + 5 < s.Length; i += 6)
                {
                    path.AddBezier(cur, new PointF(s[i], s[i + 1]), new PointF(s[i + 2], s[i + 3]),
                        new PointF(s[i + 4], s[i + 5]));
                    cur = new PointF(s[i + 4], s[i + 5]);
                }
                paths[k] = path;
                lens[k] = Measure(path);
                total += lens[k];
            }
            map[c] = new Glyph(paths, lens, advance, total);
        }
        _built = map;
        return map;
    }

    private static float Measure(GraphicsPath p)
    {
        using var probe = (GraphicsPath)p.Clone();
        probe.Flatten(null, 0.15f);
        var pts = probe.PathPoints;
        var kinds = probe.PathTypes;
        float len = 0f;
        for (int i = 1; i < pts.Length; i++)
        {
            if ((kinds[i] & 0x7) == 0) continue;   // a start point is a pen-up, not a stroke
            len += MathF.Sqrt(MathF.Pow(pts[i].X - pts[i - 1].X, 2) + MathF.Pow(pts[i].Y - pts[i - 1].Y, 2));
        }
        return MathF.Max(len, 0.001f);
    }

    // Exposed for the test that pins the 2 + 6n shape of every stroke, which is the one authoring mistake
    // this table can make without anything going wrong loudly.
    internal static IEnumerable<(char Char, int Stroke, int Numbers)> Strokes()
    {
        foreach (var (c, (_, strokes)) in Hand)
            for (int i = 0; i < strokes.Length; i++)
                yield return (c, i, strokes[i].Length);
    }

    internal static bool Can(string text)
    {
        var map = Built();
        foreach (char c in text)
            if (c != ' ' && !map.ContainsKey(char.ToLowerInvariant(c))) return false;
        return true;
    }

    // Width in authored units, so the caller can fit a line before drawing it.
    internal static float Width(string text)
    {
        var map = Built();
        float w = 0f;
        foreach (char c in text)
            w += c == ' ' ? Space : (map.TryGetValue(char.ToLowerInvariant(c), out var gl) ? gl.Advance : 0f) + Track;
        return MathF.Max(w, 1f);
    }

    // written 0..1 across the WHOLE line, paced by ink length rather than by letter count - otherwise the
    // pen sprints through an l and crawls through an m, which is the tell that a machine is drawing it.
    internal static void Draw(Graphics g, string text, RectangleF box, float written, float alpha,
        Color ink, float weight)
    {
        if (alpha <= 0.004f || written <= 0f || string.IsNullOrEmpty(text)) return;
        var map = Built();

        float total = 0f;
        foreach (char c in text)
            if (c != ' ' && map.TryGetValue(char.ToLowerInvariant(c), out var gl)) total += gl.Length;
        if (total <= 0f) return;

        float unitsW = Width(text);
        const float Top = -80f, Bottom = 6f;   // ascender to just under the baseline
        float scale = MathF.Min(box.Width / (unitsW + weight), box.Height / (Bottom - Top + weight));

        var save = g.Save();
        try
        {
            g.TranslateTransform(box.X + box.Width / 2f, box.Y + box.Height / 2f);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-unitsW / 2f, -(Top + Bottom) / 2f);

            using var pen = new Pen(Color.FromArgb((int)(Math.Clamp(alpha, 0f, 1f) * ink.A), ink), weight)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
                DashCap = DashCap.Round,
            };

            float want = total * written, done = 0f, x = 0f;
            foreach (char c in text)
            {
                if (c == ' ') { x += Space; continue; }
                if (!map.TryGetValue(char.ToLowerInvariant(c), out var gl)) continue;
                if (done >= want) break;

                var st = g.Save();
                g.TranslateTransform(x, 0f);
                // stroke by stroke, so the pen finishes one before starting the next - the whole point of
                // keeping them apart
                for (int k = 0; k < gl.Strokes.Length && done < want; k++)
                {
                    float len = gl.Lengths[k];
                    float here = Math.Clamp((want - done) / len, 0f, 1f);
                    if (here < 1f)
                    {
                        // same repeat trap as the signature: the gap has to outrun the rest of the stroke
                        pen.DashPattern = [MathF.Max(0.001f, len * here / weight), len * 2f / weight];
                    }
                    else pen.DashStyle = DashStyle.Solid;
                    g.DrawPath(pen, gl.Strokes[k]);
                    done += len;
                }
                g.Restore(st);

                x += gl.Advance + Track;
            }
        }
        finally { g.Restore(save); }
    }
}
