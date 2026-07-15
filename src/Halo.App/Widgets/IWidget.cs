using System.Drawing;

namespace Halo.Widgets;

internal interface IWidget
{
    void DrawContent(Graphics g, int w, int h, float expandFade);

    RectangleF ExpandedButton(int w, int h);

    void ActivateButton();
}
