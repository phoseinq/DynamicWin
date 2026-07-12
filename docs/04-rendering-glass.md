# 04 — Rendering & glass (the visual heart)

This is where the "smooth like Apple / clean glass" bar is set. Get P1 perfect here.

## Why Composition
- Composition animations run on the **compositor thread** at the monitor's refresh rate,
  independent of the UI thread → no stutter, true 120/144Hz.
- Gives us `SpringVector3NaturalMotionAnimation`, implicit animations, `ExpressionAnimation`,
  and backdrop brushes — the exact toolkit Apple-style motion needs.

## Shape & motion
- The pill/panel is a `SpriteVisual` with a rounded-rect clip (`CompositionRoundedRectangleGeometry`
  via a shape visual) or a rounded-rect mask. Corner radius animates with size.
- Expand/collapse = **spring** on `Size` (and radius, and glass params below), so it overshoots and
  settles like Dynamic Island. Tune damping/period once, reuse everywhere via a `Springs` helper.
- Content inside cross-fades; don't hard-swap. Slight scale-in on expanded content.

## Glass
- Backdrop = `CompositionBackdropBrush` behind the surface, run through a Gaussian blur graph
  (`Microsoft.Graphics.Canvas` effects: `GaussianBlurEffect` → `Tint`/`Blend`). Tint is a dark
  color at low opacity.
- **Interpolate with size:** collapsed → blur radius ≈ 0, tint opacity high (near-opaque black pill).
  Expanded → blur radius high, tint opacity low (very glassy). Drive both from the same 0→1 expand
  progress used by the size spring, so glass "thickens" as it opens.
- Prefer building our own blur graph over `DesktopAcrylicController` because we need blur/tint tied
  to expand progress, not a fixed system material. `ponytail: if the custom graph is fiddly, fall
  back to DesktopAcrylicController for expanded state and just fade a solid black for collapsed.`

## Killing banding / the "low-res" look
- Add a faint **grain/noise** layer over the glass (a small tiled noise texture at very low opacity).
  Dithers the smooth blur gradient so no LED-like banding.
- Render at full DPI; never upscale a low-res surface. Anti-alias the rounded geometry.
- A thin 1px inner highlight stroke on the top edge sells the glass and hides the corner seam.

## One thing to verify
Spring settle + glass interpolation must hold at 60, 120, and 144Hz without changing feel — the
whole point of the stack. Test on the real 144Hz panel.
