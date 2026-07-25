using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Halo.Widgets;

// Download progress in the pill. Data comes from the Downloads scanner. What the panel can actually do
// depends on the source: Store items pause/cancel through AppInstallManager, a partial file is cancelled
// by deleting it (the downloader gives up when its file disappears — verified live against Chrome), and a
// window-scanned manager can only be brought forward or quit, since no cross-app per-download API exists.
internal sealed class DownloadWidget : IWidget
{
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Track = Color.FromArgb(46, 255, 255, 255);
    private static readonly Color Blue = Color.FromArgb(120, 170, 255); // fallback download accent
    private static readonly FontFamily Fluent = new("Segoe Fluent Icons");

    public DownloadWidget() => Downloads.Poke();

    public string Icon => ""; // Segoe MDL2 Download
    private static string? _icoFile;
    private static Bitmap? _icoCache;
    // GDK games stage their own logo → load it directly (real Roblox icon, not the generic Store bag).
    // Store items carry an AUMID → resolve the packaged icon via ShellIcon.
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
        try { using var t = new Bitmap(f); return new Bitmap(t); } catch { return null; } // copy so the file isn't locked
    }
    public Bitmap? IconImage => Ico();
    public bool IsActive => Downloads.Name != null;
    // closed circle fills with the download %; -1 (full ring) while %-less (spinner phases: installing,
    // queued, or a game whose catalog total hasn't arrived yet)
    public float RingProgress => Downloads.Name == null || Downloads.Installing || Downloads.Waiting || Downloads.NoPct
        ? -1f : Math.Clamp(Downloads.Percent / 100f, 0f, 1f);
    // while installing (indeterminate), keep bumping so the breathing glow re-renders
    // spinner phases (installing / queued / actively-downloading game) must keep repainting or the ring
    // arc freezes — bump Version every frame and flag Animating for all three.
    private static bool Spinning => Downloads.Installing || Downloads.Waiting || (Downloads.NoPct && !Downloads.Paused);
    public int Version => Downloads.Version + (Spinning ? (int)(Environment.TickCount64 / 60) : 0);
    public bool Animating => Spinning;
    public Color? Ring => Downloads.Name == null ? null : Accent();

    private static Color Accent()
    {
        var a = Fx.AccentOf(Ico());
        return a == Fx.White ? Blue : a;
    }

    // Art tile and the y=158 control baseline are shared with the media panel, so the two widgets read as
    // the same surface. The row itself is left-aligned rather than centred the way media's transport is:
    // media has a symmetric prev/play/next cluster, this is a toolbar hanging off a left-aligned column,
    // and centring it left the chips floating with nothing above them to line up against.
    private const float ArtX = 26, ArtY = 26, ArtSize = 132;
    private static RectangleF[] CtlRects(int n)
    {
        const float size = 40, gap = 14, y = 158;
        float x0 = ArtX + ArtSize + 24;   // same left edge as the title
        var r = new RectangleF[n];
        for (int i = 0; i < n; i++) r[i] = new RectangleF(x0 + i * (size + gap), y, size, size);
        return r;
    }

    // Deliberately none. A Stop lived here briefly and was removed: the collapsed pill is a glance
    // surface the user reaches toward, so a control on it fires by accident. Stop, cancel and the file
    // path all live in the expanded panel, where there is room to label them.
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
            // an open menu owns the panel: the chips underneath must not be reachable through it, and a
            // click anywhere else is how you dismiss it (NotchController stops at the first rect that hits,
            // so this catch-all has to come last)
            hits.Add((new RectangleF(0, 0, w, h), _ => _menuOpen = false));
            return hits;
        }
        foreach (var c in Chips()) { var act = c.Click; hits.Add((c.Rect, _ => act())); }
        return hits;
    }

    // One row description, read by both the painter and the hit-tester. They used to lay the row out
    // separately and drifted: DrawControls grew a third chip for browser downloads while Buttons still
    // built two, so Cancel had no hit rect at all and the other two sat 29px off-centre from the circles
    // the user was aiming at. Adding a chip now moves both at once.
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

    // Which controls the panel offers, given where the download came from. Pure and internal so the row
    // itself can be asserted: browser downloads are the case that broke, because the painter grew a third
    // chip here while the hit-tester still believed there were two.
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
        DlCtl.Reveal => new Chip(r, 0xE838, false, false, Downloads.Reveal),      // bring the manager forward
        DlCtl.Stop => new Chip(r, 0, false, true, Downloads.StopProcess),         // quitting it is the only stop
        DlCtl.ShowInFolder => new Chip(r, 0xE838, false, false, Downloads.ShowInFolder),
        DlCtl.RevealOwner => new Chip(r, 0xE7C4, false, false, Downloads.RevealOwner),
        // ✕, not the filled Stop square: for a browser this opens its downloads list, where the cancel the
        // browser will honour is one click away. Drawing it as a Stop would promise the bytes had already
        // stopped, which is exactly the lie the delete-the-partial version told.
        _ => new Chip(r, 0xE711, true, false, Downloads.CancelDownload),
    };

    private static float _fracShown = -1f;
    private static string? _lastName;

    // media-style panel: rounded logo tile + name + status line + horizontal progress bar with byte
    // end-labels + glass control chips. Indeterminate phases show a sliding segment; the fill eases.
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

        // One column to the right of the art, everything left-aligned to the same edge and stacked on a
        // regular rhythm. The old layout hung the title at a fixed y, the bar at another, and the chips at
        // a third, so nothing lined up; here each block is placed from the one above it.
        float tx = ArtX + ArtSize + 24, tw = w - tx - MenuSlot - 26;
        using var titleF = new Font("Segoe UI Semibold", 23f, GraphicsUnit.Pixel);
        using var metaF = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var smallF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);

        float y = ArtY + 4;
        using (var tb = new SolidBrush(Mul(White, fade)))
            DrawEllipsized(g, name, titleF, tb, tx, y, tw, 30);
        y += 32;

        // status and the numbers on one line, in the same "a · b · c" voice the rest of the app uses
        string state = Downloads.Waiting ? "Waiting…" : Downloads.Installing ? "Installing…"
            : paused ? "Paused" : "Downloading";
        string meta = state;
        if (done > 1_048_576 && tot > 1_048_576) meta += $"   ·   {Bytes(done)} / {Bytes(tot)}   ·   {pct}%";
        else if (done > 1_048_576) meta += $"   ·   {Bytes(done)}";
        using (var mb = new SolidBrush(Mul(Dim, fade)))
            DrawEllipsized(g, meta, metaF, mb, tx, y, tw, 20);
        y += 30;

        // the bar owns the full column width now, instead of being inset to match nothing in particular
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
            if (name != _lastName) { _lastName = name; _fracShown = frac; } // new download → snap
            _fracShown = _fracShown < 0 ? frac : _fracShown + (frac - _fracShown) * 0.18f;
            if (Math.Abs(frac - _fracShown) < 0.002f) _fracShown = frac;
            if (_fracShown > 0) Fill(g, tx, y, tw * _fracShown, bh, Mul(paused ? Dim : accent, fade), bh / 2);
        }
        y += bh + 10;

        // where it is landing — folder only, since the filename is already the title
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
        DrawMenuList(g, w, h, fade);   // last: it floats over the column
        g.TextRenderingHint = oldHint;
    }

    // Reserved gutter on the right for the download switcher. It is empty while a single download is in
    // flight, but the column is sized around it from the start so the layout will not jump the day a
    // second download makes the button appear.
    private const float MenuSlot = 44;

    internal static RectangleF MenuRect(int w) => new(w - MenuSlot - 8, 22, 34, 34);

    // The switcher list. Four rows is what fits between the hamburger and the bottom of a 220px panel, so
    // longer lists scroll by windowing rather than shrinking the rows into unreadability; the window always
    // contains the selected row, because that is the one the user is looking for.
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
        // dim what is behind it: the chips stay half-visible around the list otherwise, and the scrim is
        // also the affordance for "click anywhere to close"
        using (var scrim = new SolidBrush(Mul(Color.FromArgb(120, 0, 0, 0), fade)))
            g.FillRectangle(scrim, 0, 0, w, h);
        var l = MenuListRect(w, n);

        // a soft drop shadow, so the list sits above the panel instead of being pasted onto it: concentric
        // strokes on an expanding path are the cheap way to fake a blur with no second surface
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
        // two edges, not one: a bright hairline where the light would land and a darker one below it, which
        // is what reads as a raised surface rather than a flat rectangle with a border
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
            // the selected row is marked by a short accent bar, not just a lighter fill — hover uses the
            // fill too, and without this the two states were hard to tell apart
            if (cur)
                using (var ab = new SolidBrush(Mul(accent, fade)))
                using (var p = Fx.Rounded(new RectangleF(r.X + 4, r.Y + RowH * 0.26f, 3f, RowH * 0.48f), 1.5f))
                    g.FillPath(ab, p);

            var it = items[idx];
            // a download with no known total has no honest percentage — show what has landed instead
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
        if (!Downloads.HasMore) { _menuOpen = false; return; }   // nothing to switch between
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

    // The hairline border comes from the media panel, where the art is a full-bleed photo that needs an
    // edge. An app icon is not full-bleed: Chrome's is a circle on transparency, so the border traced a
    // rounded SQUARE around a round mark and read as a second stray outline next to the icon's own white
    // ring. Only the fallback glyph tile, which really is a flat filled square, still gets one.
    private static void DrawArt(Graphics g, Bitmap? icon, float fade)
        => IconTile(g, new RectangleF(ArtX, ArtY, ArtSize, ArtSize), ArtSize * 0.24f, icon, fade, 46f,
            border: icon == null);

    // rounded logo tile (matches album art). Fill the rounded path with the icon as a TEXTURE (AA
    // corners) — SetClip gives jagged, "dirty" edges. Download glyph as fallback.
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

    // small rounded app icon for the collapsed pill (left side, like the media pill)
    private static void DrawCollapsedIcon(Graphics g, Bitmap? icon, float x, float y, float sz, float fade)
        => IconTile(g, new RectangleF(x, y, sz, sz), sz * 0.28f, icon, fade, sz * 0.5f, border: false);

    // human byte size: MB up to a GB, then GB with one decimal
    private static string Bytes(long b)
    {
        if (b <= 0) return "0 MB";
        double mb = b / 1048576.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private static readonly Color Ctl = Color.FromArgb(255, 255, 255, 255);
    // round control chip (pause/resume/cancel); red-tinted when it's the destructive one
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

    // collapsed pill (~220x40): app icon on the left, then a slim progress bar + %. While queued
    // ("Waiting…") the whole pill breathes like Claude's compacting pulse — icon + name, no number;
    // the % only appears once the download actually starts. Installing / game-staging keep a sliding
    // indeterminate segment.
    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        _menuOpen = false;   // the switcher belongs to the panel; collapsing is a dismissal
        string? name = Downloads.Name;
        if (name == null) return;
        var icon = Ico();
        var accent = icon != null ? Accent() : Blue;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float sz = h - 14f, ix = 9, iy = (h - sz) / 2f;
        float tx = ix + sz + 12; // content starts right of the icon

        // Two states carry no trustworthy percentage: queued (nothing has started) and a download whose
        // total size nobody reports — a Firefox .part file, a learned app, Xbox staging before the catalog
        // answers. Both get the same honest treatment as Claude's compacting pill: the whole pill breathes
        // instead of a bar that would have to fake a position, with the live byte count as the only number,
        // since bytes-so-far is the one thing actually known.
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
            if (!Downloads.Waiting && Downloads.Downloaded > 0) // show what has actually landed
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
        if (Downloads.Installing) // deploying a package: real work, unknowable position → sliding segment
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

    // Progress WITHOUT a separate bar: the pill itself is the bar. The whole silhouette carries a deep,
    // dim wash of the app's own accent as the track, the vivid accent fills it left-to-right, and a glow
    // rides the leading edge so the motion reads even at a glance. The icon is drawn last, so the fill
    // passes BEHIND it and never sits on top of it. `iconRight` is where the icon ends, so the number can
    // centre in the space that is actually free.
    private static void DrawPillProgress(Graphics g, int w, int h, float fade, int pct, Color accent,
        bool paused, float iconRight)
    {
        var bar = paused ? Dim : accent;
        Fx.PillBar(g, w, h, fade, pct / 100f, bar, 1f);   // both glows now live inside PillBar

        float sz = h - 14f;
        DrawCollapsedIcon(g, Ico(), 9, (h - sz) / 2f, sz, fade); // last, so the fill passes behind it
        if (paused) DrawPausedBadge(g, 9, (h - sz) / 2f, sz, fade);
        DrawCountBadge(g, 9, (h - sz) / 2f, sz, fade, Downloads.Count);

        // One line, one voice: what has landed, then the percentage after a separator, the way the rest of
        // the app writes compound status ("net 15 · api 210 ms"). The bytes are the honest number — they
        // come off the file itself — so they lead, and the percentage follows as the derived figure.
        long done = Downloads.Downloaded, tot = Downloads.Total;

        var oldHint = g.TextRenderingHint;
        // ClearType paints orange/blue subpixel fringes, which on a layered per-pixel-alpha surface read as
        // dirty colour rather than as smoothing
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var f = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

        float left = iconRight + 8f, right = w - 12f;
        // 220px is not much once an icon has taken its share, so offer the fullest line that actually fits
        // and step down rather than letting it clip — the percentage is the part that must never be lost.
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

    // Stop, right there on the collapsed pill — reaching it used to mean opening the panel first. Same
    // action as the panel's: there is no cross-app cancel API, so it quits the downloader (the download
    // stays resumable) or, for a Store item, cancels through AppInstallManager.
    // A stopped/paused download keeps its app icon so you still know what it is; the state goes on top as a
    // small badge, the way the strip badges sessions.
    // How many other downloads are waiting behind this one. Top-right, so it never collides with the pause
    // badge in the opposite corner; absent at one download, because a "1" would be noise.
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
        // two bars = the universal pause mark; a square would read as "stop", which we cannot actually do
        float bw = d * 0.16f, bh = d * 0.42f, gap = d * 0.14f;
        float cx = bx + d / 2f, cy = by + d / 2f;
        using var b = new SolidBrush(Mul(White, fade));
        g.FillRectangle(b, cx - gap / 2f - bw, cy - bh / 2f, bw, bh);
        g.FillRectangle(b, cx + gap / 2f, cy - bh / 2f, bw, bh);
    }

    // round "stop" button — quits the download manager (Downloads.StopProcess), the only reliable way to
    // stop another app's download. Soft red so it reads as stop; brightens on hover. Filled rounded square.
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
