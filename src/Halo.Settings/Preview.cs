using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Halo.Settings;

// `Halo.Settings --render-page <out.png> [home|general|features|agents|access|docs]`, the same kind of
// dev hook Halo.App already carries for every widget it cannot screenshot.
//
// It exists because the alternative was driving the real cursor: move the pointer onto a nav row, click
// it, park the pointer somewhere harmless, then grab the screen. That works and it is horrible - it
// fights the person at the keyboard for their own mouse, and it only ever captures whatever the window
// manager felt like putting on top. This renders the visual tree straight to a bitmap: no window shown,
// no pointer moved, no focus stolen, and the same picture every time.
//
// The window is never Show()n, so the acrylic behind it does not exist and the translucent surfaces would
// composite onto nothing. A stand-in backdrop goes down first, in the same spirit as the colourful
// backdrop `--render-notif` paints - glass has to be judged against something.
internal static class Preview
{
    internal static void Render(string outPath, string page, string mode = "")
    {
        const int W = 840, H = 640;

        var window = new MainWindow();
        window.Preview(Parse(page), mode);

        // Measure/Arrange by hand rather than showing the window: layout has to have run before a render,
        // and this is the only way to get it without putting a window on the user's screen.
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        root.UpdateLayout();

        // "bottom" is how anything below the fold gets looked at: the detail pane is a ScrollViewer and a
        // render of the visual tree shows only what is in view, so a long page's last rows were
        // unreviewable. Layout has to run once before the scroll and once after it.
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

    // Not flat grey: a flat backdrop hides exactly the failure these tints are tuned against, which is a
    // translucent surface disappearing over a busy or bright wallpaper.
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
