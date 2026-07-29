# Widgets — the pill's faces

`Widgets/IWidget.cs` is the whole contract (`internal interface`, heavy use of default interface members
so widgets opt in only to what they need): `Icon` glyph + optional `IconImage`, `IsActive` (only active
widgets appear in the pill/strip), `Version` (bump = force re-render), `Animating` (want continuous
frames), `Ring`/`RingProgress` (status ring or progress arc on the strip circle), `AgentNotice`,
`OwnerPids` (focus-follow), `DrawContent(g,w,h,expandFade)` for the expanded panel,
`DrawCollapsed(g,w,h,fade)` for the ~220×40 pill, and `Buttons(w,h)` → list of
`(RectangleF rect, Action<PointF> onClick)` in pill-local coords (the `PointF` is what makes seek/volume
sliders work).

## Adding a widget
1. New file in `Widgets/`, implement `IWidget`.
2. Register it in the `NotchController` constructor's `widgets` list — **order matters**: it is the
   strip/fallback order. Multi-session widgets are registered one instance per slot
   (`MediaSessions.MaxSlots`, `StatusStore.MaxSessions`).
3. If it needs to steal the pill on an event, drive it through `AgentNotice` / the existing
   `_primary`/`_drop` machinery in the controller — don't add ad-hoc expand paths.
4. Verify with `Halo.App --render-widget <out.png> [media|claude|codex]` (see `mem:dev_hooks`).

## Current roster (constructor order)
`MediaWidget`×MaxSlots → `VlcWidget` → `DownloadWidget` → `FileTray` → `BtWidget` →
`ClaudeCodeWidget`×MaxSessions → `CodexWidget`(Desktop) → `CodexWidget`(Cli) →
`GenericAgentWidget`×MaxSessions.

## Shared helpers (reuse before writing new drawing code)
- `Widgets/Fx.cs` — accent extraction from an icon (`ConditionalWeakTable` cached), dithered radial glow
  texture, `PillPath`, `Shade`, `Badge`, `FlagGhost`, `CleanText` (NFKC — fixes fancy-Unicode/Persian),
  `IsRtl` (RTL text must be drawn with `DirectionRightToLeft` + `EllipsisCharacter`, otherwise mixed
  FA+EN mangles and the ellipsis lands on the wrong side).
- `Widgets/AppIcon.cs` / `Notifications/ShellIcon.cs` — real app icons. Resolution order that works:
  `ShellIcon` (clean transparent 256px, packaged + classic) → `AppIcon` (running exe icon) → logo.
- `AudioMeter` (output peak, Core Audio COM), `AudioSpectrum` (loopback bands), `KeyInject`,
  `Downloads`/`StoreInstall`/`GameInstall` (progress sources), `Privacy` (mic/cam in use), `MediaSessions`
  (GSMTC session pool; media is player-agnostic through it).

## Rules
- WinRT/COM events arrive off-thread: update a lock-guarded snapshot and bump `Version`; do GDI work only
  inside `Draw*`. Decode album art lazily in `DrawContent`.
- Never invent progress/percentages the OS can't tell us — the user rejects fake data. If a value isn't
  knowable, show a breathing/indeterminate state instead (see the compacting pill and Store "Waiting…").
- Controls that the underlying app may ignore (e.g. SMTC playback rate) should be hidden when unsupported
  rather than shown as a silent no-op.
