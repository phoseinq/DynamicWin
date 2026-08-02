using System.Drawing;

namespace Halo.Widgets;

// The self-scrolling title line, extracted from MediaWidget the day VLC's panel was reported as "still
// doesn't scroll" - the marquee only existed on the SMTC widget and classic VLC never goes through it.
// Contract (argued over twice, do not re-litigate): an overflowing title on an OPEN panel scrolls
// unconditionally - "a film name you cannot read without reaching for the mouse is not a title at all" -
// resting a long beat between laps so it stays glanceable; hover shortens the rest to "show me now".
// A fitting title parks with an ellipsis and resets, so the next overflow starts from the beginning.
internal sealed class Marquee
{
    internal const float Gap = 48f, Speed = 42f, Hold = 0.35f;   // px, px/s, seconds
    internal const float Rest = 1.6f;                            // unattended pause between laps

    private float _offset;      // scroll offset in px, 0 while parked
    private float _hold;        // seconds paused at the start of each pass

    // set while the title is actually travelling, so a PAUSED session with a long name still gets
    // frames - otherwise the scroll would only move on whatever else happened to trigger a repaint.
    // written on the render thread, read by Animating from wherever the controller asks: volatile.
    private volatile bool _scrolling;
    public bool Scrolling => _scrolling;

    // the marquee lives on the open panel; a panel closed mid-scroll must not leave the flag latched,
    // or a paused long-titled session keeps requesting frames with nothing moving. call from DrawCollapsed.
    public void Park() => _scrolling = false;

    // One step of the scroll, kept pure so the motion is a test rather than an eyeball: it holds still
    // for holdFor at the start of each pass (so you can begin reading), then travels at a fixed px/sec -
    // a rate, not a per-frame amount, or the speed would change with the pill's fps tier - and wraps by
    // exactly one span so the second copy lands seamlessly where the first left.
    internal static (float offset, float hold) Step(float offset, float hold, float dt, float span,
        float holdFor = Hold)
    {
        if (span <= 0f) return (0f, 0f);
        if (hold < holdFor) return (offset, hold + dt);
        offset += Speed * dt;
        return offset >= span ? (offset - span, 0f) : (offset, hold);
    }

    public void Draw(Graphics g, string text, Font f, Brush b, float x, float y, float w,
        bool hovered, float dt)
    {
        float textW = g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic).Width;
        if (textW <= w)
        {
            // parked: reset so the next pass starts from the beginning rather than mid-word
            _offset = 0f; _hold = 0f;
            _scrolling = false;
            using var pf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            if (Fx.IsRtl(text)) pf.FormatFlags |= StringFormatFlags.DirectionRightToLeft; // Near => right edge, ellipsis on the left
            g.DrawString(text, f, b, new RectangleF(x, y, w, f.Height + 4), pf);
            return;
        }
        _scrolling = true;   // keep asking for frames even if the session is paused

        float span = textW + Gap;
        (_offset, _hold) = Step(_offset, _hold, dt, span, hovered ? Hold : Rest);

        var state = g.Save();
        g.SetClip(new RectangleF(x, y, w, f.Height + 4));   // hard clip is fine: the edges are axis-aligned
        bool rtl = Fx.IsRtl(text);
        using var sf = new StringFormat(StringFormatFlags.NoWrap);
        if (rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        float h2 = f.Height + 4;
        for (int pass = 0; pass < 2; pass++)                 // second copy trails in so the loop is seamless
        {
            // LTR slides left off its start; RTL is the mirror, anchored on the rect's right edge
            float ox = rtl ? x + w - textW + (_offset - pass * span)
                           : x - (_offset - pass * span);
            g.DrawString(text, f, b, new RectangleF(ox, y, textW + 2, h2), sf);
        }
        g.Restore(state);
    }
}
