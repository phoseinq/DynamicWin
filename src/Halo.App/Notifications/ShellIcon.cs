using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Halo.Notifications;

internal static class ShellIcon
{

    private static readonly System.Collections.Generic.Dictionary<string, Bitmap> _ok = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Generic.Dictionary<string, long> _missed = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? ForAumid(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        lock (_ok)
        {
            if (_ok.TryGetValue(aumid, out var c)) return c;
            if (_missed.TryGetValue(aumid, out var t) && Environment.TickCount64 - t < 5000) return null;
        }
        var bmp = Extract(aumid);
        lock (_ok)
        {
            if (bmp != null) { _ok[aumid] = bmp; _missed.Remove(aumid); }
            else _missed[aumid] = Environment.TickCount64;
        }
        return bmp;
    }

    private static Bitmap? Extract(string aumid)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, IntPtr.Zero, ref iid, out var f) != 0 || f == null)
                return null;

            f.GetImage(new SIZE { cx = 256, cy = 256 }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp);
            Marshal.ReleaseComObject(f);
            if (hbmp == IntPtr.Zero) return null;

            var bm = new BITMAP();
            if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmBits == IntPtr.Zero)
                { using var fb = Image.FromHbitmap(hbmp); return new Bitmap(fb); }
            using var src = new Bitmap(bm.bmWidth, bm.bmHeight, bm.bmWidthBytes,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bm.bmBits);
            return Trim(new Bitmap(src));
        }
        catch { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }

    private static Bitmap Trim(Bitmap b)
    {
        int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y).A > 90)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < minX) return b;
        int w = maxX - minX + 1, h = maxY - minY + 1;
        if (w >= b.Width - 1 && h >= b.Height - 1) return b;
        var crop = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(b, new Rectangle(0, 0, w, h), new Rectangle(minX, minY, w, h), GraphicsUnit.Pixel);
        b.Dispose();
        return crop;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }
}
