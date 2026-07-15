using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Halo.Interop;

namespace Halo.Shell;

internal sealed class LayeredNotch
{
    private static readonly string PlayGlyph = char.ConvertFromUtf32(0xE768);

    private Win32.WndProc _wndProc = null!;
    private int _workLeft, _workTop, _workWidth;

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
        Win32.EnableAcrylic(Hwnd, 0x00000000);
    }

    public void Render(int w, int h, int radius, int tintAlpha, float contentFade)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using (var path = PillPath(w, h, radius))
            using (var tint = new SolidBrush(Color.FromArgb(tintAlpha, 10, 10, 10)))
                g.FillPath(tint, path);

            using (var hl = new Pen(Color.FromArgb(32, 255, 255, 255), 1f))
                g.DrawLine(hl, radius, 1, w - radius, 1);

            if (contentFade > 0.01f)
                DrawContent(g, w, h, contentFade);
        }
        Blit(bmp, w, h);
    }

    private static void DrawContent(Graphics g, int w, int h, float fade)
    {
        int a = (int)(255 * fade);
        using var iconFont = new Font("Segoe Fluent Icons", 34f, GraphicsUnit.Pixel);
        using var titleFont = new Font("Segoe UI Semibold", 30f, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 18f, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
        using var dim = new SolidBrush(Color.FromArgb((int)(a * 0.7f), 255, 255, 255));

        float cx = w / 2f, cy = h / 2f;
        g.DrawString(PlayGlyph, iconFont, white, cx - 150, cy - 22);
        g.DrawString("Halo", titleFont, white, cx - 100, cy - 28);
        g.DrawString(DateTime.Now.ToString("HH:mm"), subFont, dim, cx - 98, cy + 8);
    }

    private void Blit(Bitmap bmp, int w, int h)
    {
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr old = Win32.SelectObject(memDc, hBitmap);

        var size = new Win32.SIZE { cx = w, cy = h };
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

        Win32.SelectObject(memDc, old);
        Win32.DeleteObject(hBitmap);
        Win32.DeleteDC(memDc);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
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
