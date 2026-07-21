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
    public float Appear;            // eased 0..1 — the strip grows/fades in instead of popping
    public string[] RowIcons;       // one row per app group, top-to-bottom; index 0 == the closed circle
    public Bitmap?[] RowImages;     // group icon (plain mark when the app has several sessions)
    public int[] RowCounts;         // sessions in the row's rightward fan (0 = row has no fan)
    public Bitmap?[][] SessImages;  // per-row session icons (badged), left-to-right
    public string[][] SessIcons;
    public Color?[] RowRings;       // status ring per row (null = none)
    public float[] RowProgress;     // per row: >=0 draws the ring as a 0..1 progress arc (download %), else -1
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

    // a live privacy dot needs real space between the pill and the swap circle (4px isn't enough), so
    // the strip slides right by this much while mic/cam is in use, and the dot centres in the gap.
    private const int PrivacyGap = 10;
    public static int PrivacyPad => Widgets.Privacy.Active ? PrivacyGap : 0;

    private Win32.WndProc _wndProc = null!;
    private int _workLeft, _workTop, _workWidth;

    // global pill scale (corner-drag resize). Rendering applies it as one ScaleTransform,
    // so icons/text/clips all stay in lockstep; hit-testing scales in NotchController.
    public float Scale = 1f;
    public float OffsetX; // horizontal shift from centre (drag-to-move); the controller owns/persists it
    public float HandleAlpha; // corner resize-handle visibility, 0..1 (controller fades it)
    private static readonly string ScalePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "scale");

    public void LoadScale()
    {
        try
        {
            if (float.TryParse(System.IO.File.ReadAllText(ScalePath),
                    System.Globalization.CultureInfo.InvariantCulture, out var s))
                Scale = Math.Clamp(s, 0.7f, 1.6f);
        }
        catch { }
    }

    public void SaveScale()
    {
        try
        {
            System.IO.File.WriteAllText(ScalePath,
                Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { }
    }
    private Bitmap? _bg;
    private readonly object _bgLock = new();
    private volatile bool _capturing;
    private int _captureVersion;

    public int CaptureVersion => _captureVersion; // bumps when a new background is ready (async capture)

    public IntPtr Hwnd { get; private set; }
    public int WorkLeft => _workLeft;
    public int WorkTop => _workTop;
    public int WorkWidth => _workWidth;

    // fires with the captured image when a new bitmap lands on the clipboard (PrtSc / Win+Shift+S) —
    // Windows' own screen-snip "copied" toast never reaches UserNotificationListener (verified: not in
    // the listener, the Notification table, or TransientTable), so the controller mirrors it from here.
    // bool = true when it's a real screen capture (snip host owns the clipboard, or a raw PrtSc with
    // no owner), false when a normal app just copied an image — so the banner can say which it was.
    public event Action<Bitmap, bool>? ClipboardImage;
    private uint _lastClipSeq;
    private long _lastClipTick;

    // snip/capture tools that own the clipboard after a shot; anyone else = a plain image copy
    private static readonly string[] SnipHosts =
        { "screenclippinghost", "snippingtool", "screensketch", "shellexperiencehost",
          "greenshot", "sharex", "lightshot", "flameshot", "snagit32", "snagiteditor", "picpick" };

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
        LoadScale();

        int exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE;
        Hwnd = Win32.CreateWindowEx(exStyle, "HaloNotchWindow", "Halo", Win32.WS_POPUP,
            _workLeft, _workTop, 10, 10, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        // stay off screenshots / screen recordings — the pill is chrome, not content.
        // HALO_CAPTURABLE=1 keeps it capturable (demo GIFs / README recordings).
        if (Environment.GetEnvironmentVariable("HALO_CAPTURABLE") != "1")
            Win32.SetWindowDisplayAffinity(Hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
        Win32.AddClipboardFormatListener(Hwnd); // watch for screenshot images landing on the clipboard
        // File Tray: register a real OLE drop target so dragging a file over the pill reveals the tray.
        // Keep the instance alive (field) — RegisterDragDrop only holds a COM ref, GC would collect it.
        _dropTarget = new Halo.Interop.FileDropTarget();
        Win32.RegisterDragDrop(Hwnd, _dropTarget);
    }

    private Halo.Interop.FileDropTarget? _dropTarget;

    public void SetVisible(bool visible)
        => Win32.ShowWindow(Hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);

    // WS_EX_TOPMOST alone loses the top z-band to a fullscreen/exclusive app (game/movie), so a pinned
    // pill ends up visible-but-buried — especially after autostart, where the pill exists before the app.
    // Re-assert HWND_TOPMOST (no move/size/activate) so it actually draws over them. Called ~1×/s when pinned.
    public void AssertTopmost()
        => Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

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
        int menuX = w + CircleGap + PrivacyPad; // strip slides right to open a gap for the privacy dot
        // reserve the strip's max extent (transparent padding is free): widest fan + all rows
        int maxFan = 0;
        if (menu.Show)
            foreach (var k in menu.RowCounts) maxFan = Math.Max(maxFan, k);
        int totalW = menu.Show ? menuX + CircleD * (1 + maxFan) : w;
        int totalH = Math.Max(h, menu.Show ? Math.Max(1, menu.RowIcons.Length) * CircleD : 0);

        // privacy dot(s) sit in the gap between the pill and the swap circle, on neither. Reserve
        // width (covers the case with no strip) AND enough height for a vertical stack.
        bool privacy = Widgets.Privacy.Active;
        if (privacy)
        {
            totalW = Math.Max(totalW, w + CircleGap + PrivacyGap);
            int nDots = (Widgets.Privacy.Mic ? 1 : 0) + (Widgets.Privacy.Cam ? 1 : 0);
            totalH = Math.Max(totalH, (int)Math.Ceiling(DotTop + (nDots - 1) * DotStep + DotR + 2f));
        }

        // resize: everything is laid out in logical units, one ScaleTransform blows it all up
        // together — icons, text, clips and the strip stay in lockstep at any size
        float S = Scale;
        int pw = (int)MathF.Ceiling(totalW * S), ph = (int)MathF.Ceiling(totalH * S);

        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = pw,
            biHeight = -ph,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr dib = Win32.CreateDIBSection(screenDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr oldObj = Win32.SelectObject(memDc, dib);

        using (var bmp = new Bitmap(pw, ph, pw * 4, PixelFormat.Format32bppPArgb, bits))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(S, S);
            DrawShape(g, w, h, radius, tintAlpha, glass);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (collapsedFade > 0.01f) drawCollapsed(g, w, h, collapsedFade);
            drawContent(g, w, h, contentFade);
            if (HandleAlpha > 0.01f && contentFade > 0.5f)
            {
                // resize handle: a short stroke hugging the bottom-right corner curve — kept tight
                // to the edge and short so it stays clear of the panel's bottom-right text
                using var hp = new Pen(Color.FromArgb((int)(160 * HandleAlpha * contentFade), 255, 255, 255), 3f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
                int m = 3;
                g.DrawArc(hp, w - 2 * radius + m, h - 2 * radius + m, 2 * (radius - m), 2 * (radius - m), 25, 40);
            }
            float ca = 1f - contentFade;
            if (menu.Show && ca > 0.01f && !menu.Dropping) DrawMenu(g, menuX, w, tintAlpha, glass, menu, ca);
            if (menu.Dropping) DrawDrop(g, menu, tintAlpha, w, h); // the circle itself flows in (no static circle)
            if (privacy) DrawPrivacyDots(g, w);
        }

        var size = new Win32.SIZE { cx = pw, cy = ph };
        var src = new Win32.POINT { X = 0, Y = 0 };
        var dst = new Win32.POINT { X = _workLeft + (_workWidth - (int)(w * S)) / 2 + (int)OffsetX, Y = _workTop };
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

    // privacy indicator: small colored dot(s) centred in the gap between the pill and the swap circle,
    // touching neither. A THIN black outline (not a fat ring — a fat ring reads as a donut/"سوراخ"), no
    // glow. Orange = mic live, green = camera live; the first sits high in the band, extras stack below.
    private const float DotR = 3.3f, DotRing = 0.9f, DotStep = 8.5f, DotTop = 9f;
    private static readonly Color MicColor = Color.FromArgb(255, 159, 10);  // orange
    private static readonly Color CamColor = Color.FromArgb(48, 209, 88);   // green
    private static void DrawPrivacyDots(Graphics g, int pillW)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = pillW + (CircleGap + PrivacyGap) / 2f; // centre of the pill↔circle gap
        float y = DotTop;                                 // high in the band, not centred
        if (Widgets.Privacy.Mic) { Dot(g, cx, y, MicColor); y += DotStep; }
        if (Widgets.Privacy.Cam) { Dot(g, cx, y, CamColor); }
    }

    private static void Dot(Graphics g, float cx, float cy, Color c)
    {
        using (var kb = new SolidBrush(Color.FromArgb(230, 0, 0, 0)))   // thin dark outline
            g.FillEllipse(kb, cx - DotR, cy - DotR, DotR * 2, DotR * 2);
        float ri = DotR - DotRing;                                      // colored centre fills most of it
        using var cb = new SolidBrush(c);
        g.FillEllipse(cb, cx - ri, cy - ri, ri * 2, ri * 2);
    }

    internal void DrawShape(Graphics g, int w, int h, int radius, int tintAlpha, bool glass)
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
        // bilinear, NOT bicubic: bicubic's negative lobes undershoot at the shape's dark→transparent
        // premultiplied edge, leaving a thin dark rim ("خط سیاه") that shows over light content behind.
        // The 2x supersample already smooths the curve, so bilinear costs no visible sharpness.
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(big, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w * ss, h * ss), GraphicsUnit.Pixel);
    }

    // Circle → vertical capsule menu of app icons. Same style as the pill (glass over apps,
    // black on desktop). Rendered into a temp bitmap so it fades uniformly on pill-expand.
    private void DrawMenu(Graphics g, int x, int pillW, int tintAlpha, bool glass, MenuFrame menu, float alpha)
    {
        alpha *= menu.Appear; // soft grow-out-of-the-pill entrance (the <1 path below scales + fades)
        if (alpha <= 0.01f) return;
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

            void Cell(string icon, Bitmap? img, float cx, float cy, float ia, Color? ring, float progress = -1f)
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
                }
                else
                {
                    using var ib = new SolidBrush(Color.FromArgb((int)(235 * ia), 255, 255, 255));
                    cg.DrawString(icon, f, ib, new RectangleF(cx, cy, D, D), sf);
                }
                if (ring is { } rc) // status ring hugging the icon, same style as the pill's
                {
                    float inset = D * 0.19f - 2.5f * ss, dd = D - inset * 2;
                    var rr = new RectangleF(cx + inset, cy + inset, dd, dd);
                    if (progress >= 0f)
                    {
                        // download %: dim full track + a bright arc from 12 o'clock, clockwise
                        cg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var track = new Pen(Color.FromArgb((int)(55 * ia), rc), 1.9f * ss);
                        cg.DrawEllipse(track, rr);
                        if (progress > 0.001f)
                            using (var arc = new Pen(Color.FromArgb((int)(230 * ia), rc), 2.2f * ss)
                            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                                cg.DrawArc(arc, rr, -90f, 360f * Math.Clamp(progress, 0f, 1f));
                    }
                    else
                    {
                        using var pen = new Pen(Color.FromArgb((int)(140 * ia), rc), 1.9f * ss);
                        cg.DrawEllipse(pen, rr);
                    }
                }
            }

            for (int i = 0; i < rows; i++)
                Cell(menu.RowIcons[i], menu.RowImages[i], 0, i * D,
                    Math.Clamp((hf - i * CircleD) / CircleD, 0f, 1f), menu.RowRings[i], menu.RowProgress[i]);
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

    // a new image on the clipboard. Dedupe: one snip can raise several updates —
    // GetClipboardSequenceNumber only bumps on a real write (our reads don't touch it), plus an 800ms
    // guard swallows the burst a single copy sometimes fires. Text copies (no CF_BITMAP) are ignored.
    private void HandleClipboard()
    {
        uint seq = Win32.GetClipboardSequenceNumber();
        if (seq == _lastClipSeq) return;
        _lastClipSeq = seq;
        long now = Environment.TickCount64;
        if (now - _lastClipTick < 800) return;
        if (!Win32.IsClipboardFormatAvailable(Win32.CF_BITMAP)) return;
        bool shot = OwnerIsCapture(); // read the owner BEFORE OpenClipboard (which would make US the owner)
        var bmp = ReadClipboardBitmap();
        if (bmp != null) { _lastClipTick = now; ClipboardImage?.Invoke(bmp, shot); }
    }

    // screen capture vs plain image copy: a snip host (or a raw PrtSc with no owner) → capture;
    // any real app owning the clipboard (chrome, an image editor, …) → a copy
    private static bool OwnerIsCapture()
    {
        try
        {
            IntPtr owner = Win32.GetClipboardOwner();
            if (owner == IntPtr.Zero) return true; // PrtSc full-screen leaves no window owner
            Win32.GetWindowThreadProcessId(owner, out uint pid);
            if (pid == 0) return true;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            string pn = p.ProcessName.ToLowerInvariant();
            foreach (var s in SnipHosts) if (pn.Contains(s)) return true;
            return false;
        }
        catch { return true; } // unsure → default to "Screenshot" (the historical behaviour)
    }

    private Bitmap? ReadClipboardBitmap()
    {
        if (!Win32.OpenClipboard(Hwnd)) return null; // another app holds it → skip; the next copy retries
        try
        {
            IntPtr h = Win32.GetClipboardData(Win32.CF_BITMAP); // clipboard owns this HBITMAP — don't delete
            if (h == IntPtr.Zero) return null;
            using var tmp = Image.FromHbitmap(h);
            return new Bitmap(tmp);
        }
        catch { return null; }
        finally { Win32.CloseClipboard(); }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_DESTROY)
        {
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        if (msg == Win32.WM_CLIPBOARDUPDATE)
        {
            HandleClipboard();
            return IntPtr.Zero;
        }
        if (msg is Win32.WM_DISPLAYCHANGE or Win32.WM_SETTINGCHANGE)
        {
            // monitor plugged/unplugged or work area moved: stale coords leave the pill
            // off-screen — re-read; the next UpdateLayeredWindow repositions from these
            var work = default(Win32.RECT);
            Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref work, 0);
            _workLeft = work.left;
            _workTop = work.top;
            _workWidth = work.right - work.left;
            lock (_bgLock) { _bg?.Dispose(); _bg = null; } // wallpaper snapshot is per-geometry
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
