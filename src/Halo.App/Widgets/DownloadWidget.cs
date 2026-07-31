using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Halo.Widgets;

internal sealed class DownloadWidget : IWidget
{
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Track = Color.FromArgb(46, 255, 255, 255);
    private static readonly Color Blue = Color.FromArgb(120, 170, 255);
    private static readonly FontFamily Fluent = new("Segoe Fluent Icons");

    public DownloadWidget() => Downloads.Poke();

    public string Icon => "";
    private static string? _icoFile;
    private static Bitmap? _icoCache;

    private static Bitmap? Ico()
    {
        if (Downloads.IconFile is { } f)
        {
            if (f != _icoFile) { _icoCache?.Dispose(); _icoCache = LoadFile(f); _icoFile = f; }
            if (_icoCache != null) return _icoCache;
        }
        return Downloads.IsStore && Downloads.ExePath is { } aumid
            ? Halo.Notifications.ShellIcon.ForAumid(aumid)
            : AppIcon.ForAumid(Downloads.ExePath);
    }
    private static Bitmap? LoadFile(string f)
    {
        try { using var t = new Bitmap(f); return new Bitmap(t); } catch { return null; }
    }
    public Bitmap? IconImage => Ico();
    public bool IsActive => Downloads.Name != null;

    public float RingProgress => Downloads.Name == null || Downloads.Installing || Downloads.Waiting || Downloads.NoPct
        ? -1f : Math.Clamp(Downloads.Percent / 100f, 0f, 1f);

    private static bool Spinning => Downloads.Installing || Downloads.Waiting || (Downloads.NoPct && !Downloads.Paused);
    public int Version => Downloads.Version + (Spinning ? (int)(Environment.TickCount64 / 60) : 0);
    public bool Animating => Spinning;
    public Color? Ring => Downloads.Name == null ? null : Accent();

    private static Color Accent()
    {
        var a = Fx.AccentOf(Ico());
        return a == Fx.White ? Blue : a;
    }

    private const float ArtX = 26, ArtY = 26, ArtSize = 132;
    private static RectangleF[] CtlRects(int n)
    {
        const float size = 40, gap = 14, y = 158;
        float x0 = ArtX + ArtSize + 24;
        var r = new RectangleF[n];
        for (int i = 0; i < n; i++) r[i] = new RectangleF(x0 + i * (size + gap), y, size, size);
        return r;
    }

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> CollapsedButtons(int w, int h)
        => Array.Empty<(RectangleF, Action<PointF>)>();

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
    {
        var hits = new List<(RectangleF, Action<PointF>)>();
        int n = Downloads.Count;
        if (Downloads.HasMore) hits.Add((MenuRect(w), _ => _menuOpen = !_menuOpen));
        if (_menuOpen && Downloads.HasMore)
        {
            int top = MenuTop(n), rows = Math.Min(n - top, MaxRows);
            for (int v = 0; v < rows; v++)
            {
                int idx = top + v;
                hits.Add((RowRect(w, n, v), _ => { Downloads.Select(idx); _menuOpen = false; }));
            }

            hits.Add((new RectangleF(0, 0, w, h), _ => _menuOpen = false));
            return hits;
        }
        foreach (var c in Chips()) { var act = c.Click; hits.Add((c.Rect, _ => act())); }
        return hits;
    }

    private readonly record struct Chip(RectangleF Rect, int Glyph, bool Danger, bool Stop, Action Click);

    private static Chip[] Chips()
    {
        var row = Row(Downloads.Name != null, Downloads.IsStore, Downloads.CanControl,
                      Downloads.Hwnd != IntPtr.Zero, Downloads.FilePath is { Length: > 0 });
        var rects = CtlRects(row.Length);
        var chips = new Chip[row.Length];
        for (int i = 0; i < row.Length; i++) chips[i] = Make(rects[i], row[i]);
        return chips;
    }

    internal enum DlCtl { PauseResume, StoreCancel, Reveal, Stop, ShowInFolder, RevealOwner, Cancel }

    internal static DlCtl[] Row(bool named, bool store, bool canControl, bool hasWindow, bool hasPath)
    {
        if (!named) return Array.Empty<DlCtl>();
        if (store && canControl) return new[] { DlCtl.PauseResume, DlCtl.StoreCancel };
        if (hasWindow) return new[] { DlCtl.Reveal, DlCtl.Stop };
        if (hasPath) return new[] { DlCtl.ShowInFolder, DlCtl.RevealOwner, DlCtl.Cancel };
        return Array.Empty<DlCtl>();
    }

    private static Chip Make(RectangleF r, DlCtl c) => c switch
    {
        DlCtl.PauseResume => new Chip(r, Downloads.Paused ? 0xE768 : 0xE769, false, false,
                                      () => { if (Downloads.Paused) Downloads.StoreResume(); else Downloads.StorePause(); }),
        DlCtl.StoreCancel => new Chip(r, 0xE711, true, false, Downloads.StoreCancel),
        DlCtl.Reveal => new Chip(r, 0xE838, false, false, Downloads.Reveal),
        DlCtl.Stop => new Chip(r, 0, false, true, Downloads.StopProcess),
        DlCtl.ShowInFolder => new Chip(r, 0xE838, false, false, Downloads.ShowInFolder),
        DlCtl.RevealOwner => new Chip(r, 0xE7C4, false, false, Downloads.RevealOwner),

        _ => new Chip(r, 0xE711, true, false, Downloads.CancelDownload),
    };

    private static float _fracShown = -1f;
    private static string? _lastName;

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        if (fade <= 0.01f) return;
        string? name = Downloads.Name;
        if (name == null) return;
        bool indeterminate = Downloads.Installing || Downloads.Waiting || (Downloads.NoPct && !Downloads.Paused);
        bool paused = Downloads.Paused;
        int pct = Math.Clamp(Downloads.Percent, 0, 100);
        long done = Downloads.Downloaded, tot = Downloads.Total;
        var icon = Ico();
        var accent = icon != null ? Accent() : Blue;
        float pulse = 0.5f + 0.5f * MathF.Sin(Environment.TickCount64 / 480f);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var oldHint = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        Fx.Glow(g, w, h, fade * (indeterminate ? 0.55f + 0.45f * pulse : 1f),
            ArtX + ArtSize / 2f, h / 2f, w * 0.85f, h * 1.2f, 34, accent);
        DrawArt(g, icon, fade);

        float tx = ArtX + ArtSize + 24, tw = w - tx - MenuSlot - 26;
        using var titleF = new Font("Segoe UI Semibold", 23f, GraphicsUnit.Pixel);
        using var metaF = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var smallF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);

        float y = ArtY + 4;
        using (var tb = new SolidBrush(Mul(White, fade)))
            DrawEllipsized(g, name, titleF, tb, tx, y, tw, 30);
        y += 32;

        string state = Downloads.Waiting ? "Waiting…" : Downloads.Installing ? "Installing…"
            : paused ? "Paused" : "Downloading";
        string meta = state;

        if (Downloads.NoBytes) { }
        else if (done > 1_048_576 && tot > 1_048_576) meta += $"   ·   {Bytes(done)} / {Bytes(tot)}   ·   {pct}%";
        else if (done > 1_048_576) meta += $"   ·   {Bytes(done)}";
        using (var mb = new SolidBrush(Mul(Dim, fade)))
            DrawEllipsized(g, meta, metaF, mb, tx, y, tw, 20);
        y += 30;

        float bh = 6;
        Fill(g, tx, y, tw, bh, Mul(Track, fade), bh / 2);
        if (indeterminate)
        {
            float seg = tw * 0.34f, sx = tx + (tw - seg) * (0.5f + 0.5f * MathF.Sin(Environment.TickCount64 / 700f));
            Fill(g, sx, y, seg, bh, Mul(accent, fade * (0.5f + 0.5f * pulse)), bh / 2);
        }
        else
        {
            float frac = tot > 1_048_576 ? Math.Clamp(done / (float)tot, 0f, 1f) : pct / 100f;
            if (name != _lastName) { _lastName = name; _fracShown = frac; }
            _fracShown = _fracShown < 0 ? frac : _fracShown + (frac - _fracShown) * 0.18f;
            if (Math.Abs(frac - _fracShown) < 0.002f) _fracShown = frac;
            if (_fracShown > 0) Fill(g, tx, y, tw * _fracShown, bh, Mul(paused ? Dim : accent, fade), bh / 2);
        }
        y += bh + 10;

        if (Downloads.FilePath is { Length: > 0 } fp)
        {
            string? dir = null;
            try { dir = System.IO.Path.GetDirectoryName(fp); } catch { }
            if (!string.IsNullOrEmpty(dir))
                using (var pb = new SolidBrush(Mul(Color.FromArgb(115, 255, 255, 255), fade)))
                using (var psf = new StringFormat(StringFormat.GenericTypographic)
                { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(dir, smallF, pb, new RectangleF(tx, y, tw, 18), psf);
        }

        DrawControls(g, fade);
        DrawMenuSlot(g, w, fade);
        DrawMenuList(g, w, h, fade);
        g.TextRenderingHint = oldHint;
    }

    private const float MenuSlot = 44;

    internal static RectangleF MenuRect(int w) => new(w - MenuSlot - 8, 22, 34, 34);

    private static bool _menuOpen;
    private const float MenuW = 252f, RowH = 32f, MenuPad = 7f, MenuR = 15f;
    private const int MaxRows = 4;

    internal static RectangleF MenuListRect(int w, int n)
        => new(w - MenuW - 8, MenuRect(w).Bottom + 8, MenuW, Math.Min(n, MaxRows) * RowH + MenuPad * 2);

    private static int MenuTop(int n) => MenuTop(n, Downloads.SelectedIndex, MaxRows);

    internal static int MenuTop(int n, int selected, int maxRows)
        => n <= maxRows ? 0 : Math.Clamp(selected - maxRows + 1, 0, n - maxRows);

    private static RectangleF RowRect(int w, int n, int visible)
    {
        var l = MenuListRect(w, n);
        return new RectangleF(l.X + MenuPad, l.Y + MenuPad + visible * RowH, l.Width - MenuPad * 2, RowH);
    }

    private void DrawMenuList(Graphics g, int w, int h, float fade)
    {
        if (!_menuOpen || !Downloads.HasMore) return;
        var items = Downloads.Items;
        int n = items.Count;
        if (n == 0) return;

        using (var scrim = new SolidBrush(Mul(Color.FromArgb(120, 0, 0, 0), fade)))
            g.FillRectangle(scrim, 0, 0, w, h);
        var l = MenuListRect(w, n);

        for (int i = 6; i >= 1; i--)
        {
            var s = RectangleF.Inflate(l, i, i);
            s.Y += 2f;
            using var sp = Fx.Rounded(s, MenuR + i);
            using var pen = new Pen(Mul(Color.FromArgb(11, 0, 0, 0), fade), 2f);
            g.DrawPath(pen, sp);
        }

        using (var bg = new SolidBrush(Mul(Color.FromArgb(232, 22, 22, 26), fade)))
        using (var p = Fx.Rounded(l, MenuR))
            g.FillPath(bg, p);

        using (var pen = new Pen(Mul(Color.FromArgb(52, 255, 255, 255), fade), 1f))
        using (var p = Fx.Rounded(RectangleF.Inflate(l, -0.5f, -0.5f), MenuR - 0.5f))
            g.DrawPath(pen, p);
        using (var pen = new Pen(Mul(Color.FromArgb(30, 0, 0, 0), fade), 1f))
        using (var p = Fx.Rounded(RectangleF.Inflate(l, 0.5f, 0.5f), MenuR + 0.5f))
            g.DrawPath(pen, p);

        using var f = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        using var bold = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
        var accent = Accent();
        int top = MenuTop(n), sel = Downloads.SelectedIndex, rows = Math.Min(n - top, MaxRows);
        for (int v = 0; v < rows; v++)
        {
            int idx = top + v;
            var r = RowRect(w, n, v);
            bool cur = idx == sel, hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
            if (cur || hov)
                using (var hb = new SolidBrush(Mul(Color.FromArgb(cur ? 34 : 18, 255, 255, 255), fade)))
                using (var p = Fx.Rounded(r, 10f))
                    g.FillPath(hb, p);

            if (cur)
                using (var ab = new SolidBrush(Mul(accent, fade)))
                using (var p = Fx.Rounded(new RectangleF(r.X + 4, r.Y + RowH * 0.26f, 3f, RowH * 0.48f), 1.5f))
                    g.FillPath(ab, p);

            var it = items[idx];

            string tail = it.NoPct ? Bytes(it.Downloaded) : $"{it.Percent}%";
            var tsz = g.MeasureString(tail, f);
            using (var tb = new SolidBrush(Mul(cur ? Dim : Color.FromArgb(112, 255, 255, 255), fade)))
                g.DrawString(tail, f, tb, r.Right - tsz.Width - 10, r.Y + (RowH - tsz.Height) / 2f);
            using (var nb = new SolidBrush(Mul(cur ? White : Dim, fade)))
                DrawEllipsized(g, it.Name, cur ? bold : f, nb, r.X + 14, r.Y + (RowH - 17) / 2f,
                               r.Width - tsz.Width - 32, 17);
        }
    }

    private static void DrawMenuSlot(Graphics g, int w, float fade)
    {
        if (!Downloads.HasMore) { _menuOpen = false; return; }
        var r = MenuRect(w);
        bool hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
        bool lit = hov || _menuOpen;
        using (var bg = new SolidBrush(Mul(Color.FromArgb(lit ? 42 : 24, 255, 255, 255), fade)))
        using (var p = Fx.Rounded(r, 10f))
            g.FillPath(bg, p);
        using var b = new SolidBrush(Mul(lit ? White : Dim, fade));
        float bw = r.Width * 0.44f, x = r.X + (r.Width - bw) / 2f;
        for (int i = 0; i < 3; i++) g.FillRectangle(b, x, r.Y + 11 + i * 6, bw, 2f);
    }

    private void DrawControls(Graphics g, float fade)
    {
        foreach (var c in Chips())
        {
            if (c.Stop) DrawStop(g, c.Rect, fade);
            else DrawCtl(g, c.Rect, c.Glyph, fade, c.Danger);
        }
    }

    private static void DrawArt(Graphics g, Bitmap? icon, float fade)
        => IconTile(g, new RectangleF(ArtX, ArtY, ArtSize, ArtSize), ArtSize * 0.24f, icon, fade, 46f,
            border: icon == null);

    private static void IconTile(Graphics g, RectangleF box, float radius, Bitmap? icon, float fade, float glyphPx, bool border)
    {
        using var path = Fx.Rounded(box, radius);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (icon != null)
        {
            int s = Math.Max(1, (int)Math.Ceiling(box.Width));
            using var scaled = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(scaled))
            {
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.SmoothingMode = SmoothingMode.HighQuality;
                using var ia = new ImageAttributes();
                ia.SetWrapMode(WrapMode.TileFlipXY);
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
                int side = Math.Min(icon.Width, icon.Height);
                sg.DrawImage(icon, new Rectangle(0, 0, s, s),
                    (icon.Width - side) / 2, (icon.Height - side) / 2, side, side, GraphicsUnit.Pixel, ia);
            }
            using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
            tb.TranslateTransform(box.X, box.Y);
            g.FillPath(tb, path);
        }
        else
        {
            using var gb = new SolidBrush(Mul(Track, fade));
            g.FillPath(gb, path);
            DrawGlyph(g, box, ((char)0xE896).ToString(), glyphPx, fade);
        }
        if (border)
        {
            using var pen = new Pen(Mul(Color.FromArgb(28, 255, 255, 255), fade), 1f);
            g.DrawPath(pen, path);
        }
    }

    private static void DrawCollapsedIcon(Graphics g, Bitmap? icon, float x, float y, float sz, float fade)
        => IconTile(g, new RectangleF(x, y, sz, sz), sz * 0.28f, icon, fade, sz * 0.5f, border: false);

    private static string Bytes(long b)
    {
        if (b <= 0) return "0 MB";
        double mb = b / 1048576.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private static readonly Color Ctl = Color.FromArgb(255, 255, 255, 255);

    private void DrawCtl(Graphics g, RectangleF r, int glyph, float fade, bool danger)
    {
        bool hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
        var tint = danger ? Red : Ctl;
        using (var bg = new SolidBrush(Mul(Color.FromArgb(hov ? 58 : 34, tint), fade)))
            g.FillEllipse(bg, r);
        using (var pen = new Pen(Mul(Color.FromArgb(hov ? 70 : 40, tint), fade), 1f))
            g.DrawEllipse(pen, r);
        DrawGlyph(g, r, ((char)glyph).ToString(), r.Width * 0.40f, fade * (hov ? 1f : 0.85f), danger ? tint : White);
    }

    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        _menuOpen = false;
        string? name = Downloads.Name;
        if (name == null) return;
        var icon = Ico();
        var accent = icon != null ? Accent() : Blue;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float sz = h - 14f, ix = 9, iy = (h - sz) / 2f;
        float tx = ix + sz + 12;

        bool breathe = Downloads.Waiting || (Downloads.NoPct && !Downloads.Paused && !Downloads.Installing);
        if (breathe)
        {
            float pulse = 0.5f - 0.5f * MathF.Cos(Environment.TickCount % 2400 / 2400f * MathF.Tau);
            using (var pb = new SolidBrush(Mul(accent, fade * (0.05f + 0.12f * pulse))))
            using (var pp = Fx.PillPath(w, h, h / 2f))
                g.FillPath(pb, pp);
            DrawCollapsedIcon(g, icon, ix, iy, sz, fade);
            DrawCountBadge(g, ix, iy, sz, fade, Downloads.Count);
            using var nf = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
            using var nb = new SolidBrush(Mul(White, fade));
            float right = w - tx - 14;

            if (!Downloads.Waiting && !Downloads.NoBytes && Downloads.Downloaded > 0)
            {
                string got = Bytes(Downloads.Downloaded);
                using var sf2 = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
                using var sb2 = new SolidBrush(Mul(Dim, fade));
                var gsz = g.MeasureString(got, sf2);
                g.DrawString(got, sf2, sb2, w - gsz.Width - 14, (h - gsz.Height) / 2f);
                right -= gsz.Width + 8;
            }
            DrawEllipsized(g, name, nf, nb, tx, (h - 18f) / 2f, right, 18);
            return;
        }

        DrawCollapsedIcon(g, icon, ix, iy, sz, fade);
        float by = h / 2f - 3, bh = 6;
        if (Downloads.Installing)
        {
            float p = 0.5f + 0.5f * MathF.Sin(Environment.TickCount64 / 480f);
            float bw = w - tx - 16;
            Fill(g, tx, by, bw, bh, Track, bh / 2);
            float seg = bw * 0.38f, sx = tx + (bw - seg) * (0.5f + 0.5f * MathF.Sin(Environment.TickCount64 / 700f));
            Fill(g, sx, by, seg, bh, Mul(accent, 0.5f + 0.5f * p), bh / 2);
            return;
        }
        DrawPillProgress(g, w, h, fade, Math.Clamp(Downloads.Percent, 0, 100), accent,
            Downloads.Paused, ix + sz);
    }

    private static void DrawPillProgress(Graphics g, int w, int h, float fade, int pct, Color accent,
        bool paused, float iconRight)
    {
        var bar = paused ? Dim : accent;

        Fx.PillBar(g, w, h, fade, pct / 100f, bar, 1f, alive: !paused);

        float sz = h - 14f;
        DrawCollapsedIcon(g, Ico(), 9, (h - sz) / 2f, sz, fade);
        if (paused) DrawPausedBadge(g, 9, (h - sz) / 2f, sz, fade);
        DrawCountBadge(g, 9, (h - sz) / 2f, sz, fade, Downloads.Count);

        long done = Downloads.Downloaded, tot = Downloads.Total;

        var oldHint = g.TextRenderingHint;

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var f = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

        float left = iconRight + 8f, right = w - 12f;

        string text = $"{pct}%";
        foreach (var candidate in new[]
        {
            done > 1_048_576 && tot > 1_048_576 ? $"{Bytes(done)} / {Bytes(tot)}  ·  {pct}%" : null,
            done > 1_048_576 ? $"{Bytes(done)}  ·  {pct}%" : null,
        })
        {
            if (candidate == null) continue;
            if (g.MeasureString(candidate, f, int.MaxValue, sf).Width <= right - left) { text = candidate; break; }
        }
        var zone = new RectangleF(left, -Fx.CenterLift(f), right - left, h);
        using (var shadow = new SolidBrush(Mul(Color.FromArgb(110, 0, 0, 0), fade)))
            g.DrawString(text, f, shadow, new RectangleF(zone.X + 0.6f, zone.Y + 0.6f, zone.Width, zone.Height), sf);
        using (var nb = new SolidBrush(Mul(White, fade)))
            g.DrawString(text, f, nb, zone, sf);
        g.TextRenderingHint = oldHint;

    }

    private static void DrawCountBadge(Graphics g, float x, float y, float sz, float fade, int n)
    {
        if (n < 2) return;
        float d = sz * 0.60f, bx = x + sz - d + 1f, by = y - 1f;
        using (var shade = new SolidBrush(Mul(Color.FromArgb(215, 12, 12, 14), fade)))
            g.FillEllipse(shade, bx, by, d, d);
        using (var ring = new Pen(Mul(Color.FromArgb(190, 255, 255, 255), fade), 1.1f))
            g.DrawEllipse(ring, bx, by, d, d);
        using var f = new Font("Segoe UI Semibold", d * 0.62f, GraphicsUnit.Pixel);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var b = new SolidBrush(Mul(White, fade));
        g.DrawString(n > 9 ? "9+" : n.ToString(), f, b,
                     new RectangleF(bx, by - Fx.CenterLift(f), d, d), sf);
    }

    private static void DrawPausedBadge(Graphics g, float x, float y, float sz, float fade)
    {
        float d = sz * 0.62f, bx = x + sz - d + 2f, by = y + sz - d + 2f;
        using (var shade = new SolidBrush(Mul(Color.FromArgb(190, 12, 12, 14), fade)))
            g.FillEllipse(shade, bx, by, d, d);
        using (var ring = new Pen(Mul(Color.FromArgb(210, 255, 255, 255), fade), 1.2f))
            g.DrawEllipse(ring, bx, by, d, d);

        float bw = d * 0.16f, bh = d * 0.42f, gap = d * 0.14f;
        float cx = bx + d / 2f, cy = by + d / 2f;
        using var b = new SolidBrush(Mul(White, fade));
        g.FillRectangle(b, cx - gap / 2f - bw, cy - bh / 2f, bw, bh);
        g.FillRectangle(b, cx + gap / 2f, cy - bh / 2f, bw, bh);
    }

    private static readonly Color Red = Color.FromArgb(255, 120, 110);
    private static void DrawStop(Graphics g, RectangleF r, float fade)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
        using (var bg = new SolidBrush(Mul(Color.FromArgb(hov ? 60 : 38, 255, 120, 110), fade)))
            g.FillEllipse(bg, r);
        float s = r.Width * 0.34f;
        var sq = new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s);
        using var b = new SolidBrush(Mul(Red, fade * (hov ? 1f : 0.85f)));
        using var p = Fx.Rounded(sq, s * 0.22f);
        g.FillPath(b, p);
    }

    private static Color Mul(Color c, float a) => Color.FromArgb((int)(c.A * a), c.R, c.G, c.B);

    private static void Fill(Graphics g, float x, float y, float w, float h, Color c, float r = 0)
    {
        if (w <= 0.5f) return;
        using var b = new SolidBrush(c);
        if (r <= 0) { g.FillRectangle(b, x, y, w, h); return; }
        using var p = Fx.Rounded(new RectangleF(x, y, w, h), r);
        g.FillPath(b, p);
    }

    private static void DrawEllipsized(Graphics g, string s, Font f, Brush b, float x, float y, float w, float h)
    {
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, new RectangleF(x, y, w, h), sf);
    }

    private static void DrawGlyph(Graphics g, RectangleF r, string glyph, float px, float fade, Color? tint = null)
    {
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(glyph, Fluent, (int)FontStyle.Regular, px, PointF.Empty, sf);
        path.Flatten();
        var bnd = path.GetBounds();
        if (bnd.Width <= 0 || bnd.Height <= 0) return;
        using var m = new Matrix();
        m.Translate(MathF.Round(r.X + (r.Width - bnd.Width) / 2f - bnd.X),
                    MathF.Round(r.Y + (r.Height - bnd.Height) / 2f - bnd.Y));
        path.Transform(m);
        using var br = new SolidBrush(Mul(tint ?? White, fade * 0.9f));
        g.FillPath(br, path);
    }
}
