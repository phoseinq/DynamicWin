# Roadmap — widget backend, media, notifications

Status: **M1 + M2 + M3 DONE + verified live (2026-07-15).** M4 read-spike done: `UserNotificationListener`
**works unpackaged** (no MSIX to read/mirror toasts); native suppression has no official API. M4 banner
UI not built — awaiting user's suppression-appetite call.

## Goal
Turn the notch from "Claude Code + demo widgets" into a small, real app platform:
a widget backend, a Now Playing widget, a couple more real widgets, and (stretch)
intercepting Windows notifications to show them in the notch.

## Where we are
- `IWidget` = `Icon`, `DrawContent(g,w,h,fade)`, `ExpandedButton(w,h)` (one rect), `ActivateButton()`.
- `NotchController` holds `_widgets[]` + `_primary`, renders primary in the pill, the rest as icons
  in the hover-dropdown; swap = drop animation. Re-render is driven by a per-tick change check
  (`StatusStore.Version`, cursor, glass capture, clock tick).
- Glass = BitBlt of the region behind the notch; desktop→black; fullscreen apps hide the notch.

## Phase M1 — Widget backend  ✅ DONE
Scope: make widgets first-class and data-driven, no plugin/DI framework.
- Extend `IWidget`: add `bool IsActive` (widget appears in the app list / can be primary only when it
  has something to show) and a change signal. Laziest signal: an `int Version { get; }` the controller
  polls in OnTick (same pattern as `StatusStore.Version`) — no events/threads to manage on the UI side.
- `NotchController`: build the dropdown + primary from **active** widgets only. Hide the circle when
  <2 active. If the current primary goes inactive, fall back to the next active one.
- Generalize actions: `ExpandedButton` (single) → `IReadOnlyList<(RectangleF rect, Action onClick)>`
  so a widget can have several buttons (media needs 3–4). Update the Cancel button + click polling.
- Deliverable: same behavior as today but list is activity-gated and multi-button ready.
- Risk: low. This is a refactor of existing code.

## Phase M2 — Now Playing (media players + "codec"/multi-source)  ✅ DONE (single-session)
Scope: a `MediaWidget` backed by system media transport controls (works for Spotify, browsers,
players — that's the "codec/any app" coverage).
- API: `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` →
  `GetCurrentSession()`. Per session: `GetMediaPropertiesAsync()` (title/artist/thumbnail),
  `GetPlaybackInfo()` (status + which controls exist), `GetTimelineProperties()` (position/end).
  Controls: `TryTogglePlayPauseAsync / TrySkipNextAsync / TrySkipPreviousAsync`.
- Threading: WinRT events (`CurrentSessionChanged`, `MediaPropertiesChanged`, `PlaybackInfoChanged`,
  `TimelinePropertiesChanged`) fire off-thread → marshal to the UI/timer thread (existing
  `DispatcherQueue`) → bump `Version` → controller re-renders. Do NOT touch GDI from WinRT callbacks.
- Thumbnail (album art): `MediaProperties.Thumbnail` is an `IRandomAccessStreamReference` →
  open → decode to `Bitmap` once per track, cache; draw it in the pill (and as the collapsed icon).
- UI: collapsed = art thumbnail + tiny playing indicator. Expanded = art, title/artist, seek bar with
  elapsed/total, prev / play-pause / next buttons (M1's multi-button actions).
- `IsActive` = a current session exists (playing or paused).
- Multi-player: start with `GetCurrentSession()` (system picks). Later, list sessions and let the
  dropdown switch between them if the user wants per-player control.
- Risk: medium — WinRT async lifetime, thumbnail decode, seek-bar time formatting.

## Phase M3 — More real widgets + polish  ✅ DONE (Volume added, Clock/Battery kept, RTL title fixed)
- Replace demo widgets with real ones as wanted: **Volume** (Core Audio `IAudioEndpointVolume` /
  `IMMDeviceEnumerator`; scroll or drag to set, mute toggle), keep **Battery**, keep **Clock**.
- Decide which demo widgets stay (user asked earlier: battery placeholder — confirm keep/replace).
- Risk: low–medium (Core Audio COM interop for Volume).

## Phase M4 — Windows notifications: block native + show ours (STRETCH, "if you can")
Scope: read incoming toasts, render them as a notch banner; suppress the native toast if feasible.
- Read: `Windows.UI.Notifications.Management.UserNotificationListener.Current` →
  `RequestAccessAsync()` → `GetNotificationsAsync(NotificationKinds.Toast)` + the change event.
  Gives app name, title, body, and can decode the app icon.
- **Feasibility spike DONE (2026-07-15): WORKS UNPACKAGED.** `UserNotificationListener.Current` →
  `RequestAccessAsync()` returned Allowed and `GetNotificationsAsync(Toast)` listed every live toast
  (app/title/body via `KnownNotificationBindings.ToastGeneric`) with **no MSIX**. So the read/mirror path
  needs no packaging. Also subscribe to `NotificationChanged` for live arrivals.
- Suppress native toast: there is **no official API**. Options, in order: (a) toggle Focus
  Assist / Quiet Hours so Windows holds toasts while our listener still receives them (mechanism is
  undocumented — needs its own spike); (b) if suppression proves unreliable, ship "mirror in the notch"
  and let both show. Don't over-invest until (a) is proven.
- UI: reuse the capsule/dropdown drawing for a slide-down banner (app icon + title + body, auto-dismiss
  timer, click to activate/dismiss).
- Risk: HIGH / uncertain. Gate the whole phase on the two spikes above; report findings before coding UI.

## Cross-cutting
- One re-render path: everything sets/bumps a version the OnTick loop already polls — no second timer.
- Keep GDI on the UI thread; WinRT/COM callbacks only marshal + set state.
- After each phase: build, run, screenshot-verify, then `dotnet publish -c Release -o %LOCALAPPDATA%\Halo\app`
  (that's what reboot launches — build alone doesn't update it).
- Update `PROGRESS.md` (note: it still says ACCENT acrylic — outdated, glass is now BitBlt) as phases land.

## Open decisions to settle at "go"
1. Media: single current session (simple) or full multi-player switching?
2. Keep the Battery/Clock demo widgets, or replace with Volume + Now Playing only?
3. Notifications: are you willing to package as MSIX if the listener needs it? If not, treat M4 as
   best-effort mirror with no native suppression.

## Suggested order
M1 → M2 → M3, then spike M4 and report before committing to it.
