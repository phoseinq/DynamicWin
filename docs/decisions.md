# Decisions

## Locked
- **Stack:** C# + .NET 9 + **Composition API** for shell animation and glass.
  Chosen because compositor-thread animation runs at the monitor's real refresh rate and real
  backdrop blur fixes the "not smooth / low-res LED look / not glassy" complaints about DynamicWin.
- **Shell compositor = system `Windows.UI.Composition` on a Win32 layered tool-window** (via
  `CreateDesktopWindowTarget` + `CreateHostBackdropBrush`), NOT a WinUI 3 XAML `Window`. Reason: a
  XAML Window can't do clean per-pixel transparency around the pill; the layered-window + system
  Composition path is the proven way to get a transparent frosted-glass overlay. WinUI 3 XAML is
  used later only as **XAML Islands** for rich widget content (bars/labels/buttons), if needed.
  Toolchain (WinUI build) already verified working on this machine anyway — see 09.
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

## Glass mechanism (resolved P1, verified 2026-07-13)
- `CreateHostBackdropBrush()` returns **opaque black** on our layered NOREDIRECTIONBITMAP window
  (host backdrop only works for UWP/CoreWindow). Not usable.
- **Real frosted blur = `SetWindowCompositionAttribute` with `ACCENT_ENABLE_ACRYLICBLURBEHIND`**,
  and the window shaped to the pill via `SetWindowRgn(CreateRoundRectRgn(...))`. Verified: the
  desktop behind the pill is genuinely blurred/frosted, and Composition (tint + highlight + content)
  draws on top. User chose this (real acrylic) over smoked-translucent.
- Consequence: the window is shaped to the pill, so expand/collapse animates the **window bounds
  + region** (per-frame `SetWindowPos`/`SetWindowRgn`) alongside the Composition content. Glass
  darkness = Composition tint sprite opacity bound to expand progress (collapsed≈0.9 near-black,
  expanded≈0.2 glassy); acrylic's own GradientColor tint stays fixed and moderate.
- `ponytail: per-frame window-region/pos is the smoothness risk. Validate the spring feel in Task 6;
  if SetWindowRgn flickers, animate window bounds via SetWindowPos with a fixed full-window rounded
  region instead. Upgrade to a two-window (acrylic backing + composition overlay) split only if a
  single window can't stay smooth.`

## Open / to refine
- **Account usage-limit %** (5h / weekly) has no clean public API. It's the one *best-effort* data
  source — estimated from transcript/cost. Context % is solid; ship that first, refine limit later.
- Multi-monitor follow behavior (stay on primary vs. follow mouse) — default primary, decide in P5.
- Third-party widget DLL hot-load — deferred until someone actually ships one (P2 ships compiled-in).

## Working name
**Halo** — rename freely; it's only in namespaces/paths so far.
