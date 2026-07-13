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

- P1 DONE: **window = pill** model. Hover (GetCursorPos polling, robust to resize churn) → spring
  expand/collapse via `SetWindowPos` + rounded region each frame (DispatcherQueueTimer 8ms).
  `SetWindowRgn`-shrink ghost fixed by animating window bounds instead. Tint 0.9→0.2 with progress.
  **Top corners square + flush to screen top, bottom corners rounded** (CombineRgn RGN_OR); acrylic
  gradient set to 0 (no dark halo). Both states verified by screenshot over a colorful backdrop.

## Next
- Live-tune spring feel on the 144Hz panel (EaseOutBack c1, DurationSeconds in NotchController).
- Task 7 grain (LoadedImageSurface noise) — deferred; acrylic frosting already looks clean, add only
  if banding shows.

Then P2 widgets → P3/P4 Claude Code → P5 more widgets → P6 ship. See `docs/07-build-phases.md`.

## Verify recipe
Run app in background, drop a colorful WinForms backdrop behind it, `CopyFromScreen` to PNG, view.
Crash log: `%TEMP%\halo-crash.log` (try/catch in `Program.Main`, dev-only).
