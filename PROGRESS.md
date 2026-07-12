# Halo — progress

Goal: smooth glass notch for Windows (Dynamic Island for desktop). C# + .NET 9 + system
`Windows.UI.Composition` on a Win32 layered window. Spec in `docs/` (start at `docs/MAP.md`).

## Done
- P0 skeleton: layered topmost transparent tool-window at top-center of primary monitor. Runs.
- P0 geometry: `NotchGeometry` (2 xUnit tests pass), window placement.
- P0 composition: system Compositor + `DesktopWindowTarget` (manual QI for `ICompositorDesktopInterop`);
  static rounded pill; **transparency proven** (only the pill paints, surround see-through).
- P1 glass — mechanism resolved & verified:
  - `CreateHostBackdropBrush` = black on this window type (dead end, recorded).
  - Translucent tint over transparency = works (smoked look).
  - **Real frosted acrylic = `ACCENT_ENABLE_ACRYLICBLURBEHIND` + rounded `SetWindowRgn`** — verified
    against a colorful backdrop (desktop genuinely blurred inside the pill). User chose this path.

## Next (remaining P1)
- Refactor to **window = pill** model: window sized to current pill rect, rounded region, acrylic on.
- Task 5: state machine (Idle/Peek/Expanded) + hover hit-test (WndProc WM_MOUSEMOVE / WM_MOUSELEAVE).
- Task 6: animated expand/collapse (window bounds + region + composition tint per frame). Tune the
  spring feel live on the 144Hz panel; watch for SetWindowRgn flicker (fallback in decisions.md).
- Task 7: grain (LoadedImageSurface noise) to kill banding; top highlight already in.

Then P2 widgets → P3/P4 Claude Code → P5 more widgets → P6 ship. See `docs/07-build-phases.md`.

## Verify recipe
Run app in background, drop a colorful WinForms backdrop behind it, `CopyFromScreen` to PNG, view.
Crash log: `%TEMP%\halo-crash.log` (try/catch in `Program.Main`, dev-only).
