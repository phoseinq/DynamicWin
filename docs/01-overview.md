# 01 — Overview

## What Halo is
A top-center screen notch for Windows that behaves like Apple's Dynamic Island: a small dark
pill when idle, a glassy panel when it expands. It hosts pluggable widgets. The headline widget
is a Claude Code panel.

## The three problems we fix (vs. DynamicWin)
1. **Not smooth / no refresh-rate sync.** DynamicWin (WPF) animates on the UI thread and stutters.
   → We animate with the **Composition API** on the compositor thread, at the monitor's real Hz.
2. **Looks low-res / "LEDs visible."** Its blur is low quality with visible banding.
   → Real Gaussian backdrop blur + a subtle grain layer to kill banding, rendered full-DPI,
   anti-aliased rounded corners.
3. **Not transparent/glassy enough.** → Collapsed = near-opaque black like Apple's pill.
   Expanded = high blur + low tint = very glassy. Blur/tint interpolate *with* the size, so the
   wider it gets, the glassier it looks.

## Scope (v1)
- Notch shell: idle pill → peek → expanded panel, spring animated.
- Widget system: `IWidget` contract + host; adding an app = adding a widget.
- Widgets: **Claude Code** (usage bars, live activity, real Cancel), plus Now Playing, Volume,
  Battery.
- Config file, autostart, single-instance.

## Non-goals (v1)
- Third-party widget marketplace / DLL hot-load (interface is ready; loader deferred).
- Cross-platform. Windows only.
- Deep theming UI. One good dark glass look, few knobs.

## Success criteria
- Expand/collapse is visibly as smooth as Dynamic Island on a 120/144Hz panel.
- Glass reads as clean frosted glass, no banding, at any DPI.
- A new widget can be added by implementing one interface, no shell changes.
- Claude Code bar reflects real activity within ~1s; Cancel actually stops the run.
