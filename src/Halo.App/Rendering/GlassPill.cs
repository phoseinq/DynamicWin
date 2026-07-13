using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace Halo.Rendering;

internal sealed class GlassPill
{
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _tint;

    public Visual Visual => _root;

    public GlassPill(Compositor compositor)
    {
        _root = compositor.CreateContainerVisual();
        _root.RelativeSizeAdjustment = Vector2.One;

        _tint = compositor.CreateSpriteVisual();
        _tint.Brush = compositor.CreateColorBrush(Color.FromArgb(0xFF, 0x08, 0x08, 0x08));
        _tint.RelativeSizeAdjustment = Vector2.One;
        _tint.Opacity = 0.9f;
        _root.Children.InsertAtTop(_tint);

        var highlight = compositor.CreateSpriteVisual();
        highlight.Brush = compositor.CreateColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        highlight.Size = new Vector2(0f, 1f);
        highlight.RelativeSizeAdjustment = new Vector2(1f, 0f);
        _root.Children.InsertAtTop(highlight);
    }

    public void SetGlass(float tintOpacity)
    {
        _tint.Opacity = tintOpacity;
    }
}
