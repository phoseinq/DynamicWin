# Halo — progress

Goal: smooth glass notch for Windows (Dynamic Island for desktop). C# + .NET 9, Win32 layered window
rendered with `UpdateLayeredWindow` + GDI+. Spec in `docs/` (start at `docs/MAP.md`); current
architecture truth is `docs/decisions.md` (it supersedes the older Composition-based docs).

**Roadmap:** `docs/plans/2026-07-15-backend-media-notifications.md`. **M1 (backend) + M2 (Now Playing)
+ M3 (Volume) DONE + verified live (2026-07-15). M4 spike: `UserNotificationListener` WORKS UNPACKAGED
(no MSIX to read/mirror toasts); native suppression has no official API.** M4 banner UI not built —
waiting on user's call re suppression appetite.

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
- P6 config + autostart + package + comment-strip.

## M1+M2 done (2026-07-15)
- **M1 widget backend:** `IWidget` gained `bool IsActive` + `int Version`; the pill/dropdown build from
  **active** widgets only (`NotchController.ActiveIndices/AltIndices`), primary falls back to the first
  active widget when it goes inactive, and the version poll is aggregated across all widgets (dropped the
  StatusStore special-case). `ExpandedButton`+`ActivateButton` → `Buttons(w,h)` = list of (rect, Action)
  for multi-button widgets. ClaudeCode active when a status file exists; Clock/Battery always active.
- **M2 Now Playing:** `Widgets/MediaWidget.cs` on `GlobalSystemMediaTransportControlsSessionManager`
  (Spotify/browsers/any player). WinRT events run off-thread → update a lock-guarded snapshot + bump
  Version; GDI stays on the UI thread (album art decoded lazily in DrawContent). Draws art + title +
  seek bar (extrapolated while playing) + prev/play-pause/next (the M1 button list). Verified live:
  real Chrome session, thumbnail, timeline, swap-into-pill + expand all work unpackaged.
- Wart to polish in M3: RTL (Persian) titles left-align with the ellipsis on the wrong side. **FIXED**
  (MediaWidget `DrawLine` uses DirectionRightToLeft + EllipsisCharacter for RTL text).

## M3 done (2026-07-15)
- **Volume:** `Widgets/VolumeWidget.cs` — hand-rolled Core Audio COM interop (no NuGet). Reads master
  volume + mute; mute / −5% / +5% buttons write via `SetMasterVolumeLevelScalar`/`SetMute`. Bumps
  `Version` on change for instant re-render. Verified live: 100%→95% via minus button, restored.
- Widgets now: `{ ClaudeCode, Media, Volume, Clock, Battery }` (kept the demos — trim on request).
- Dev hook kept: `Halo.App --render-widget <png> [media|clock|battery|volume]`.

## Redesign per user (2026-07-16) — media-first, Apple-style
- Widgets trimmed to **Media + Claude Code** (deleted Clock/Battery/Volume widgets).
- **Collapsed previews:** Media = album art (HQ, AA rounded) + audio equalizer (9 fine bars, driven by
  REAL output peak via `AudioMeter`/IAudioMeterInformation, heights mostly 30-70%, soft multi-hue
  gradient from the art accent). CC = Claude icon (downloaded coral sunburst, embedded resource
  `Assets/claude.png`) on the left + live activity on the right.
- **Real app icons:** swap circle shows the source app's real icon (`AppIcon.ForAumid` extracts the exe
  icon of the running app — verified Spotify). Circle icons inset ~19% (10% smaller). Media falls back to
  album art, CC uses the Claude icon.
- **Media expanded:** volume control added (mute glyph + click-to-set bar, Core Audio via AudioMeter),
  click-to-seek on the progress bar. Buttons generalized to `Action<PointF>` (pill-local click point).
- **CC expanded:** removed the FAKE 5h/weekly bars; shows real Context (clamped K tokens) + cwd.
- **Cancel fix:** `Halo.Hooks` cancel now injects **Esc** into the CC console (WriteConsoleInput) instead
  of Ctrl+C to the whole group — cancels the running turn without closing the terminal. Redeployed to
  `%LOCALAPPDATA%\Halo\hooks`.
- **Animation:** faster open (Open 0.16s / Close 0.24s); circle **merges into the pill** (scales toward
  the pill edge + fades) on expand; swap has an **arrival bloom** (new app content eases in, not a snap).
  Still TODO: fuller metaball "join-then-separate" swap feel.

## M4 spike (2026-07-15) — notifications
- **`UserNotificationListener` returns full toast data UNPACKAGED** on this build (access Allowed,
  app/title/body all readable). Reading/mirroring toasts needs **no MSIX**.
- Native suppression of the Windows toast: **no official API.** Options if pursued: Focus-Assist/Quiet-
  Hours toggle (undocumented, own spike) or ship mirror-only (both show). Gate before building banner UI.

## Verify recipe
Run exe in background, drop a colorful WinForms backdrop behind it, move cursor onto the pill center
(1280,15) to hover-expand, `CopyFromScreen` to PNG, view. Crash log: `%TEMP%\halo-crash.log`.
