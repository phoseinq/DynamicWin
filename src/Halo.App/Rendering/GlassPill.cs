using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace Halo.Rendering;

internal sealed class GlassPill
{
    private readonly SpriteVisual _pill;
    private readonly CompositionRoundedRectangleGeometry _geometry;

    public Visual Visual => _pill;

    public GlassPill(Compositor compositor)
    {
        _pill = compositor.CreateSpriteVisual();
        _pill.Brush = compositor.CreateColorBrush(Color.FromArgb(0xD9, 0x0A, 0x0A, 0x0A));
        _pill.AnchorPoint = new Vector2(0.5f, 0f);
        _pill.RelativeOffsetAdjustment = new Vector3(0.5f, 0f, 0f);

        _geometry = compositor.CreateRoundedRectangleGeometry();
        _pill.Clip = compositor.CreateGeometricClip(_geometry);
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
}
