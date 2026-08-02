using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Halo.Settings;

internal static class Preview
{
    internal static void Render(string outPath, string page, string mode = "")
    {
        const int W = 840, H = 640;

        var window = new MainWindow();
        window.Preview(Parse(page), mode);

        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        root.UpdateLayout();

        if (mode == "bottom")
        {
            window.PreviewScrollToEnd();
            root.UpdateLayout();
        }

        var content = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        content.Render(root);

        var composite = new DrawingVisual();
        using (var dc = composite.RenderOpen())
        {
            dc.DrawRectangle(Backdrop(), null, new Rect(0, 0, W, H));
            dc.DrawImage(content, new Rect(0, 0, W, H));
        }
        var final = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        final.Render(composite);

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(final));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var file = File.Create(outPath);
        png.Save(file);
    }

    private static Brush Backdrop() => new LinearGradientBrush(
        [
            new GradientStop(Color.FromRgb(0x2A, 0x1B, 0x3D), 0),
            new GradientStop(Color.FromRgb(0x12, 0x2A, 0x4A), 0.45),
            new GradientStop(Color.FromRgb(0x5A, 0x1F, 0x3A), 1),
        ], new Point(0, 0), new Point(1, 1));

    private static PageId Parse(string page) => page.ToLowerInvariant() switch
    {
        "general" => PageId.General,
        "features" => PageId.Features,
        "agents" => PageId.Agents,
        "api" => PageId.Api,
        "access" => PageId.Access,
        "docs" or "about" => PageId.DocsAbout,
        _ => PageId.Home,
    };
}
