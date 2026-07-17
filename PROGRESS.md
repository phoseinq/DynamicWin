# Halo — progress

## DONE 2026-07-17: multi-session + strip redesign + glow everywhere (verified live, needs commit-audit only)
- **Multi-session CC**: hook writes `status-{agentPid}.json` per session (pid stamped every event —
  a mid-turn-born file without pid evades dedupe; session-end deletes file + legacy status.json;
  session-start sweeps dead-pid files). `StatusStore` scans `status*.json`+`app.json` → stable slots
  (`MaxSessions=4`, per-pid dedupe keeps freshest); `SessionLive(slot)` cached 1s (+3 tests).
  One `ClaudeCodeWidget` per slot, cwd-initial badge composited on the icon. Codex = two widgets
  (desktop/cli) via `Candidate(surface)`. Ceiling: N codex CLI sessions still share cli.json.
- **Strip UI (user's design)**: circle beside pill; apps stack DOWNWARD, a row with ≥2 sessions of
  one app fans RIGHTWARD on hover (closed circle shows the plain app mark, fan carries badges);
  primary session excluded. Union pill-path (flat top), 2x supersampled so icons stay crisp.
  Click maps row/fan → session; liquid drop flies from the actual clicked circle (_dropCX/_dropCY).
  Arrival toss lands on the circle (old bug: flew to slot*D below it).
- **Glow**: shared `Fx` helper — accent from icon (ConditionalWeakTable cache), dithered 128px
  radial texture (PArgb premultiplied! non-premul source on the layered surface sprayed white
  garbage), pill-shaped clip (flat top — all-corner clip left a dark crescent). Media art accent;
  CC coral / Codex green fallbacks; strip cells get 20-alpha washes.
- **Media polish**: iOS 9-bar center-weighted waveform; glass transport chips + eased hover; glyphs
  centred by path ink-bounds; soft volume chip + breathing bar.
- **Limits staleness**: 5-min heartbeat Timer in `Limits` (was: only panel-open/refresh → "59m ago").
- **Startup lag**: autostart moved Startup-folder lnk → Scheduled Task `Halo` at logon (no stagger).

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

## Always-on pill + limits without a session + 529 detection (2026-07-17)
- Pill no longer hides when no widget is active (fixes "missing after Windows startup"): only
  fullscreen hides it; with zero active widgets it renders as a bare glass pill (no expand/menu).
- CC/Codex expanded panels draw the limit bars + net graph + refresh even with `Session == null`
  (only the context bar needs a transcript). Stale/dead CC status renders as idle via a `Live`
  coercion helper; `StatusStore.IsLive` now cached 1s (it's hit per-frame).
- Widget visibility (user's call after seeing the Codex circle with ChatGPT closed): agents show
  only while their app actually runs — Codex needs desktop/CLI presence, Claude a live pid. So
  "limits without a session" applies while the app is open but idle, not when it's closed.
- Both NetMons treat an HTTP 5xx answer (incl. 529 Overloaded) as Lost → red ring, "api error :("
  verb, red api line in the graph during Anthropic/OpenAI overload storms (previously any HTTP
  status counted as healthy, so 529s looked fine).
- Verified: 68/68 tests; live pill screenshot post-deploy; `--render-widget` shots of the
  no-session Claude panel (limits visible) and Codex panel. Deployed to `%LOCALAPPDATA%\Halo\app`.
- Gotcha: sandboxed shells run on an isolated desktop — `Start-Process` there makes the pill
  invisible to the real session; deploy/restart Halo from an unsandboxed shell.

## Codex widget done (2026-07-16)
- Supports Codex Desktop and CLI; Desktop wins when both are active.
- Lifecycle hooks write `~/.codex/notch/{desktop,cli}.json`; rollout JSONL supplies live state,
  context, model window, and real rate-limit windows without private endpoints.
- Live rollout files are read with shared access so an active Codex session remains visible.
- Codex auto-promotes into the primary pill when it enters `working`; CLI Stop injects Esc and
  Desktop Stop remains disabled.
- Independent `chatgpt.com` HTTPS health graph, OpenAI asset, status-ring/mood/emerge animations,
  context and dynamic plan-limit rows match the Claude widget.
- User-reported Claude stale-status bug fixed: dead/reused PID makes Claude inactive; Halo hides when
  no widgets are active and reappears when one becomes active.
- Verification: Release build 0 warnings/errors, 56/56 tests, deployed to `%LOCALAPPDATA%\Halo\app`;
  hooks installed with backup at `~/.codex/hooks.json.halo-bak`; live Desktop screenshots captured.

## Polish round (2026-07-16, post-Codex-merge)
- **Compacting pill redesign (both widgets):** bottom sweep bar → whole-pill soft blue breathing
  fill (alpha 0.05→0.16, 2.4s cosine) + elapsed timer. Percent was tried and REMOVED (user called
  it fake — correctly): compact progress isn't knowable, even CC's spinner only shows a token
  counter hooks can't see. Cancelled compacts (Esc, no hook fires) covered by a 3-min expiry →
  pill falls back to idle mood; `PostCompact` hook (CC ≥2.1.x) now installed = real end edge
  (auto→working, manual→idle, +compactedAt +context refresh). "compacted :)" notice now triggers
  ONLY on a fresh compactedAt (<30s), never on a bare state transition. All three verified live.
- **Round 2 (user feedback):** percent is BACK but paced honestly — elapsed / the LAST compact's
  real duration (`lastCompactMs`, recorded by post-compact; 60s default; clamp 1-99). Esc-cancel
  now detected live: controller polls VK_ESCAPE while state=compacting and foreground is a
  terminal/claude/chatgpt host -> marks that compact (keyed by startedAt) cancelled -> pill drops
  to idle instantly; wrong guesses self-heal via post-compact. Verified: ~35% at 31s/90s expected,
  post-compact wrote lastCompactMs=31700. Esc path needs a real-compact hand test by the user.
- **Context accuracy:** `session-start` with `source=clear|startup` now drops the stale `session`
  block (user saw 250K after /clear). Verified: piped clear event → session removed.
- **Claude dual-surface:** hook exe writes `status.json` (terminal ancestor = CLI) or `app.json`
  (desktop app; `HALO_CLAUDE_SURFACE` override); `StatusStore` reads both, **CLI wins when live**,
  falls back to live app. 2 new tests.
- **Codex leftovers fixed:** verb map now speaks Codex tool names (`exec`→running…, `apply_patch`→
  patching…, `web_search`→googling :P, `view_image`→peeking o.o, `update_plan`→plotting…);
  Desktop Stop rewritten — PostMessage never reaches Electron, now restore-if-iconic +
  SetForegroundWindow + SendInput(Esc) (untested against a live running Desktop task).
- 67/67 tests; app + hooks republished to `%LOCALAPPDATA%\Halo`.

## Codex capsule accuracy and controls (2026-07-16) — design approved
- User approved both tracks: creative model-aware capsule/context data and working dual-surface Stop
  with anti-spam Weekly refresh/cache behavior.
- Root causes verified: Desktop Stop was intentionally disabled; context had an unsafe cumulative
  fallback; manual refresh rescanned twice; repeated rendering could save unchanged cache values.
- Design spec: `docs/superpowers/specs/2026-07-16-codex-capsule-accuracy-controls-design.md`.
- Next: user review of the committed spec, then TDD implementation plan and execution.
