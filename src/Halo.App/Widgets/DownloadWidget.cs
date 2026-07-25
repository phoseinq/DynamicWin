using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Halo.Widgets;

// Read-only download progress in the pill. Data comes from the Downloads scanner (window-title %).
// No transport buttons: there's no cross-app API to pause/cancel another program's download.
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

    // Layout mirrors the media panel: art top-left, title + status, a horizontal bar, control chips
    // centred in the right column at y=158. Same geometry so downloads sit alongside the other widgets.
    private const float ArtX = 26, ArtY = 26, ArtSize = 132;
    private static RectangleF[] CtlRects(int w, int n)
    {
        const float size = 40, gap = 18;
        float colL = ArtX + ArtSize + 22, colR = w - 26, cx = (colL + colR) / 2f;
        float total = n * size + (n - 1) * gap, x0 = cx - total / 2f, y = 158;
        var r = new RectangleF[n];
        for (int i = 0; i < n; i++) r[i] = new RectangleF(x0 + i * (size + gap), y, size, size);
        return r;
    }

    // the collapsed pill's own Stop, so a runaway download can be killed at a glance without opening the
    // panel first. Same two actions as the panel: Store items cancel through AppInstallManager, everything
    // else quits the downloader, since no cross-app per-download cancel exists.
    // Deliberately none. A Stop lived here briefly and was removed: the collapsed pill is a glance
    // surface the user reaches toward, so a control on it fires by accident. Stop, cancel and the file
    // path all live in the expanded panel, where there is room to label them.
    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> CollapsedButtons(int w, int h)
        => Array.Empty<(RectangleF, Action<PointF>)>();

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
    {
        if (Downloads.Name == null) return Array.Empty<(RectangleF, Action<PointF>)>();
        if (Downloads.IsStore && Downloads.CanControl)
        {
            var r = CtlRects(w, 2);
            return new (RectangleF, Action<PointF>)[]
            {
                (r[0], _ => { if (Downloads.Paused) Downloads.StoreResume(); else Downloads.StorePause(); }),
                (r[1], _ => Downloads.StoreCancel()),
            };
        }
        if (Downloads.Hwnd == IntPtr.Zero)
        {
            // browser/partial download: reveal the file, or bring the fetching app forward to cancel there
            if (Downloads.FilePath is not { Length: > 0 }) return Array.Empty<(RectangleF, Action<PointF>)>();
            var pr = CtlRects(w, 2);
            return new (RectangleF, Action<PointF>)[]
            {
                (pr[0], _ => Downloads.ShowInFolder()),
                (pr[1], _ => Downloads.RevealOwner()),
            };
        }
        var wr = CtlRects(w, 2); // window-scanned downloader: open it, or stop (quit) it
        return new (RectangleF, Action<PointF>)[]
        {
            (wr[0], _ => Downloads.Reveal()),
            (wr[1], _ => Downloads.StopProcess()),
        };
    }

    private static float _fracShown = -1f;
    private static string? _lastName;

    // media-style panel: rounded logo tile + name + status line + horizontal progress bar with byte
    // end-labels + glass control chips. Indeterminate phases show a sliding segment; the fill eases.
    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        if (fade <= 0.01f) return;
        string? name = Downloads.Name;
        if (name == null) return;
        // Two different questions that used to share one flag: "is the bar indeterminate" and "what is the
        // app doing". NoPct only means the total size is unknown, which is now the normal case for a browser
        // download, so folding it into `installing` labelled ordinary downloads "Installing…".
        bool indeterminate = Downloads.Installing || Downloads.Waiting || (Downloads.NoPct && !Downloads.Paused);
        bool installing = Downloads.Installing;
        bool paused = Downloads.Paused;
        int pct = Math.Clamp(Downloads.Percent, 0, 100);
        long done = Downloads.Downloaded, tot = Downloads.Total;
        var icon = Ico();
        var accent = icon != null ? Accent() : Blue;
        float pulse = 0.5f + 0.5f * MathF.Sin(Environment.TickCount64 / 480f);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        // logo tile (rounded square, like album art)
        Fx.Glow(g, w, h, fade * (indeterminate ? 0.55f + 0.45f * pulse : 1f),
            ArtX + ArtSize / 2f, ArtY + ArtSize / 2f, w * 0.85f, h * 1.2f, 38, accent);
        DrawArt(g, icon, fade);

        float tx = ArtX + ArtSize + 22, tw = w - tx - 26;
        using var titleF = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var bodyF = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var timeF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using (var tb = new SolidBrush(Mul(White, fade)))
            DrawEllipsized(g, name, titleF, tb, tx, 34, tw, 30);
        string state = Downloads.Waiting ? "Waiting…" : installing ? "Installing…"
            : paused ? "Paused" : "Downloading";
        string sub = indeterminate || Downloads.NoPct ? state : $"{state}   ·   {pct}%";
        using (var lb = new SolidBrush(Mul(Dim, fade)))
            DrawEllipsized(g, sub, bodyF, lb, tx, 66, tw, 22);

        // horizontal progress bar (matches the media seek bar)
        float by = 116, bh = 5;
        Fill(g, tx, by, tw, bh, Mul(Track, fade), bh / 2);
        if (indeterminate)
        {
            float seg = tw * 0.34f, sx = tx + (tw - seg) * (0.5f + 0.5f * MathF.Sin(Environment.TickCount64 / 700f));
            Fill(g, sx, by, seg, bh, Mul(accent, fade * (0.5f + 0.5f * pulse)), bh / 2);
        }
        else
        {
            float frac = tot > 1_048_576 ? Math.Clamp(done / (float)tot, 0f, 1f) : pct / 100f;
            if (name != _lastName) { _lastName = name; _fracShown = frac; } // new download → snap
            _fracShown = _fracShown < 0 ? frac : _fracShown + (frac - _fracShown) * 0.18f;
            if (Math.Abs(frac - _fracShown) < 0.002f) _fracShown = frac;
            if (_fracShown > 0) Fill(g, tx, by, tw * _fracShown, bh, Mul(paused ? Dim : accent, fade), bh / 2);
            // byte labels at the bar ends, like elapsed/total time
            using var eb = new SolidBrush(Mul(Dim, fade));
            if (done > 1_048_576) g.DrawString(Bytes(done), timeF, eb, tx, by + 9);
            if (tot > 1_048_576)
            {
                var ts = g.MeasureString(Bytes(tot), timeF);
                g.DrawString(Bytes(tot), timeF, eb, tx + tw - ts.Width, by + 9);
            }
        }

        // where the file is landing — the question the panel could not answer before. Folder only, since
        // the filename is already the title, and the middle is elided so a deep path still reads.
        if (Downloads.FilePath is { Length: > 0 } fp)
        {
            string? dir = null;
            try { dir = System.IO.Path.GetDirectoryName(fp); } catch { }
            if (!string.IsNullOrEmpty(dir))
                using (var pb = new SolidBrush(Mul(Color.FromArgb(120, 255, 255, 255), fade)))
                using (var psf = new StringFormat(StringFormat.GenericTypographic)
                { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(dir, timeF, pb, new RectangleF(tx, by + 26, tw, 18), psf);
        }

        // control chips, centred in the right column at y=158
        if (Downloads.IsStore && Downloads.CanControl)
        {
            var r = CtlRects(w, 2);
            DrawCtl(g, r[0], paused ? 0xE768 : 0xE769, fade, danger: false); // resume ▶ / pause ⏸
            DrawCtl(g, r[1], 0xE711, fade, danger: true);                    // cancel ✕
        }
        else if (Downloads.Hwnd != IntPtr.Zero)
        {
            var wr = CtlRects(w, 2);
            DrawCtl(g, wr[0], 0xE838, fade, danger: false); // FolderOpen — bring the downloader to front
            DrawStop(g, wr[1], fade);                        // real stop — quits the download manager
        }
        else if (Downloads.FilePath is { Length: > 0 })
        {
            // A browser download had no controls at all, because it has no window we scanned. Two honest
            // ones: show the file, and surface the app that is fetching it so the download can be cancelled
            // where it actually lives. No "stop" chip here — for a browser the only stop we could implement
            // is killing the browser or deleting the half-written file, and the project's rule is to hide a
            // control the underlying app cannot honour rather than ship a silent no-op.
            var br = CtlRects(w, 2);
            DrawCtl(g, br[0], 0xE838, fade, danger: false); // FolderOpen — reveal the file
            DrawCtl(g, br[1], 0xE7C4, fade, danger: false); // Switch app — bring the downloader forward
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
        float fill = w * pct / 100f;
        var bar = paused ? Dim : accent;
        Fx.PillBar(g, w, h, fade, pct / 100f, bar, 1f);
        if (fill > 0.5f) Fx.Glow(g, w, h, fade, fill, h / 2f, h * 1.15f, h * 1.5f, 22, bar);

        float sz = h - 14f;
        DrawCollapsedIcon(g, Ico(), 9, (h - sz) / 2f, sz, fade); // last, so the fill passes behind it
        if (paused) DrawPausedBadge(g, 9, (h - sz) / 2f, sz, fade);

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
