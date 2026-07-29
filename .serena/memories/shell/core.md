# Shell — the pill window and its state machine

## `Shell/LayeredNotch.cs` — the window
`WS_EX_LAYERED|TOOLWINDOW|TOPMOST|NOACTIVATE` popup at top-center, `ACCENT_ENABLE_ACRYLICBLURBEHIND`
for real frosted glass. Not `WS_EX_TRANSPARENT`, so it *does* receive mouse + OLE drops.
`Render(w,h,radius,tintAlpha,contentFade)` draws a GDI+ per-pixel-alpha bitmap (square top flush to the
screen edge, rounded bottom) and blits it with `UpdateLayeredWindow`. Public `Hwnd`, `OffsetX`
(user-parked horizontal offset), `SetCapturable`, `ClipboardImage` event, and the `RegisterDragDrop`
of the File Tray's `_dropTarget`.

Gotchas baked in from real bugs — do not "clean up":
- Final supersample downscale must be **bilinear**, not bicubic: bicubic's negative lobes undershoot the
  dark→transparent premultiplied edge into a visible dark rim.
- Glow/gradient textures must be **PArgb premultiplied**; non-premultiplied sources spray white garbage
  onto the layered surface.
- Pill-shaped clips use a flat-top path (`Fx.PillPath`); an all-corners rounded rect leaves dark crescents
  at the top corners.
- `WDA_EXCLUDEFROMCAPTURE` is applied unless pinned / `HALO_CAPTURABLE=1` (see `mem:core`).

## `Shell/NotchController.cs` — everything else (~1700 lines, deliberately one class)
Owns a `DispatcherQueueTimer` at 8ms. `Frame()` measures a real per-frame delta `_dt` (clamped 1..50ms —
never re-introduce a hard-coded tick, animations desync), `EaseOutBack`-lerps size/radius/tint/contentFade
between collapsed (`CollapsedW/H/R`) and expanded (`ExpandedW/H/R`), then calls `Apply` → `LayeredNotch.Render`.

Responsibilities living here: widget list construction + `ActiveIndices`/`AltIndices`/`Groups` (the swap
strip), `_primary`/`_userPicked` selection, foreground-follow (`FollowForeground` matches fg pid against
`IWidget.OwnerPids`; `FollowForegroundMedia` by process name), click polling via `GetAsyncKeyState`
(`PollClick`), the notification banner morph (`_notif*`), press-and-hold drag-to-move (`UpdateMove`,
`HoldSeconds`), pin (`DrawPushpin`, persisted to `%LOCALAPPDATA%\Halo\pin`), fullscreen hide,
`AdaptFrameRate` (fps tiers + `_heavy` → BelowNormal priority + slower glass capture), and the
edge-triggered local alerts (`CheckAlerts` → battery / CPU / RAM / agent-limit / internet / hourly chime,
each with a "fired" latch so they fire once per edge).

`AgentNoticeCoordinator` + `NotchVisibility` (same file) are the extracted, unit-testable pieces —
`tests/Halo.Tests/AgentNoticeTests.cs`, `NotchControllerTests.cs`. Prefer extracting new logic into
similar pure helpers rather than growing `Frame()`.

State persisted under `%LOCALAPPDATA%\Halo\`: `offset`, `pin`, `tray.txt`, `notif-seen.txt`,
`limit-fired`, `notif-debug.txt` (log).
