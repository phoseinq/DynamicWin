# 07 — Build phases

Order matters: the visual core (P0–P1) must feel right before anything is layered on.
Each phase has a "done when" you can actually observe.

| Phase | Goal | Done when |
|-------|------|-----------|
| **P0** | Skeleton | Transparent, topmost, borderless window sits top-center of the primary monitor showing a static black pill. Survives DPI changes. |
| **P1** | Composition shell | Hover → spring expand/collapse; corner radius animates; glass thickens on expand, near-opaque black when collapsed; grain kills banding. Feels as smooth as Dynamic Island at 60/120/144Hz. **This is the make-or-break phase.** |
| **P2** | Widget system | `IWidget` + `WidgetHost`; a trivial **Clock** widget shows in the pill and expands. Adding it required no shell edits. |
| **P3** | Claude Code widget | status.json schema + `Halo.Hooks.exe` + the 7 hooks + FileSystemWatcher + context/usage bars + activity line + waiting-input peek. Bar reflects a real CC session within ~1s. |
| **P4** | Cancel | Cancel interrupts a real running prompt via helper Ctrl+C (fallback Esc). Disabled when no live session. |
| **P5** | More widgets | Now Playing, Volume, Battery — each a clean `IWidget`. Multi-monitor default decided here. |
| **P6** | Ship | Config file + settings, autostart, single-instance, packaging (self-contained), **comment-strip pass** before any GitHub push. |

## Rules across phases
- One runnable check per non-trivial piece (token-summing from transcript, cancel signaling,
  glass interpolation math). No test framework unless it earns its keep.
- No comments in source; `ponytail:` markers only, stripped at P6.
- Don't start P3 until P2's contract is proven by the Clock widget.
