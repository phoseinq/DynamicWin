using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using Halo.ClaudeCode;

namespace Halo.Widgets;

internal sealed class ClaudeCodeWidget : IWidget
{
    private static readonly Color Blue = Color.FromArgb(91, 157, 255);
    private static readonly Color Green = Color.FromArgb(62, 207, 92);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Red = Color.FromArgb(229, 72, 77);
    private static readonly Color Track = Color.FromArgb(38, 255, 255, 255);
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);

    private readonly StatusStore _store;
    private readonly Action _cancel;

    public ClaudeCodeWidget(StatusStore store, Action cancel)
    {
        _store = store;
        _cancel = cancel;
    }

    private bool CanCancel => _store.Current?.State == "working" && _store.Current.Pid > 0;

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        var st = _store.Current;
        float col = 1f - fade;
        if (col > 0.01f) DrawCollapsed(g, w, h, col, st);
        if (fade > 0.01f) DrawExpanded(g, w, h, fade, st);
    }

    private static void DrawCollapsed(Graphics g, int w, int h, float a, CcStatus? st)
    {
        var dot = StateColor(st?.State);
        float cy = h / 2f;
        using (var db = new SolidBrush(Mul(dot, a)))
            g.FillEllipse(db, 16, cy - 5, 10, 10);

        float bx = 34, bw = w - bx - 16, by = cy - 2, bh = 4;
        Fill(g, bx, by, bw, bh, Mul(Track, a));
        double frac = ContextFrac(st);
        if (frac > 0)
            Fill(g, bx, by, (float)(bw * frac), bh, Mul(Blue, a));
    }

    private void DrawExpanded(Graphics g, int w, int h, float a, CcStatus? st)
    {
        int pad = 26;
        using var title = new Font("Segoe UI Semibold", 21f, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 12.5f, GraphicsUnit.Pixel);

        var dot = StateColor(st?.State);
        using (var db = new SolidBrush(Mul(dot, a)))
            g.FillEllipse(db, pad, pad + 5, 11, 11);
        using (var tb = new SolidBrush(Mul(White, a)))
            g.DrawString("Claude Code", title, tb, pad + 20, pad - 2);
        using (var ab = new SolidBrush(Mul(Dim, a)))
            g.DrawString(Activity(st), small, ab, pad + 20, pad + 24);

        if (st?.Session == null && st?.Usage == null)
        {
            using var nb = new SolidBrush(Mul(Dim, a));
            g.DrawString("No active Claude Code session", body, nb, pad, pad + 64);
            return;
        }

        float y = pad + 52;
        int barW = w - pad * 2;
        double ctx = ContextFrac(st);
        DrawBar(g, pad, y, barW, "Session context", $"{ctx * 100:0}%", ctx, Blue, a, body, small);
        if (st?.Usage != null)
        {
            y += 42;
            double five = st.Usage.FiveHourPct;
            DrawBar(g, pad, y, barW, "5-hour limit", ResetLabel(five, st.Usage.FiveHourResetsAt), five, LimitColor(five), a, body, small);
            y += 42;
            double week = st.Usage.WeeklyPct;
            DrawBar(g, pad, y, barW, "Weekly limit", $"{week * 100:0}%", week, LimitColor(week), a, body, small);
        }

        DrawCancel(g, w, h, a, body);
    }

    private void DrawCancel(Graphics g, int w, int h, float a, Font font)
    {
        var r = ExpandedButton(w, h);
        bool on = CanCancel;
        var bg = on ? Red : Color.FromArgb(120, 255, 255, 255);
        float ba = on ? a : a * 0.4f;
        using (var path = Rounded(r, 9))
        using (var b = new SolidBrush(Mul(Color.FromArgb(46, bg), a)))
        using (var pen = new Pen(Mul(bg, ba), 1.3f))
        {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        var text = "Cancel";
        var sz = g.MeasureString(text, font);
        using var tb = new SolidBrush(Mul(on ? White : Dim, a));
        g.DrawString(text, font, tb, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
    }

    public RectangleF ExpandedButton(int w, int h)
    {
        float bw = 104, bh = 36, margin = 22;
        return new RectangleF(w - margin - bw, 19, bw, bh);
    }

    public void ActivateButton()
    {
        if (CanCancel) _cancel();
    }

    private static void DrawBar(Graphics g, float x, float y, float w, string label, string value,
        double frac, Color fill, float a, Font labelFont, Font valueFont)
    {
        using (var lb = new SolidBrush(Mul(White, a)))
            g.DrawString(label, labelFont, lb, x, y);
        var sz = g.MeasureString(value, valueFont);
        using (var vb = new SolidBrush(Mul(Dim, a)))
            g.DrawString(value, valueFont, vb, x + w - sz.Width, y + 1);

        float by = y + 24, bh = 6;
        Fill(g, x, by, w, bh, Mul(Track, a));
        double f = Math.Clamp(frac, 0, 1);
        if (f > 0)
            Fill(g, x, by, (float)(w * f), bh, Mul(fill, a));
    }

    private static void Fill(Graphics g, float x, float y, float w, float h, Color c)
    {
        if (w <= 0) return;
        using var path = Rounded(new RectangleF(x, y, w, h), h / 2f);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

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

    private static Color Mul(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);

    private static double ContextFrac(CcStatus? st)
    {
        var s = st?.Session;
        if (s == null || s.ContextMax <= 0) return 0;
        return Math.Clamp((double)s.ContextUsed / s.ContextMax, 0, 1);
    }

    private static Color StateColor(string? state) => state switch
    {
        "working" => Green,
        "waiting_input" => Amber,
        _ => Color.FromArgb(140, 255, 255, 255),
    };

    private static Color LimitColor(double f) => f < 0.6 ? Green : f < 0.85 ? Amber : Red;

    private static string Activity(CcStatus? st) => st?.State switch
    {
        "working" => ToolVerb(st.CurrentTool),
        "waiting_input" => "Waiting for your input…",
        "idle" => "Idle",
        _ => "Not connected",
    };

    private static string ToolVerb(string? tool) => tool switch
    {
        "Edit" or "Write" or "MultiEdit" => "Editing files…",
        "Read" => "Reading…",
        "Bash" or "PowerShell" => "Running a command…",
        "Grep" or "Glob" => "Searching…",
        "WebFetch" or "WebSearch" => "Browsing the web…",
        null or "" => "Working…",
        _ => tool + "…",
    };

    private static string ResetLabel(double pct, string? resetsAt)
    {
        var s = $"{pct * 100:0}%";
        if (DateTimeOffset.TryParse(resetsAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
        {
            var d = t - DateTimeOffset.UtcNow;
            if (d.TotalMinutes >= 1)
                s += d.TotalHours >= 1 ? $" · resets in {(int)d.TotalHours}h {d.Minutes}m" : $" · resets in {d.Minutes}m";
        }
        return s;
    }
}
