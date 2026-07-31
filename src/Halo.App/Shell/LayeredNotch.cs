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
    public float[] RowImageOffsets; // optical x correction in logical pixels
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
        // stay off screenshots / screen recordings by default — the pill is chrome, not content.
        // Pinned flips this (SetCapturable): a pinned pill is deliberate UI the user wants in recordings.
        SetCapturable(false);
        Win32.AddClipboardFormatListener(Hwnd); // watch for screenshot images landing on the clipboard
        // File Tray: register a real OLE drop target so dragging a file over the pill reveals the tray.
        // Keep the instance alive (field) — RegisterDragDrop only holds a COM ref, GC would collect it.
        _dropTarget = new Halo.Interop.FileDropTarget();
        Win32.RegisterDragDrop(Hwnd, _dropTarget);
    }

    private Halo.Interop.FileDropTarget? _dropTarget;

    // pinned → visible in screenshots/recorders; unpinned → excluded. HALO_CAPTURABLE=1 forces visible (demos).
    public void SetCapturable(bool on)
    {
        if (Environment.GetEnvironmentVariable("HALO_CAPTURABLE") == "1") on = true;
        _capturable = on;
        Win32.SetWindowDisplayAffinity(Hwnd, on ? 0u : Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    // Whether the pill currently shows up in screen captures. It matters to the glass: the fast capture
    // path reads the SCREEN, and a pill that is not excluded would photograph itself — glass of glass of
    // glass. Excluded (the default) it is simply not in the pixels, which is what makes that path usable.
    private volatile bool _capturable;

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
        // A probe has to leave the z-order exactly as it found it. SW_SHOWNOACTIVATE re-inserts the window
        // at the BOTTOM of the topmost band, so entering a fullscreen video - which is a foreground change,
        // which is what triggers this probe - dropped the pill underneath the player and left it there until
        // the next once-a-second assert. Reported as the pinned pill vanishing in a fullscreen video.
        AssertTopmost();
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
        // + OffsetX, because the pill can be DRAGGED off centre and this grab could not. The window lands at
        // (workLeft + (workWidth - winW)/2 + OffsetX); the composite then reads the middle of this strip,
        // so the strip has to be centred on the pill and not on the screen. Without the offset the glass
        // showed a faithful picture of whatever sat at the centre of the display - a rectangle of unrelated
        // content floating inside the panel, which is exactly how it was reported. The pill's own width
        // cancels out of that algebra, which is why only the offset is needed here.
        int nx = _workLeft + (_workWidth - CaptureW) / 2 + (int)OffsetX, ny = _workTop;
        int sx = nx - wr.left, sy = ny - wr.top;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        string how;

        // Fast path: read the notch strip straight off the SCREEN DC. The window DC below returns black for
        // GPU-composited content — which is every browser and every video player — and the only rescue was
        // PrintWindow, which re-renders the WHOLE window. Over a maximised video that is tens of ms per
        // capture against a ~16ms request cadence, so the glass ran ~15fps and always showed a frame from
        // ~60ms ago; the faster the video moved, the more obviously stale it looked. The screen DC is what
        // DWM already composited, so it costs the same as the window BitBlt and has the video in it.
        //
        // Only safe while the pill is excluded from capture, which is the default: otherwise it reads its
        // own glass back in and feeds it forward, and each frame gets murkier than the last.
        // No IsMostlyBlack test on this one, deliberately. That check exists to catch a window-DC BitBlt that
        // FAILED — a GPU-composited window's DC holds nothing, so it comes back all zeros. The screen DC has
        // no such failure mode: it is the composited desktop, and black there means the backdrop really is
        // black. Screening it the same way sent every capture over a dark window down the 29ms PrintWindow
        // path for no reason (measured: 113 of 113 captures, against a dark editor), which is most of the lag
        // this was meant to remove — dark title bars and dark themes are exactly what sits under the notch.
        Bitmap? raw = _capturable ? null : GrabScreen(nx, ny);
        how = raw != null ? "screen" : "";

        if (raw == null)
        {
            raw = new Bitmap(CaptureW, CaptureH, PixelFormat.Format24bppRgb);
            IntPtr src = Win32.GetWindowDC(behind);
            using (var g = Graphics.FromImage(raw))
            {
                g.Clear(Color.FromArgb(24, 24, 24));
                IntPtr dhdc = g.GetHdc();
                Win32.BitBlt(dhdc, 0, 0, CaptureW, CaptureH, src, sx, sy, Win32.SRCCOPY);
                g.ReleaseHdc(dhdc);
            }
            Win32.ReleaseDC(behind, src);
            how = "window";

            // BitBlt returns black for GPU-composited windows (browsers/video) → fall back to PrintWindow
            if (IsMostlyBlack(raw))
            {
                var pw = CaptureViaPrintWindow(behind, wr, sx, sy);
                if (pw != null) { raw.Dispose(); raw = pw; how = "printwindow"; }
            }
        }

        var blurred = BlurPyramid(raw);

        // HALO_DUMP_GLASS=1 writes the raw grab and the blurred result next to the crash log, once every
        // two seconds. The pill refuses to be screenshotted, so when the glass shows something wrong this is
        // the only way to tell a bad CAPTURE from a bad composite - and they need completely different fixes.
        if (GlassDump)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastDump > 2000)
            {
                _lastDump = nowMs;
                try
                {
                    string dir = System.IO.Path.GetTempPath();
                    raw.Save(System.IO.Path.Combine(dir, "halo-glass-raw.png"), ImageFormat.Png);
                    blurred.Save(System.IO.Path.Combine(dir, "halo-glass-blur.png"), ImageFormat.Png);
                }
                catch { }
            }
        }

        raw.Dispose();

        // A capture that produced the SAME backdrop must not bump the version. NotchController.Frame
        // reads a version change as dirty and calls Apply(), which redraws the whole layered surface
        // supersampled — so over a window that is not moving this was 20-60 full redraws a second spent
        // putting back a picture identical to the one already on screen, and that was most of what the
        // pill cost while it just sat there. Only the redraw is skipped; the grab still happens on its
        // own cadence, so the glass still tracks a video the moment it actually changes.
        ulong hash = PlateHash(blurred);
        if (hash == _bgHash && _bg != null)
        {
            if (_staleStreak < 1000) _staleStreak++;
            blurred.Dispose();
            GlassTrace(how + " same", (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency);
            return;
        }
        _bgHash = hash;
        _staleStreak = 0;
        lock (_bgLock) { var old = _bg; _bg = blurred; old?.Dispose(); }
        System.Threading.Interlocked.Increment(ref _captureVersion);
        GlassTrace(how, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    // Coarse fingerprint of a blurred plate. Read through Marshal rather than copying the buffer: the
    // whole point is to be cheaper than the redraw it prevents, and ~1.1k sampled pixels answer "did
    // anything visible change" on an image that has already been through a 1/14 thumbnail. Written by
    // the capture thread only, which CaptureFrom serialises with _capturing.
    private ulong _bgHash;
    private int _staleStreak;

    // How many captures in a row came back byte-identical. The hash below stops an unchanged plate
    // forcing a redraw, but the grab itself is the larger cost — on the PrintWindow path it is ~30ms of
    // waiting on the other app — so the caller uses this to stop grabbing so often when nothing behind
    // the pill is moving. Any real change resets it, which is what keeps the glass live on a video.
    internal int StaleStreak => _staleStreak;

    private static ulong PlateHash(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
                              PixelFormat.Format32bppPArgb);
        try
        {
            ulong h = 14695981039346656037UL;                       // FNV-1a
            int stepX = Math.Max(1, b.Width / 48), stepY = Math.Max(1, b.Height / 24);
            for (int y = 0; y < b.Height; y += stepY)
                for (int x = 0; x < b.Width; x += stepX)
                {
                    int px = System.Runtime.InteropServices.Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4);
                    h = (h ^ (uint)px) * 1099511628211UL;
                }
            return h;
        }
        finally { b.UnlockBits(data); }
    }

    private static Bitmap? GrabScreen(int x, int y)
    {
        IntPtr screen = IntPtr.Zero;
        try
        {
            screen = Win32.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            var bmp = new Bitmap(CaptureW, CaptureH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(24, 24, 24));
                IntPtr dhdc = g.GetHdc();
                bool ok = Win32.BitBlt(dhdc, 0, 0, CaptureW, CaptureH, screen, x, y, Win32.SRCCOPY);
                g.ReleaseHdc(dhdc);
                if (!ok) { bmp.Dispose(); return null; }
            }
            return bmp;
        }
        catch { return null; }
        finally { if (screen != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, screen); }
    }

    // HALO_GLASS_DEBUG=1 → one line per capture with the path taken and its cost, so "is the glass keeping
    // up" is a number instead of an impression. Off by default; this runs on the capture thread.
    private static readonly bool GlassDebug =
        Environment.GetEnvironmentVariable("HALO_GLASS_DEBUG") == "1";
    private static int _traceCount;

    private static readonly bool GlassDump =
        Environment.GetEnvironmentVariable("HALO_DUMP_GLASS") == "1";
    private static long _lastDump;

    private static void GlassTrace(string how, double ms)
    {
        if (!GlassDebug) return;
        try
        {
            if (++_traceCount > 600) return;   // a few seconds' worth is plenty; never grow without bound
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "glass-debug.txt");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {how} {ms:0.0}ms\n");
        }
        catch { }
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
        MenuFrame menu, Action<Graphics, int, int, float> drawContent, Action<Graphics, int, int, float> drawCollapsed,
        float glassFade = 1f)
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
            DrawShape(g, w, h, radius, tintAlpha, glass, glassFade);
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

    // glassFade scales the captured-backdrop layer. It exists because that layer used to be drawn at FULL
    // opacity no matter what tintAlpha was: when the pill empties out, the tint fades to alpha 1 (invisible,
    // but still an OLE hit-target for a dragged file) while the glass kept painting a blurred picture of
    // whatever was behind it. The result was a small grey rectangle that appeared to "match the background"
    // because it literally was the background — left behind after the last app closed. Fading them together
    // makes the empty pill actually empty.
    internal void DrawShape(Graphics g, int w, int h, int radius, int tintAlpha, bool glass, float glassFade = 1f)
    {
        lock (_bgLock) ShapeInto(g, w, h, radius, tintAlpha, glass ? _bg : null, glassFade);
    }

    // Two supersampled buffers, reused. The composite runs every frame the pill is on screen, and it used to
    // allocate one of these per call; the single-mask fix needs a second, which at 1120x440 is the kind of
    // per-frame churn this render path is not allowed to have. Only ever touched from the frame timer, and
    // from the render hook, which is single-threaded - the lock is there so that stays true by construction
    // rather than by luck.
    private static readonly object _scratchLock = new();
    private static Bitmap? _scratchA, _scratchB;

    private static Bitmap Scratch(ref Bitmap? slot, int w, int h)
    {
        if (slot is { } b && b.Width == w && b.Height == h) return b;
        slot?.Dispose();
        slot = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        return slot;
    }

    private static Bitmap ScratchA(int w, int h) { lock (_scratchLock) return Scratch(ref _scratchA, w, h); }
    private static Bitmap ScratchB(int w, int h) { lock (_scratchLock) return Scratch(ref _scratchB, w, h); }

    // Blur alone does not make frosted glass. A blurred bright panel is still a bright panel, so a light
    // strip in the window behind - a message bar, a title bar - came through as a hard-edged pale block
    // sitting inside the pill, which is what "a white rectangle in the glass" turned out to be. Real frosted
    // glass carries hue and movement and throws away legible detail, so the backdrop is desaturated toward
    // its own luminance and its range squeezed into the lower half before the tint goes on. Bright content
    // still shows as a lift, just no longer as a shape with edges you can read.
    // Contrast went 0.58 -> 0.34 on a second look: 0.58 cut the offending band from a spread of 51.5 to
    // 2.7, which sounds finished until you remember the panel around it sits near 8, so 2.7 was still a
    // third of the local level and the eye reads relative differences in the dark, not absolute ones.
    private const float FrostDesat = 0.40f, FrostContrast = 0.34f, FrostFloor = 0.05f;

    private static ColorMatrix Frost(float alpha)
    {
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float d = FrostDesat, c = FrostContrast;
        return new ColorMatrix(new[]
        {
            new[] { ((1 - d) + lr * d) * c, lr * d * c,             lr * d * c,             0f, 0f },
            new[] { lg * d * c,             ((1 - d) + lg * d) * c, lg * d * c,             0f, 0f },
            new[] { lb * d * c,             lb * d * c,             ((1 - d) + lb * d) * c, 0f, 0f },
            new[] { 0f,                     0f,                     0f,                     alpha, 0f },
            new[] { FrostFloor,             FrostFloor,             FrostFloor,             0f, 1f },
        });
    }

    // The composite itself, with the backdrop passed in rather than read off the field, so `--render-shape`
    // can drive the REAL path with a known backdrop. The edge behaviour here has been wrong twice; it needs
    // to be inspectable without a screenshot of a window that refuses to be screenshotted.
    internal static void ShapeInto(Graphics g, int w, int h, int radius, int tintAlpha,
                                   Bitmap? backdrop, float glassFade)
    {
        const int ss = 2;

        // The backdrop and the tint used to be filled through the SAME path, one after the other, and that
        // is what produced the coloured frame. At a boundary pixel with partial coverage c, the tint's alpha
        // is scaled by c as well - so it covers the backdrop *less* exactly where the backdrop is already
        // there, and the rim comes out far more wallpaper than tint. Measured on a magenta backdrop: the
        // outer ring reached 130 in red and blue against an interior of 27.
        // Composite them on a FLAT rectangle first, so every pixel is already the finished glass colour, and
        // mask the result once. One antialiased edge, and it only scales alpha - it cannot change the hue.
        var content = ScratchA(w * ss, h * ss);
        using (var cg = Graphics.FromImage(content))
        {
            cg.CompositingMode = CompositingMode.SourceCopy;   // reused buffer: overwrite, never blend onto
            cg.Clear(Color.Transparent);
            cg.CompositingMode = CompositingMode.SourceOver;
            if (backdrop != null && glassFade > 0.004f)
            {
                int sx = (CaptureW - w) / 2;
                cg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(Frost(Math.Clamp(glassFade, 0f, 1f)));
                cg.DrawImage(backdrop, new Rectangle(0, 0, w * ss, h * ss),
                    sx, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            using var tint = new SolidBrush(Color.FromArgb(tintAlpha, 8, 8, 8));
            cg.FillRectangle(tint, 0, 0, w * ss, h * ss);

            // What was learned the expensive way: transparency alone is not glass. Both fixes for the
            // backdrop showing through as shapes - the tint, then FrostMix - work by REMOVING information,
            // and with nothing put back the pane stopped reading as a material at all ("شیشه‌ای بودنش از بین
            // رفت"). Frosted glass is a blurred backdrop PLUS a lit surface: a sheen down the face and a
            // grain in the substrate. Those two are backdrop-independent, so they cost none of the ghost
            // suppression back. The rim light is the third and has to follow the path, so it is below.
            if (Sheen > 0.004f)
            {
                using var lg = new LinearGradientBrush(new Rectangle(0, -1, w * ss, h * ss + 2),
                    Color.FromArgb((int)(255 * Sheen), 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical)
                { Blend = new Blend { Factors = new[] { 0f, 0.55f, 1f }, Positions = new[] { 0f, 0.30f, 1f } } };
                cg.FillRectangle(lg, 0, 0, w * ss, h * ss);
            }
            // real frosted glass is a scattering medium, and the grain is most of why it reads as one. It
            // also happens to break the banding the heavy blur leaves behind.
            if (Grain > 0.004f)
            {
                using var noise = new TextureBrush(GrainTile(), WrapMode.Tile);
                cg.FillRectangle(noise, 0, 0, w * ss, h * ss);
            }
        }

        var big = ScratchB(w * ss, h * ss);
        using (var bg = Graphics.FromImage(big))
        {
            bg.SmoothingMode = SmoothingMode.AntiAlias;
            bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bg.CompositingMode = CompositingMode.SourceCopy;
            bg.Clear(Color.Transparent);
            bg.CompositingMode = CompositingMode.SourceOver;
            using var path = PillPath(w * ss, h * ss, radius * ss);
            using var mask = new TextureBrush(content) { WrapMode = WrapMode.Clamp };
            bg.FillPath(mask, path);
            // the edge is where a pane of glass announces itself - it catches light along its contour and
            // that single hairline is the strongest "this is glass" cue there is. Drawn INSIDE the mask so
            // it is shaped by the same path and cannot become the coloured frame all over again; inset by
            // half the pen so it lands fully on covered pixels instead of riding the antialiased boundary.
            if (RimLight > 0.004f)
            {
                using var rim = new Pen(Color.FromArgb((int)(255 * RimLight), 255, 255, 255), ss)
                { Alignment = PenAlignment.Inset };
                bg.DrawPath(rim, path);
            }
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

            void Cell(string icon, Bitmap? img, float cx, float cy, float ia, Color? ring,
                float progress = -1f, float imageOffsetX = 0f)
            {
                if (ia <= 0.01f) return;
                if (img != null)
                {
                    // Faint icon-accent wash behind the icon. It used to be a flat SQUARE clipped to the
                    // strip path, which put a rounded-cornered rectangle around every circular icon —
                    // obvious behind a colourful album cover, where the accent is strong. A radial falloff
                    // has no corners and no edge to notice, and it needs no clip at all.
                    var accent = Widgets.Fx.AccentOf(img);
                    if (accent != Widgets.Fx.White)
                    {
                        using var wash = new System.Drawing.Drawing2D.GraphicsPath();
                        wash.AddEllipse(cx - D * 0.1f, cy - D * 0.1f, D * 1.2f, D * 1.2f);
                        using var pgb = new System.Drawing.Drawing2D.PathGradientBrush(wash)
                        {
                            CenterColor = Color.FromArgb((int)(34 * ia), accent),
                            SurroundColors = new[] { Color.FromArgb(0, accent) },
                        };
                        cg.FillPath(pgb, wash);
                    }
                    DrawCircleImage(cg, img, cx + imageOffsetX * ss, cy, D, ia);
                }
                else
                    DrawGlyphCentered(cg, icon, cx, cy, D, D * 0.45f, (int)(235 * ia));
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
                    Math.Clamp((hf - i * CircleD) / CircleD, 0f, 1f), menu.RowRings[i], menu.RowProgress[i],
                    menu.RowImageOffsets[i]);
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
        // ink-centred, like the swap cells right below: StringFormat centring sat this glyph high and left in
        // its blob, the same fault the fallback art glyph had
        DrawGlyphCentered(g, menu.DropIcon, blob.X - r2, blob.Y - r2, r2 * 2, r2 * 1.8f, (int)(235 * a));
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

    // Centre a glyph in a circle by its true INK bounds, not font metrics — font-metric centring includes
    // the glyph's side bearings + em padding, which shifted the media/video play mark off-centre in the
    // swap circle. Ink-bounds centring places the visible shape dead-centre.
    private static readonly FontFamily _cellGlyphFont = new("Segoe MDL2 Assets");
    private static void DrawGlyphCentered(Graphics g, string glyph, float x, float y, float box, float px, int alpha)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(glyph, _cellGlyphFont, (int)FontStyle.Regular, px, PointF.Empty, sf);
        path.Flatten();
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        using var m = new Matrix();
        m.Translate(MathF.Round(x + (box - b.Width) / 2f - b.X), MathF.Round(y + (box - b.Height) / 2f - b.Y));
        path.Transform(m);
        using var br = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(br, path);
        g.SmoothingMode = old;
    }

    // Draw a bitmap as a circular icon (cover-fit) inset in a CircleD-ish box, with alpha.
    // HQ-scale into a square then fill an ellipse with it as a texture, so edges are anti-aliased.
    private static void DrawCircleImage(Graphics g, Bitmap img, float x, float y, float box, float alpha)
    {
        img = CenteredSquare(img); // re-centre by true ink bounds so off-centre icons (VLC cone…) sit centred
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


    // Re-centre an icon by its true INK bounds onto a square canvas, so an icon whose art sits off-centre
    // inside its own bitmap (VLC's cone leans; some app icons carry asymmetric padding) renders visually
    // centred in the circle. Well-centred icons come back effectively unchanged (no regression). Cached by
    // source bitmap ref — the pixel scan runs once per icon, not per frame; icons are cached upstream.
    private static readonly System.Collections.Generic.Dictionary<Bitmap, Bitmap> _centered = new();
    private static Bitmap CenteredSquare(Bitmap src)
    {
        lock (_centered)
        {
            if (_centered.TryGetValue(src, out var c)) return c;
            var made = MakeCenteredSquare(src);
            _centered[src] = made;
            return made;
        }
    }

    private static Bitmap MakeCenteredSquare(Bitmap src)
    {
        try
        {
            var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * src.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int yy = 0; yy < src.Height; yy++)
                    for (int xx = 0; xx < src.Width; xx++)
                        if (buf[yy * stride + xx * 4 + 3] > 24) // alpha coverage
                        {
                            if (xx < minX) minX = xx; if (xx > maxX) maxX = xx;
                            if (yy < minY) minY = yy; if (yy > maxY) maxY = yy;
                        }
            }
            finally { src.UnlockBits(data); }
            if (maxX < minX) return src; // fully transparent → leave it

            // full-bleed image (a photo / video thumbnail — ink reaches an edge) → leave it; the caller's
            // centre-crop is correct and re-centring would only letterbox it. Only re-centre PADDED icons.
            int edge = Math.Max(1, Math.Min(src.Width, src.Height) / 64);
            if (minX <= edge && maxX >= src.Width - 1 - edge) return src;   // spans full width
            if (minY <= edge && maxY >= src.Height - 1 - edge) return src;  // spans full height

            // shift the mark so its ink centre sits at the bitmap centre — same scale, no crop, no letterbox.
            // (a padded icon whose mark leans off-centre becomes centred; a well-centred one shifts by ~0.)
            float dx = (src.Width - 1) / 2f - (minX + maxX) / 2f;
            float dy = (src.Height - 1) / 2f - (minY + maxY) / 2f;
            if (Math.Abs(dx) < 1.5f && Math.Abs(dy) < 1.5f) return src; // already centred → no re-blit

            var shifted = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(shifted))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, (int)Math.Round(dx), (int)Math.Round(dy), src.Width, src.Height);
            }
            return shifted;
        }
        catch { return src; }
    }

    // Two chained Blur() calls used to cost two full-size bicubic UPSCALES — and the upscale is nearly the
    // whole bill: writing 560x220 bicubic pixels dwarfs everything happening at thumbnail size. The look
    // came from the double smoothing, not from passing through full size in between, so the chain runs
    // entirely small and expands exactly once at the end. Same result, roughly half the time, which is what
    // buys the higher capture rate that actually removes the visible lag.
    // 1/8 left the edges of things behind still readable as edges - a pale bar behind the pill arrived as a
    // pale BAR. 1/14 is far enough down that a hard boundary comes back as a gradient, which is the other
    // half of what stops background content reading as a shape inside the glass (Frost is the first half).
    internal static Bitmap BlurPyramid(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        using var s1 = new Bitmap(Math.Max(1, w / 14), Math.Max(1, h / 14), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(s1))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, s1.Width, s1.Height), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
        }
        // the old second pass resampled through 1/5 scale; keeping that step at thumbnail size preserves the
        // extra smoothing (it is what takes the edge off the first upscale's ringing) for almost nothing
        using var s2 = new Bitmap(Math.Max(1, w / 5), Math.Max(1, h / 5), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(s2))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(s1, new Rectangle(0, 0, s2.Width, s2.Height), new Rectangle(0, 0, s1.Width, s1.Height), GraphicsUnit.Pixel);
        }
        var big = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(big))
        {
            // Blur shrinks what is behind, it does not stop it being a map of what is behind. A 90px block
            // against the pill's left end is still 6px in the 1/14 thumbnail, and the upscale hands it back
            // as a flat coloured slab - so the ends of the pill came out a different colour from the middle
            // ("رنگ لبه‌ها فرق می‌کند"), which no amount of extra blur fixes because past ~1/14 the upscale
            // rings instead. Frosted glass takes on the AVERAGE of its backdrop and keeps only a soft drift
            // of it, so the plate is pulled toward its own mean. Hue and movement survive - the pane still
            // shifts as the wallpaper does - but no region of it reads as a shape any more.
            if (FrostMix > 0.004f)
            {
                using var wash = new SolidBrush(Mean(s1));   // s1, not s2: fewer pixels, same average
                g.FillRectangle(wash, 0, 0, w, h);
            }
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var ia = new ImageAttributes();
            var m = new ColorMatrix { Matrix33 = 1f - FrostMix };
            ia.SetColorMatrix(m);
            g.DrawImage(s2, new Rectangle(0, 0, w, h),
                0, 0, s2.Width, s2.Height, GraphicsUnit.Pixel, ia);
        }
        return big;
    }

    // how far the blurred plate is pulled toward its own average colour. 0 = the old behaviour (a blurred
    // picture of the desktop), 1 = a single flat wash that only changes as the average behind it changes.
    internal static float FrostMix = 0.55f;

    // Sheen down the face, grain in the substrate, rim light along the contour - the three cues that make a
    // pane read as a material rather than a colour, all backdrop-independent so they cost none of the ghost
    // suppression back. Built, shipped at 0.09/0.06/0.17, looked at, and rejected on sight: off is the
    // shipped state. The code stays because the reasoning still holds and `--render-shape mix,sheen,grain,
    // rim` sweeps all four without a rebuild - at 0 every one of them is a branch that does not run.
    internal static float Sheen = 0f, Grain = 0f, RimLight = 0f;

    private static Bitmap? _grain;

    // one tile, generated once. Deterministic seed: a grain that reshuffles per frame is a crawling fizz on
    // a window that sits still, which is worse than no grain at all.
    private static Bitmap GrainTile()
    {
        if (_grain is { } g0 && Math.Abs(_grainFor - Grain) < 0.0005f) return g0;
        _grain?.Dispose();
        const int n = 128;
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rnd = new Random(20260728);
            int peak = (int)Math.Clamp(Grain * 255f, 0f, 255f);
            unsafe
            {
                for (int y = 0; y < n; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < n; x++)
                    {
                        // white, premultiplied: at alpha a the stored channels ARE a. A non-premultiplied
                        // texture here is the documented way to spray white garbage onto a layered surface.
                        byte a = (byte)rnd.Next(peak + 1);
                        row[x * 4] = a; row[x * 4 + 1] = a; row[x * 4 + 2] = a; row[x * 4 + 3] = a;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        _grainFor = Grain;
        return _grain = bmp;
    }

    private static float _grainFor = -1f;

    // the mean of a thumbnail, done on the bits: a DrawImage down to 1x1 is a resample, not an average,
    // and GDI+ does not promise box prefiltering at that ratio
    private static Color Mean(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
                              PixelFormat.Format32bppPArgb);
        try
        {
            long r = 0, g = 0, bl = 0;
            int n = b.Width * b.Height;
            unsafe
            {
                for (int y = 0; y < b.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < b.Width; x++)
                    {
                        bl += row[x * 4]; g += row[x * 4 + 1]; r += row[x * 4 + 2];
                    }
                }
            }
            return Color.FromArgb(255, (int)(r / n), (int)(g / n), (int)(bl / n));
        }
        finally { b.UnlockBits(data); }
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

    // A layered popup tells Windows nothing about which parts of itself are pressable — the class cursor
    // covers the whole window — so the arrow sat over the stop button exactly as it sat over dead space.
    // The controller owns the hit-testing, so it hands over a screen-space predicate and this asks it.
    public Func<Point, bool>? WantsHandCursor;
    private static IntPtr _handCursor;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // WM_SETCURSOR fires before the class cursor is restored, and returning TRUE is what stops the
        // default from overwriting the choice. Anything set outside this message lasts one mouse move.
        if (msg == Win32.WM_SETCURSOR && WantsHandCursor is { } wantsHand)
        {
            try
            {
                if (Win32.GetCursorPos(out var cp) && wantsHand(new Point(cp.X, cp.Y)))
                {
                    if (_handCursor == IntPtr.Zero) _handCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_HAND);
                    Win32.SetCursor(_handCursor);
                    return new IntPtr(1);
                }
            }
            catch { }
        }
        // Windows broadcasts this to top-level windows when the clock or the timezone moves. Without it
        // nothing in the process ever learns: .NET's cached local zone outlives the change.
        if (msg == Win32.WM_TIMECHANGE)
        {
            try { Almanac.TimeZoneChanged(); } catch { }
            return IntPtr.Zero;
        }
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
