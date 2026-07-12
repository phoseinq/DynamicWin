using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace Halo.Rendering;

internal sealed class GlassPill
{
    private readonly SpriteVisual _pill;
    private readonly SpriteVisual _tint;
    private readonly SpriteVisual _highlight;
    private readonly CompositionRoundedRectangleGeometry _geometry;

    public Visual Visual => _pill;

    public GlassPill(Compositor compositor)
    {
        _pill = compositor.CreateSpriteVisual();
        _pill.AnchorPoint = new Vector2(0.5f, 0f);
        _pill.RelativeOffsetAdjustment = new Vector3(0.5f, 0f, 0f);

        _geometry = compositor.CreateRoundedRectangleGeometry();
        _pill.Clip = compositor.CreateGeometricClip(_geometry);

        _tint = compositor.CreateSpriteVisual();
        _tint.Brush = compositor.CreateColorBrush(Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A));
        _tint.RelativeSizeAdjustment = Vector2.One;
        _tint.Opacity = 0.85f;
        _pill.Children.InsertAtTop(_tint);

        _highlight = compositor.CreateSpriteVisual();
        _highlight.Brush = compositor.CreateColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        _highlight.Size = new Vector2(0f, 1f);
        _highlight.RelativeSizeAdjustment = new Vector2(1f, 0f);
        _pill.Children.InsertAtTop(_highlight);
    }

    public void SetSize(float w, float h)
    {
        _pill.Size = new Vector2(w, h);
        _geometry.Size = new Vector2(w, h);
    }

    public void SetCornerRadius(float r)
    {
        _geometry.CornerRadius = new Vector2(r, r);
    }

    public void SetGlass(float tintOpacity)
    {
        _tint.Opacity = tintOpacity;
    }
}
