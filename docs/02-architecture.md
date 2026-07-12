# 02 — Architecture

## Notch window
- Borderless, transparent, topmost, no taskbar entry, tool window. Positioned top-center of the
  target monitor (default: primary). Per-monitor DPI aware.
- **Not** click-through globally: the pill hit-tests its own bounds so hover/click work; the rest
  of the window is transparent and passes clicks through (WS_EX layered + hit-test region, or a
  window sized to the pill/panel bounds that grows on expand).
- The visible surface is a **Composition Visual tree**, not XAML panels — see 04. Expanded content
  (widget UserControls) is XAML hosted inside the expanded area.

## State machine
`Idle` (collapsed pill) → `Peek` (small hint, e.g. a widget wants attention) → `Expanded` (full panel).
- Triggers: pointer hover/click on the pill; a widget raising `RequestAttention` → Peek; click/hover
  → Expanded; pointer leave / Esc / click-away → back down.
- Every transition is a spring on size + corner radius + glass params (04). One place owns the
  current state and drives the springs.

## Threads
- UI thread: XAML, input, widget logic, file watchers.
- Compositor thread: all shell animation (free, driven by Composition once started). Keep per-frame
  work off the UI thread so the pill never stutters even when a widget is busy.

## Code / folder layout
```
Halo.sln
src/
  Halo.App/                     WinUI 3 app — entry point + shell
    Shell/                      NotchWindow, NotchController (state machine), positioning
    Rendering/                  GlassBackdrop, Springs, Grain, Geometry (04 lives here)
    Widgets/                    IWidget, WidgetHost, layout (03)
    Widgets/ClaudeCode/         CC widget: view + status-file reader + cancel (05)
    Widgets/NowPlaying/         later
    Widgets/Volume/             later
    Widgets/Battery/            later
    Config/                     settings model + load/save (08)
  Halo.Hooks/                   tiny console exe used BY Claude Code hooks:
                                writes status.json, captures console HWND, reused by Cancel (06)
hooks/                          the hook script files + settings.json snippet to install (06)
docs/                           these specs
```
`Halo.Hooks` is compiled C# (not PowerShell) because grabbing the console HWND and writing JSON
atomically is cleaner there, and the Cancel path reuses the same HWND-capture code.

## Widget host wiring
`WidgetHost` owns the list of `IWidget`, renders each one's collapsed representation into the pill
row, and stacks expanded views in the panel. Widgets are compiled-in for v1 and registered in one
place (a static list). See 03.
