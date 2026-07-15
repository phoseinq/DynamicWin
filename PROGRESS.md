# Halo — progress

Goal: smooth glass notch for Windows (Dynamic Island for desktop). C# + .NET 9, Win32 layered window
rendered with `UpdateLayeredWindow` + GDI+. Spec in `docs/` (start at `docs/MAP.md`); current
architecture truth is `docs/decisions.md` (it supersedes the older Composition-based docs).

## Architecture (current, post-P2 pivot)
- `Shell/LayeredNotch.cs` — the window. `WS_EX_LAYERED|TOOLWINDOW|TOPMOST|NOACTIVATE` popup +
  `ACCENT_ENABLE_ACRYLICBLURBEHIND` (real frosted glass) + `Render(w,h,radius,tintAlpha,contentFade)`
  which draws a GDI+ per-pixel-alpha bitmap (rounded-bottom/square-top path, dark tint, top
  highlight, content) and blits with `UpdateLayeredWindow`.
- `Shell/NotchController.cs` — `DispatcherQueueTimer` (8ms) polls `GetCursorPos`; `EaseOutBack`
  spring lerps size/radius/tint/contentFade between collapsed (220x40) and expanded (560x220); calls
  `LayeredNotch.Render` each frame.
- `Interop/Win32.cs` (window class, ULW, acrylic, cursor), `Interop/Dispatcher.cs` (DQ controller),
  `Shell/NotchGeometry.cs` (+2 tests).

## Done
- P0 skeleton + geometry (2 tests).
- P1 glass + hover-spring expand/collapse; square top flush to screen, rounded bottom; no dark halo.
- **P2 pivot (2026-07-15):** dropped `Windows.UI.Composition` (couldn't host bitmap content without
  the missing `LoadedImageSurface` / heavy D2D). Rewrote shell as `UpdateLayeredWindow` + GDI+.
  Real acrylic frosts the desktop through ULW; content renders **crisp** (Segoe Fluent Icons + text).
- **P3 Claude Code panel + hooks (2026-07-15):** DONE, verified end-to-end.
  - Notch side: `Widgets/IWidget.cs` contract; `Widgets/ClaudeCodeWidget.cs` (green/amber/dim state
    dot, "Claude Code" + activity line, Session-context/5h/Weekly bars, top-right Cancel button);
    `ClaudeCode/Status.cs` (`StatusStore` FileSystemWatcher on `~/.claude/notch/status.json`, version
    poll → live re-render); click via `GetAsyncKeyState` polling in `NotchController`.
  - `src/Halo.Hooks/` — helper the CC hooks call: writes status.json per event (state/tool/prompt/
    context-from-transcript/pid/consolePid), and `cancel <pid>` = AttachConsole + Ctrl+C.
  - `hooks/install-hooks.ps1` publishes the helper to `%LOCALAPPDATA%\Halo\hooks` and merges 7 hooks
    into `~/.claude/settings.json`. **User must run it** (their live CC config).
  - Verified: helper writes status.json; panel reflects state changes **live** (idle→working shot).

## Next
- **Run `hooks/install-hooks.ps1`** to wire real Claude Code sessions (not yet installed).
- **Usage-limit data (5h/weekly)** still best-effort/unpopulated — the one open data source (no clean
  API). Panel hides those bars when `usage` absent; context bar is real. Refine later.
- Live-tune spring feel on the 144Hz panel (`EaseOutBack` c1, `DurationSeconds`).
- P5 Now Playing / Volume / Battery widgets. P6 config + autostart + package + comment-strip.

## Verify recipe
Run exe in background, drop a colorful WinForms backdrop behind it, move cursor onto the pill center
(1280,15) to hover-expand, `CopyFromScreen` to PNG, view. Crash log: `%TEMP%\halo-crash.log`.
