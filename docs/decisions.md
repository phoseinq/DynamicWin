# Decisions

## Locked
- **Stack:** C# + .NET 9, Win32 interop, GDI+ (`System.Drawing.Common`) for rendering.
- **Shell = Win32 layered window rendered with `UpdateLayeredWindow` + GDI+** (per-pixel-alpha
  bitmap), NOT the compositor. **PIVOTED away from `Windows.UI.Composition` at P2** — reason: the
  system compositor can't host a bitmap surface without `LoadedImageSurface` (missing from the
  desktop projection) or heavy D2D/DXGI interop, and we already drive animation from a UI-thread
  timer (`SetWindowPos`/redraw), so composition bought us nothing. GDI+ gives crisp anti-aliased
  corners + easy high-quality text/icons in one pass. Composition files were deleted (see git
  history if you need them). Docs 02/04/05/09 predate the pivot — this bullet supersedes their
  "Composition" details; the glass/animation/shape ideas still hold, only the renderer changed.
- **Shape via GDI+ path** (square top corners flush to screen, rounded bottom), not `SetWindowRgn`.
  Cleaner AA corners.
- **Content/icons = GDI+**: text in Segoe UI, icons as **Segoe Fluent Icons** glyphs (built-in
  vector font, crisp at any DPI) — the user asked specifically for high-quality icons.
- **Timer:** `DispatcherQueueTimer` (~8ms) created via `CreateDispatcherQueueController`
  (`Interop/Dispatcher.cs`); drives hover polling + animation. Hover = `GetCursorPos` polling
  against collapsed/expanded rects (robust to the resize churn that broke window mouse messages).
- **Claude Code bar scope:** *Everything* — account usage limit (5h + weekly), session context,
  live activity, and Cancel.
- **Cancel = real stop.** The button interrupts the running Claude Code prompt, not just closes the
  panel. v1 = spawn a helper that does `AttachConsole` + `GenerateConsoleCtrlEvent(CTRL_C_EVENT)` (no
  focus steal); fallback = focus + Esc if Ctrl+C exits CC instead of interrupting. See 05/06.
- **UI language:** English everywhere in the app. (Chat/docs may be Persian.)
- **No comments in shipped source.** Write with none from the start; a strip pass runs before any
  GitHub push. `ponytail:` markers are the only comments allowed during dev and get stripped too.
- **Default collapsed-pill bar:** session **Context %** (always accurate). The 5h-limit view is
  configurable. Reason: context is a reliable number; account-limit % is best-effort (see below).

## Glass mechanism (verified 2026-07-15, post-pivot)
- **Real frosted blur = `SetWindowCompositionAttribute` with `ACCENT_ENABLE_ACRYLICBLURBEHIND`**
  on the layered window. Verified it blurs the desktop **through the `UpdateLayeredWindow`
  per-pixel-alpha bitmap**: where the GDI+ bitmap is a low-alpha dark tint, the frosted desktop
  shows; where it's opaque (text/icons), it's solid. (`CreateHostBackdropBrush` was a dead end —
  returns black on this window type.)
- Glass darkness = the GDI+ **tint alpha** lerped with expand progress: collapsed ≈ 235 (near-black
  Apple pill) → expanded ≈ 60 (glassy). `EnableAcrylic` gradient color = 0 (no dark halo).
- Expand/collapse animates by re-rendering the bitmap at the lerped size + `UpdateLayeredWindow`
  each frame (window position via ULW's `pptDst`). `EaseOutBack` for spring overshoot.
- `ponytail: per-frame GDI+ redraw + ULW is cheap for this small bitmap; if it ever stutters, cache
  static states and only redraw during the ~0.28s transition.`

## Open / to refine
- **Account usage-limit %** (5h / weekly) has no clean public API. It's the one *best-effort* data
  source — estimated from transcript/cost. Context % is solid; ship that first, refine limit later.
- Multi-monitor follow behavior (stay on primary vs. follow mouse) — default primary, decide in P5.
- Third-party widget DLL hot-load — deferred until someone actually ships one (P2 ships compiled-in).

## Working name
**Halo** — rename freely; it's only in namespaces/paths so far.
