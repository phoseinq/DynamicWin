using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Halo.Interop;

namespace Halo.Shell;

// apps stack DOWNWARD as rows; a row with several sessions of the same app fans RIGHTWARD
internal struct MenuFrame
{
    public bool Show;
    public string[] RowIcons;       // one row per app group, top-to-bottom; index 0 == the closed circle
    public Bitmap?[] RowImages;     // group icon (plain mark when the app has several sessions)
    public int[] RowCounts;         // sessions in the row's rightward fan (0 = row has no fan)
    public Bitmap?[][] SessImages;  // per-row session icons (badged), left-to-right
    public string[][] SessIcons;
    public Color?[] RowRings;       // status ring per row (null = none)
    public Color?[][] SessRings;    // status ring per fanned session (pre-shaded for duplicates)
    public float Open;              // vertical ease 0..1
    public int OpenRow;             // row whose fan is opening (-1 none)
    public float RowOpen;           // horizontal ease 0..1
    public bool Dropping;
    public bool Outward;            // true = new-app arrival: blob flows pill → circle
    public string DropIcon;
    public Bitmap? DropImage;
    public float Drop;              // 0..1
    public float FromX, FromY, ToX, ToY;
}

internal sealed class LayeredNotch
{
    private const int CaptureW = 560, CaptureH = 220;
    // ponytail: circle is full collapsed-band height (40), flush to top, hugging the pill
    public const int CircleD = 40, CircleGap = 4, CircleY = 0;

    private Win32.WndProc _wndProc = null!;
    private int _workLeft, _workTop, _workWidth;
    private Bitmap? _bg;
    private readonly object _bgLock = new();
    private volatile bool _capturing;
    private int _captureVersion;

    public int CaptureVersion => _captureVersion; // bumps when a new background is ready (async capture)

    public IntPtr Hwnd { get; private set; }
    public int WorkLeft => _workLeft;
    public int WorkTop => _workTop;
    public int WorkWidth => _workWidth;

    public void Show()
    {
        var hInstance = Win32.GetModuleHandle(null);
        _wndProc = WndProc;

        var wc = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW), // plain arrow, not the busy spinner
            lpszClassName = "HaloNotchWindow",
        };
        if (Win32.RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        var work = default(Win32.RECT);
        Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref work, 0);
        _workLeft = work.left;
        _workTop = work.top;
        _workWidth = work.right - work.left;

        int exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE;
        Hwnd = Win32.CreateWindowEx(exStyle, "HaloNotchWindow", "Halo", Win32.WS_POPUP,
            _workLeft, _workTop, 10, 10, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
    }

    public void SetVisible(bool visible)
        => Win32.ShowWindow(Hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);

    // A borderless/exclusive fullscreen app (game) covers the whole primary monitor.
    public bool IsFullscreen(IntPtr fg)
    {
        if (fg == IntPtr.Zero || fg == Hwnd || IsDesktopWindow(fg)) return false;
        if (!Win32.GetWindowRect(fg, out var r)) return false;
        int cx = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
        int cy = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        return r.left <= 0 && r.top <= 0 && r.right >= cx && r.bottom >= cy;
    }

    public bool ProbeBehind(out IntPtr behindRoot)
    {
        int cx = _workLeft + _workWidth / 2, cy = _workTop + 6;
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
        System.Threading.Thread.Sleep(12);

        var behind = Win32.WindowFromPoint(new Win32.POINT { X = cx, Y = cy });
        var root = behind == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestor(behind, Win32.GA_ROOT);
        bool isDesktop = IsDesktopWindow(behind) || IsDesktopWindow(root);

        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        behindRoot = isDesktop ? IntPtr.Zero : root;
        return isDesktop;
    }

    // BitBlt only the notch region from the window's own DC (~2.6ms vs ~57ms for a full-window
    // PrintWindow) so the glass can track content in real time. Trade-off: BitBlt returns black
    // for some GPU-composited surfaces; fine for the common apps and games are hidden anyway.
    // Runs on a background thread (BitBlt + optional PrintWindow can take tens of ms) and swaps the
    // blurred background under a lock, so the UI thread never stalls on capture.
    public void CaptureFrom(IntPtr behind)
    {
        if (behind == IntPtr.Zero || _capturing) return;
        _capturing = true;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { DoCapture(behind); } catch { } finally { _capturing = false; }
        });
    }

    private void DoCapture(IntPtr behind)
    {
        if (!Win32.GetWindowRect(behind, out var wr)) return;
        int nx = _workLeft + (_workWidth - CaptureW) / 2, ny = _workTop;
        int sx = nx - wr.left, sy = ny - wr.top;

        var raw = new Bitmap(CaptureW, CaptureH, PixelFormat.Format24bppRgb);
        IntPtr src = Win32.GetWindowDC(behind);
        using (var g = Graphics.FromImage(raw))
        {
            g.Clear(Color.FromArgb(24, 24, 24));
            IntPtr dhdc = g.GetHdc();
            Win32.BitBlt(dhdc, 0, 0, CaptureW, CaptureH, src, sx, sy, Win32.SRCCOPY);
            g.ReleaseHdc(dhdc);
        }
        Win32.ReleaseDC(behind, src);

        // BitBlt returns black for GPU-composited windows (browsers/video) → fall back to PrintWindow
        if (IsMostlyBlack(raw))
        {
            var pw = CaptureViaPrintWindow(behind, wr, sx, sy);
            if (pw != null) { raw.Dispose(); raw = pw; }
        }

        var blurred = Blur(Blur(raw, 8), 5);
        raw.Dispose();
        lock (_bgLock) { var old = _bg; _bg = blurred; old?.Dispose(); }
        System.Threading.Interlocked.Increment(ref _captureVersion);
    }

    private static bool IsMostlyBlack(Bitmap bmp)
    {
        int dark = 0, total = 0;
        for (int y = 4; y < bmp.Height; y += 16)
            for (int x = 4; x < bmp.Width; x += 16)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R < 12 && p.G < 12 && p.B < 12) dark++;
                total++;
            }
        return total > 0 && dark >= total * 0.97f;
    }

    private Bitmap? CaptureViaPrintWindow(IntPtr behind, Win32.RECT wr, int sx, int sy)
    {
        try
        {
            int ww = wr.right - wr.left, wh = wr.bottom - wr.top;
            if (ww <= 0 || wh <= 0 || ww > 10000 || wh > 10000) return null;
            using var full = new Bitmap(ww, wh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = Win32.PrintWindow(behind, hdc, Win32.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok) return null;
            }
            var region = new Bitmap(CaptureW, CaptureH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(region))
            {
                g.Clear(Color.FromArgb(24, 24, 24));
                g.DrawImage(full, new Rectangle(0, 0, CaptureW, CaptureH),
                    new Rectangle(sx, sy, CaptureW, CaptureH), GraphicsUnit.Pixel);
            }
            return region;
        }
        catch { return null; }
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        var buf = new char[80];
        int n = Win32.GetClassName(hwnd, buf, buf.Length);
        var cls = new string(buf, 0, n);
        return cls is "Progman" or "WorkerW" or "SysListView32" or "Shell_TrayWnd";
    }

    public void Render(int w, int h, int radius, int tintAlpha, float contentFade, float collapsedFade, bool glass,
        MenuFrame menu, Action<Graphics, int, int, float> drawContent, Action<Graphics, int, int, float> drawCollapsed)
    {
        int menuX = w + CircleGap;
        // reserve the strip's max extent (transparent padding is free): widest fan + all rows
        int maxFan = 0;
        if (menu.Show)
            foreach (var k in menu.RowCounts) maxFan = Math.Max(maxFan, k);
        int totalW = menu.Show ? menuX + CircleD * (1 + maxFan) : w;
        int totalH = Math.Max(h, menu.Show ? Math.Max(1, menu.RowIcons.Length) * CircleD : 0);

        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = totalW,
            biHeight = -totalH,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr dib = Win32.CreateDIBSection(screenDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr oldObj = Win32.SelectObject(memDc, dib);

        using (var bmp = new Bitmap(totalW, totalH, totalW * 4, PixelFormat.Format32bppPArgb, bits))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            DrawShape(g, w, h, radius, tintAlpha, glass);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (collapsedFade > 0.01f) drawCollapsed(g, w, h, collapsedFade);
            drawContent(g, w, h, contentFade);
            float ca = 1f - contentFade;
            if (menu.Show && ca > 0.01f && !menu.Dropping) DrawMenu(g, menuX, w, tintAlpha, glass, menu, ca);
            if (menu.Dropping) DrawDrop(g, menu, tintAlpha, w, h); // the circle itself flows in (no static circle)
        }

        var size = new Win32.SIZE { cx = totalW, cy = totalH };
        var src = new Win32.POINT { X = 0, Y = 0 };
        var dst = new Win32.POINT { X = _workLeft + (_workWidth - w) / 2, Y = _workTop };
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        Win32.UpdateLayeredWindow(Hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Win32.ULW_ALPHA);

        Win32.SelectObject(memDc, oldObj);
        Win32.DeleteObject(dib);
        Win32.DeleteDC(memDc);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void DrawShape(Graphics g, int w, int h, int radius, int tintAlpha, bool glass)
    {
        const int ss = 2;
        using var big = new Bitmap(w * ss, h * ss, PixelFormat.Format32bppPArgb);
        using (var bg = Graphics.FromImage(big))
        {
            bg.SmoothingMode = SmoothingMode.AntiAlias;
            bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bg.Clear(Color.Transparent);
            using var path = PillPath(w * ss, h * ss, radius * ss);
            lock (_bgLock)
            {
                if (glass && _bg != null)
                {
                    var clip = bg.Clip;
                    bg.SetClip(path);
                    int sx = (CaptureW - w) / 2;
                    bg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    bg.DrawImage(_bg, new Rectangle(0, 0, w * ss, h * ss), new Rectangle(sx, 0, w, h), GraphicsUnit.Pixel);
                    bg.Clip = clip;
                }
            }
            using var tint = new SolidBrush(Color.FromArgb(tintAlpha, 8, 8, 8));
            bg.FillPath(tint, path);
        }
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(big, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w * ss, h * ss), GraphicsUnit.Pixel);
    }

    // Circle → vertical capsule menu of app icons. Same style as the pill (glass over apps,
    // black on desktop). Rendered into a temp bitmap so it fades uniformly on pill-expand.
    private void DrawMenu(Graphics g, int x, int pillW, int tintAlpha, bool glass, MenuFrame menu, float alpha)
    {
        int rows = menu.RowIcons.Length;
        float openV = Math.Max(0f, menu.Open);
        float hf = CircleD + (rows - 1) * CircleD * openV;              // apps stack downward
        int or_ = menu.OpenRow;
        float rowEase = Math.Max(0f, menu.RowOpen);
        float extf = or_ >= 0 && or_ < rows ? menu.RowCounts[or_] * CircleD * rowEase : 0f;
        if (or_ > 0 && CircleD + or_ * CircleD > hf + 0.5f) extf = 0f;  // row not revealed yet → no fan
        int mw = (int)Math.Ceiling(CircleD + extf);
        int mh = (int)Math.Ceiling(hf);
        const int ss = 2; // supersample the whole strip so icons downscale crisp (same as the pill shape)
        int D = CircleD * ss;

        using var c = new Bitmap(mw * ss, mh * ss, PixelFormat.Format32bppPArgb);
        using (var cg = Graphics.FromImage(c))
        {
            cg.SmoothingMode = SmoothingMode.AntiAlias;
            cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            cg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            cg.Clear(Color.Transparent);

            // union of the vertical app strip and the open row's rightward fan (both flat-top pills)
            using var path = new GraphicsPath(FillMode.Winding);
            using (var v = PillPath(D, mh * ss, D / 2))
                path.AddPath(v, false);
            if (extf > 0.5f)
                using (var hp = PillPath((int)((CircleD + extf) * ss), D, D / 2))
                {
                    using var m = new Matrix(1, 0, 0, 1, 0, or_ * D);
                    hp.Transform(m);
                    path.AddPath(hp, false);
                }

            int srcX = (CaptureW - pillW) / 2 + x;
            lock (_bgLock)
            {
                if (glass && _bg != null && srcX >= 0 && srcX + mw <= _bg.Width && CircleY + mh <= _bg.Height)
                {
                    var clip = cg.Clip;
                    cg.SetClip(path);
                    cg.DrawImage(_bg, new Rectangle(0, 0, mw * ss, mh * ss),
                        new Rectangle(srcX, CircleY, mw, mh), GraphicsUnit.Pixel);
                    cg.Clip = clip;
                }
            }
            using (var b = new SolidBrush(Color.FromArgb(tintAlpha, 8, 8, 8)))
                cg.FillPath(b, path);

            using var f = new Font("Segoe MDL2 Assets", D * 0.45f, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            void Cell(string icon, Bitmap? img, float cx, float cy, float ia, Color? ring)
            {
                if (ia <= 0.01f) return;
                if (img != null)
                {
                    // faint icon-accent wash, clipped to the strip so it hugs the flat top like the pill
                    var accent = Widgets.Fx.AccentOf(img);
                    if (accent != Widgets.Fx.White)
                    {
                        var clip = cg.Clip;
                        cg.SetClip(path);
                        using var gb = new SolidBrush(Color.FromArgb((int)(20 * ia), accent));
                        cg.FillRectangle(gb, new RectangleF(cx, cy, D, D));
                        cg.Clip = clip;
                    }
                    DrawCircleImage(cg, img, cx, cy, D, ia);
                    if (ring is { } rc) // status ring hugging the icon, same style as the pill's
                    {
                        float inset = D * 0.19f - 2.5f * ss, dd = D - inset * 2;
                        using var pen = new Pen(Color.FromArgb((int)(140 * ia), rc), 1.9f * ss);
                        cg.DrawEllipse(pen, cx + inset, cy + inset, dd, dd);
                    }
                    return;
                }
                using var ib = new SolidBrush(Color.FromArgb((int)(235 * ia), 255, 255, 255));
                cg.DrawString(icon, f, ib, new RectangleF(cx, cy, D, D), sf);
            }

            for (int i = 0; i < rows; i++)
                Cell(menu.RowIcons[i], menu.RowImages[i], 0, i * D,
                    Math.Clamp((hf - i * CircleD) / CircleD, 0f, 1f), menu.RowRings[i]);
            if (extf > 0.5f)
                for (int j = 0; j < menu.RowCounts[or_]; j++)
                    Cell(menu.SessIcons[or_][j], menu.SessImages[or_][j], (j + 1) * D, or_ * D,
                        Math.Clamp((extf - j * CircleD) / CircleD, 0f, 1f), menu.SessRings[or_][j]);
        }

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var dst = new Rectangle(x, CircleY, mw, mh);
        if (alpha >= 0.999f) { g.DrawImage(c, dst, 0, 0, c.Width, c.Height, GraphicsUnit.Pixel); return; }

        // merge into the pill: as the pill expands, shrink the circle toward the pill edge while fading
        var stt = g.Save();
        float ax = x, ay = CircleY + CircleD / 2f;
        g.TranslateTransform(ax, ay);
        g.ScaleTransform(alpha, alpha);
        g.TranslateTransform(-ax, -ay);
        using (var attr = new ImageAttributes())
        {
            attr.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
            g.DrawImage(c, dst, 0, 0, c.Width, c.Height, GraphicsUnit.Pixel, attr);
        }
        g.Restore(stt);
    }

    // The picked icon flies from its menu slot into the main pill, shrinking and fading on landing.
    // The picked app flows into the pill like liquid: a metaball bridge connects the pill's rounded
    // end to the blob as it travels in, then they fuse.
    private void DrawDrop(Graphics g, MenuFrame menu, int tintAlpha, int w, int h)
    {
        // easeOutBack: the blob is pushed toward the pill, overshoots slightly, then settles (liquid bounce)
        float p = menu.Drop - 1f;
        const float k1 = 1.9f, k3 = k1 + 1f;
        float e = 1f + k3 * p * p * p + k1 * p * p;
        float bx = menu.FromX + (menu.ToX - menu.FromX) * e;
        float by = menu.FromY + (menu.ToY - menu.FromY) * e;
        // inward: shrinks as it fuses; outward (arrival): grows to full circle size as it lands
        float r2 = CircleD / 2f * (menu.Outward ? 0.8f + 0.2f * e : 1f - 0.2f * e);
        var blob = new PointF(bx, by);
        var c1 = new PointF(w - h / 2f, h / 2f); // pill's right rounded end
        float r1 = h / 2f;

        var fill = Color.FromArgb(Math.Min(tintAlpha + 50, 255), 8, 8, 8);
        using (var b = new SolidBrush(fill))
        {
            Metaball(g, b, c1, r1, blob, r2);
            g.FillEllipse(b, blob.X - r2, blob.Y - r2, r2 * 2, r2 * 2);
        }

        // inward: icon fades out on landing; outward: fades in as the blob leaves the pill
        float a = menu.Outward
            ? Math.Clamp(menu.Drop / 0.25f, 0f, 1f)
            : menu.Drop < 0.8f ? 1f : 1f - (menu.Drop - 0.8f) / 0.2f;
        if (menu.DropImage != null)
        {
            DrawCircleImage(g, menu.DropImage, blob.X - r2, blob.Y - r2, r2 * 2, a);
            return;
        }
        using var f = new Font("Segoe MDL2 Assets", r2 * 0.9f, GraphicsUnit.Pixel);
        using var ib = new SolidBrush(Color.FromArgb((int)(235 * a), 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(menu.DropIcon, f, ib, new RectangleF(blob.X - r2, blob.Y - r2, r2 * 2, r2 * 2), sf);
    }

    // Standard metaball connector: fills the gooey bridge between two circles (nothing when apart or fused).
    private static void Metaball(Graphics g, Brush brush, PointF c1, float r1, PointF c2, float r2)
    {
        float dx = c2.X - c1.X, dy = c2.Y - c1.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d <= 0.001f || d >= r1 + r2 || d <= MathF.Abs(r1 - r2)) return;

        const float handle = 2.4f, v = 0.5f;
        float u1 = MathF.Acos((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d));
        float u2 = MathF.Acos((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d));
        float ab = MathF.Atan2(dy, dx);
        float maxSpread = MathF.Acos((r1 - r2) / d);

        float a1 = ab + u1 + (maxSpread - u1) * v;
        float a2 = ab - u1 - (maxSpread - u1) * v;
        float a3 = ab + MathF.PI - u2 - (MathF.PI - u2 - maxSpread) * v;
        float a4 = ab - MathF.PI + u2 + (MathF.PI - u2 - maxSpread) * v;

        var p1 = Pt(c1, r1, a1); var p2 = Pt(c1, r1, a2);
        var p3 = Pt(c2, r2, a3); var p4 = Pt(c2, r2, a4);

        float total = r1 + r2;
        float d2 = Math.Min(v * handle, Dist(p1, p3) / total) * Math.Min(1f, d * 2f / total);
        float h1 = r1 * d2, h2 = r2 * d2;

        using var path = new GraphicsPath();
        path.AddBezier(p1, Pt(p1, h1, a1 - MathF.PI / 2), Pt(p3, h2, a3 + MathF.PI / 2), p3);
        path.AddLine(p3, p4);
        path.AddBezier(p4, Pt(p4, h2, a4 - MathF.PI / 2), Pt(p2, h1, a2 + MathF.PI / 2), p2);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static PointF Pt(PointF c, float r, float a) => new(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a));
    private static float Dist(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return MathF.Sqrt(dx * dx + dy * dy); }

    // Draw a bitmap as a circular icon (cover-fit) inset in a CircleD-ish box, with alpha.
    // HQ-scale into a square then fill an ellipse with it as a texture, so edges are anti-aliased.
    private static void DrawCircleImage(Graphics g, Bitmap img, float x, float y, float box, float alpha)
    {
        float inset = box * 0.19f, d = box - inset * 2; // ~10% smaller icon than before
        var circle = new RectangleF(x + inset, y + inset, d, d);
        int s = Math.Max(1, (int)Math.Ceiling(d));

        using var scaled = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(scaled))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            sg.SmoothingMode = SmoothingMode.HighQuality;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
            int side = Math.Min(img.Width, img.Height);
            sg.DrawImage(img, new Rectangle(0, 0, s, s),
                (img.Width - side) / 2, (img.Height - side) / 2, side, side, GraphicsUnit.Pixel, ia);
        }

        using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
        tb.TranslateTransform(circle.X, circle.Y);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new GraphicsPath()) { p.AddEllipse(circle); g.FillPath(tb, p); }
        g.SmoothingMode = old;
    }


    private static Bitmap Blur(Bitmap src, int factor)
    {
        int sw = Math.Max(1, src.Width / factor), sh = Math.Max(1, src.Height / factor);
        var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, sw, sh), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
        }
        var big = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(small, new Rectangle(0, 0, src.Width, src.Height), new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
        }
        small.Dispose();
        return big;
    }

    private static GraphicsPath PillPath(int w, int h, int r)
    {
        int d = r * 2;
        var p = new GraphicsPath();
        p.AddLine(0, 0, w, 0);
        p.AddArc(w - d, h - d, d, d, 0, 90);
        p.AddArc(0, h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_DESTROY)
        {
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
