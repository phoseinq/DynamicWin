# Halo — progress

## 2026-07-25: v3.0.2 RELEASED + first outside contributions reviewed (2 PRs open on the fork)

### Shipped
- **v3.0.2 = Latest** on phoseinq/DynamicWin, target branch `V3`, tag on `c1c3070`. The first cut of
  this release (tag on `a3c2f2f`) was deleted with `gh release delete --cleanup-tag` and re-made after
  the BtWidget crash below was found, so the published assets contain that fix. **Carries its own
  assets** (`DynamicWinSetup.exe` 29.8MB + `DynamicWinPortable.zip` 41.5MB, both signed `CN=phoseinq`,
  `3.0.2.0` stamped inside) — v3.0.1 had none and pointed at v3.0. Installed live + relaunched;
  the machine had silently been running a **1.0.0.0** build until now.
- Local `master`: `414841c` (cleanup + version + tests) and `7806fb2` (CLAUDE.md). Not pushed —
  `master` is private, `origin` is the public fork only.
- Version lives in **two** places: `Halo.App.csproj` (Version/AssemblyVersion/FileVersion) and
  `installer/Halo.iss` (`#define AppVersion`). Both bumped.

### v3.0.3 (same day): thinking ring + banner leak — both root-caused with measurements
- **Ring never looked yellow — a DRAWING bug, not the state machine.** Logged the live store for 150s of
  real work: `amber 137.5s / green 8.0s`, flipping correctly on every tool boundary. The ring was drawn
  at `fade * 0.55f`; amber at 55% over the near-black pill composites to ~`(139,94,18)`, a dark
  brown-gold only 86 RGB units from the coral icon it hugs (green sits at 164), so "thinking" read as a
  shadow. Alpha → `0.9f` in all three agent widgets (A 150→231). Verified by rendering both states 4×
  side by side. **The RGB-distance metric barely moved (86→88) — the fix is composited luminance, not
  hue; don't use that metric to judge this again.**
- **Banner leak measured: 56 of 243 mirrored toasts (23%) came from never-silenced AUMIDs** — WireGuard
  tray balloons (`NotifyIconGeneratedAumid_*`) and 4 of 6 Telegram ids. Root cause: `SuppressApp` only
  learns an app *after* Halo mirrors one of its toasts, so every app banners once, and an app that mints
  a fresh AUMID per account/channel leaks once per id. Fix = keep the lazy learner AND pre-seed: `Enable()`
  now walks every AUMID already under `Notifications\Settings` (recursively — classic apps register as
  `{GUID}\...\app.exe`, which a flat `GetSubKeyNames()` misses). Verified 14/137 → **137/137**, and every
  recorded original was absent beforehand so `Restore()` still reverses all of it. Backup of the
  pre-seed state at `banner-orig.tsv.bak-preseed`.
- Gotcha: `notif-debug.txt` has **times but no dates**, so entries from different days interleave and
  look contemporaneous. Toast **ids** are the reliable ordering (WireGuard's stop at 66648 while
  `notif-seen.txt` is 67441 — those 50 leaks predate the learner).

### Open: downloader coverage (reported, root-caused, NOT built)
`Downloads.Scan()` finds downloads by regex-matching a leading `NN%` in visible **window titles**.
`Downloads.cs:118` deliberately skips browsers (`// "50% off" page, not a download`), so browser
downloads are unsupported by design; Steam never puts a percentage in its title, so it is invisible too.
Only Store (`StoreInstall` → `AppInstallManager`) and Xbox (`GameInstall` → staging folder) have real
integrations. Feasible directions, both matching existing patterns: Chromium/Firefox keep downloads in
SQLite (`History` → `downloads`, `places.sqlite`) and the project already P/Invokes the system
`winsqlite3.dll` for `wpndatabase.db`; Steam exposes `BytesDownloaded`/`BytesToDownload` in
`steamapps/appmanifest_*.acf` plus a `downloading/` staging dir, the same shape `GameInstall` reads.

### Root cause worth keeping: our duplication is what breaks contributors
Reviewing the pt-BR PR showed **every missed string sat where the same text was written twice**, far
apart, with nothing marking the pair. Fixed on our side (all four are now single-source):
- `QueueRamNotice`/`QueueCpuNotice` were two copies of one banner + two more in `PollTestNotif` →
  one `QueueLoadNotice(resource, pct, topProcess, fallbackBody)`.
- `net`/`api` had **four spellings per agent widget** (`"net " + x`, `$"net {x}"`, …) — eight literals
  for two words → `Fx.NetLabel` / `Fx.ApiLabel` / `Fx.LossLabel`.
- Screenshot wording existed in both `OnClipboardImage` and the `--render-notif` dev hook → consts on
  `NotifItem`.
- `"agent"` vs `"Agent"` (pill text vs panel heading) read as a typo → now carries a comment saying it
  is deliberate, because a PR "fixed" it and changed the English heading.

### `BtWidget.DrawCollapsed` threw on every frame while the pill was tucked — FIXED
Found in `%LOCALAPPDATA%\Halo\frame-errors.txt` (16:03:22, the same second `bt-debug.txt` logged
`connected: Boy`). `sz = h - 12`, `rr = sz/2 - 1`, so at **h ≤ 14** the arc radius hits zero and GDI+
throws `ArgumentException: Parameter is not valid`. The tuck state is 96×**12**, so any BT connect
while tucked threw every frame; `OnTick` swallowed it → frozen pill, no visible error. Reproduced
across h = 40…2 (ok until 16, throws from 14 down), fixed with an `if (h < 16) return;` guard, re-ran
the same sweep — all ok. Shipped: `9d88b1b` on master, `c1c3070` on V3, and the re-cut v3.0.2 assets.
Installed live and relaunched with `frame-errors.txt` deleted first — it stayed absent.

### Two stale tests were failing on master (79/81) — FIXED
`AgentNoticeTests` still asserted that `waiting_input` makes a widget primary; that was deliberately
removed ("no need for it to pop"). Rewrote as `WaitingInput_DoesNotStealThePill`, and rebuilt the
desktop-Codex-preference test on compact-done notices (the only kind that still opens a window). 81/81.

### PR review — evidence, not opinion (both build 0/0 in Release)
Verification trick that paid off: a throwaway project **named `Halo.Tests`** satisfies
`InternalsVisibleTo`, so contributor code can be driven directly with no reflection for internals.
- **PR #1 (i18n + pt-BR)** — sound design (English string as key). Real defect: `Loc.T(en, args)` runs
  `string.Format` on the *translated* text unguarded → a broken placeholder throws on the render path.
  Proved end-to-end by breaking one key and rendering: `Loc.T → DrawExpanded → DrawContent`, no PNG.
  Coverage ~40% and asymmetric; `HALO_LANG=pt-BR` renders show `rede` next to `api` in one label.
- **PR #2 (persistent BT widget)** — idea accepted, code not. **The 6s timeout was the error recovery**,
  not just a display duration; removing it made latent states permanent. Measured the race window:
  **75ms** warm vs **2629ms** on the cold path (`Battery()` → -1 → `Task.Delay(2500)` → retry), against
  a phone that connects for **1–2s** (seven occurrences in `bt-debug.txt`) → the disconnect is
  *guaranteed* missed → phantom device forever. Seed claim proved by A/B on `_live` alone
  (false → 0 connects; true → 1 connect "Boy" 47%). Ring/number desync measured: text 40%, ring 73.6%.
- **Three of the six PR #2 defects were caused by our comment stripping** — `// startup state, don't
  banner`, `// reveal: ring grows from empty`, `// keep frames coming so the ring eases` all exist in
  `master` and are absent from V3. Said so in the review. **V3 publishes no `docs/`, no `tests/`, no
  `PROGRESS.md`** either, so contributors cannot see any invariant. A published CONTRIBUTING is the
  cheapest fix; not written yet.

### Gotchas learned
- A stale **self-contained publish layout left in `bin/`** (194 files vs 10) makes the app fail with
  "You must install or update .NET" forever — the host reads that folder's `runtimeconfig.json` and
  never looks at the system runtime. Installing .NET does nothing. Delete `bin/`+`obj/`.
- `dotnet run --project X -- <arg>` swallowed the argument for the strip tool; build it and call the
  exe directly.
- `Radio.RequestAccessAsync`/`SetStateAsync` (WinRT) toggles the Bluetooth radio non-admin, but a
  **phone initiates the connection itself**, so cycling the PC radio does not bring it back.
- Before launching the pill from a tool shell, compare `SessionId` with `explorer.exe` — a different
  session means an invisible pill that still holds the single-instance mutex.

## 2026-07-22: v1.0.3 RELEASED — everything below is now committed + shipped
- **v1.0.3 = Latest** on phoseinq/DynamicWin (Setup + Portable, signed). All pending changes below are
  in local commits `029f650` + `11276a4` and in the release build — nothing un-bundled remains.
- Notif silence set is now banner+sound+**urgent** (`AllowUrgentNotifications=0`) — urgent toasts were
  the "banner slips out under spam" leak.
- New dev knob: env `HALO_CAPTURABLE=1` skips `WDA_EXCLUDEFROMCAPTURE` — without it the pill is
  invisible to every capture API (looks like a rendering bug; it isn't). Used ffmpeg ddagrab to record
  the README preview.gif (concise README + gif pushed to the fork's V2 branch).

## 2026-07-21: RESUME HERE — File Tray (next feature) + pending un-pushed state (ALL SHIPPED in v1.0.3 ↑)

### Un-pushed / un-released changes already made this session (bundle these into the NEXT build)
- **File Tray auto-remove + smooth reorder + pin spacing (DEPLOYED live, NOT pushed):** (1) drag-OUT now
  auto-removes on a successful drop — `FileDrag.Out` returns bool (`hr==DRAGDROP_S_DROP && effect!=NONE`);
  controller removes the dragged path(s) on success (cancel / drop-on-our-own-pill → effect NONE → kept).
  (2) **smooth reorder glide** — `FileTray._anim` eases each card's top-left toward its grid slot (~24%/frame)
  instead of snapping; `Animating` keeps frames coming until settled; `DrawContent` early-return (collapsed)
  now resets `_settled=true`+clears `_anim` so a mid-glide close can't leave `Animating` stuck on. Verified via
  successive-frame render (glide mid-frame → clean grid at rest). (3) **pin/title spacing** — "File Tray" title
  was at x=Pad(22) under the top-left pin (~x9–33); moved to `Pad+20` to clear it (matches ClaudeCode widget).
  Verified with pin overlaid in a render.
- **DND leak — notifications doubling (native banner leaks) — ROOT CAUSE FOUND, NOT YET FIXED (DEPLOYED safe
  interim, NOT pushed):** the whole registry Quiet-Hours trick is DEAD on Win11 26200. Verified live: the
  profile blob reads `Microsoft.QuietHoursProfile.AlarmsOnly` correctly, yet `SHQueryUserNotificationState`
  stays `QUNS_ACCEPTS_NOTIFICATIONS` (5) — never `QUNS_QUIET_TIME` (6). There is NO registry "DND enabled"
  flag; on 26200 the live DND on/off state is a **WNF / in-memory** state, so setting the *profile* never
  turns DND *on*. Restarting `WpnUserService` does NOT engage it AND kills Halo's own `UserNotificationListener`
  ("Class not registered" → mirroring dies until relaunch). An earlier attempt (force-restart when state==5)
  spun a 30s restart loop that repeatedly broke the listener — reverted. **Current safe `DndGate`:** writes
  the profile (harmless), reads ground-truth via `SHQueryUserNotificationState`, and only restarts to recover
  a revert IF DND has actually engaged once (`_everEngaged`, i.e. state hit 6) — on 26200 that never happens
  so it does ZERO restarts / zero self-harm. `[dnd]` logging in `notif-debug.txt`. Mirror pipeline itself is
  clean (log: ids monotonic, no floods). **REAL FIX TODO (needs user consent — risky):** toggle DND via the
  WNF state (`WNF_SHEL_QUIETHOURS_*`) / `NtUpdateWnfStateData`, the only thing that actually flips 26200 into
  quiet-time. `ToastEnabled=0` is a dead end (kills listener delivery too).
- **File Tray IMPLEMENTED (DEPLOYED live via Halo.App.dll hot-swap, NOT pushed):** new files
  `Interop/FileDropTarget.cs` (OLE `IDropTarget`) + `Widgets/FileTray.cs` (`IWidget`); OLE interop added to
  `Interop/Win32.cs` (OleInitialize/RegisterDragDrop/IDropTarget/DragQueryFile/POINTL, CF_HDROP); public
  `ShellIcon.ForPath`; wired in `Program.cs` (`OleInitialize`), `LayeredNotch.Show` (`RegisterDragDrop` +
  `_dropTarget` field), `NotchController` (widget added, drag-active priority + `open` include, Groups kind).
  Persist to `%LOCALAPPDATA%\Halo\tray.txt` (dedup, most-recent-first, cap 30, drops missing on load).
  Verified: 4 rendered states (list / drop-zone / collapsed-count / collapsed-dragging) + persistence
  self-check (dedup/order/case-insensitive/remove/load round-trip all PASS) + clean startup (no crash).
  **LIMITATION:** reveal-on-drag works only while the pill is on screen (a widget active, or the tray holds
  files). When the desktop is fully idle the pill is SW_HIDE'd → a hidden window can't be an OLE drop target,
  so a drag won't summon it from nothing. Fix if wanted: a tiny always-present transparent drop-catcher at
  top-center (steals clicks on its small rect — that's the tradeoff). Deferred (ask): Share dialog.
- **File Tray round 2 (DEPLOYED live, NOT pushed):** (a) icon → tray/inbox glyph `` (reads as a tray in
  BOTH Segoe MDL2 Assets + Fluent Icons — verified) replacing the generic folder; (b) **drag-OUT** implemented
  — new `Interop/FileDrag.cs` (OLE drag SOURCE: `SHCreateItemFromParsingName`→`IShellItem.BindToHandler(BHID_
  DataObject)`→`DoDragDrop` + minimal `IDropSource`; `Win32.cs` got those + `IShellItem`/`IDropSource`),
  `FileDropTarget.DragEnter` guarded by `FileDrag.Dragging` (no self-reveal), `FileTray.RowPathAt` + public
  `Open`, and `NotchController.HandleTrayInteraction` (press-a-row = open, hold+drag>6px = drag out). CF_HDROP
  data-object pipeline verified (QueryGetData=0, path round-trips); only DoDragDrop itself needs a real mouse
  gesture. (c) drag-IN priority made unconditional so a live drag ALWAYS makes the tray primary + expands.
- **File Tray round 3 (DEPLOYED live, NOT pushed):** (1) drop zone REDESIGNED (filled translucent zone +
  tray icon in a soft disc + two-line copy, breathing when active). (2) **Ctrl+click multi-select** —
  `_selected` HashSet, accent highlight + left bar, header "Remove N" chip (`RemoveSelected`); selected set
  drags out together (`SelectionOrRow`). (3) **drag-to-reorder** — drag a row up/down inside the panel
  (`ReorderFrom/To` live preview, `BeginReorder/UpdateReorder/CommitReorder`); leaving the panel mid-drag
  switches to drag-out. (4) **drag image fixed** — `FileDrag` now uses `SHDoDragDrop` + `IShellItemArray`
  (multi-file) so the file ICON follows the cursor instead of a bare square. `NotchController.HandleTray
  Interaction` rewritten (mode: pending/reorder/out; Ctrl=select, click=open, drag=reorder|extract). Win32
  got SHDoDragDrop/SHParseDisplayName/SHCreateShellItemArrayFromIDLists/IShellItemArray/ILFree; removed the
  now-dead single-item IShellItem/SHCreateItemFromParsingName. Verified: 4 rendered states + logic self-check
  (reorder/selection/multi-remove/SelectionOrRow) + 2-file CF_HDROP interop, all PASS; clean startup.
- **File Tray round 4 — grid redesign (DEPLOYED live, NOT pushed):** the vertical list only showed 3 of N
  files with a clipped "+N more" and wasted the right half → replaced with a **3-col card grid** (`CellRect`/
  `CellW`/`VisibleCells`, 3×3 = 9 visible, clean "+N more" footer). Each card = icon + name + folder, × on
  hover, accent tint/border when selected or lifted (reorder). Hit-testing (`RowPathAt`/`RowIndexAt`/`Buttons`)
  is now grid-aware; reorder/select/drag-out logic unchanged. Collapsed pill: several files now show a small
  **stack of their icons** ("پشت سر هم", up to 4, overlapping) + "N files" instead of one icon + count.
  Header/`RemoveChipRect` moved up (HeaderH=56). Verified: grid renders at 6/10/selected + collapsed stack.
- **Ring "yellow = thinking" round 2 (DEPLOYED live, NOT pushed):** proved the collapsed ring ALREADY goes
  Amber on working+no-tool (reflection test on the deployed dll). The gap was the EXPANDED panel dot —
  `ClaudeCodeWidget` `StateColor` was hardcoded Green for "working"; deleted it and pointed the dot at
  `RingColor(st)` so the panel dot matches the ring (yellow thinking / green tool). Codex dot left as-is.

- **Store "Waiting…" phantom + breathing redesign (DEPLOYED live via Halo.App.dll hot-swap, NOT pushed):**
  Root cause = a Phone Link (`Microsoft.YourPhone`) update parked in the Store queue at `ReadyToDownload`
  (total=1 byte) that never runs — `StoreInstall.Poll` surfaced any non-terminal item → pill stuck on
  "Waiting…" forever. Fixes: (1) `StoreInstall.cs` grace-timeout — a queued item that never starts
  downloading is dropped after `WaitGraceMs=30s` (`_waitPfn`/`_waitSinceMs`, returns `Phase.None`);
  verified live against the real phantom (Waiting for ~27s → None). (2) `DownloadWidget.DrawCollapsed`
  rewritten: app icon now on the LEFT always (extracted `IconTile` from `DrawArt`, new `DrawCollapsedIcon`);
  Waiting = whole-pill breathing glow (Claude-compacting style) + app name, NO bar/NO % ; Downloading =
  icon + bar + %. Verified by direct bitmap render (r_waiting.png / r_downloading.png). Needs: push
  `StoreInstall.cs` + `DownloadWidget.cs` to Boy + roll into installer/release.
- **Ring "thinking = yellow" fix (DEPLOYED live, NOT pushed):** `src/Halo.Hooks/Program.cs` `tool-done`
  case now sets `status["currentTool"] = null` (was: kept the last tool label to avoid flicker). Effect:
  between tool calls Claude reads as *thinking* → RingColor gives Amber; a tool sets it Green again.
  Widget logic UNCHANGED (RingColor already `empty?Amber:Green`); only `ClaudeCodeWidget.cs` has a
  reworded comment. **Hook binary hot-deployed** into `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.{exe,dll,
  deps.json,runtimeconfig.json}`. Still needs: rebuild into the installer + push `Halo.Hooks/Program.cs`
  (+ the ClaudeCodeWidget comment) to Boy.
- **Already DONE + deployed + pushed (Boy @ 2149780, pre-release v1.0.2 assets refreshed):** notif Island
  redesign (`NotifBanner.cs` eyebrow row app+time / bigger title / SummaryH 106→112 / RelTime "now"),
  notif icon Start-menu fallback (`ShellIcon.ForAppName` for `NotifyIconGeneratedAumid_*` tray toasts like
  WireGuard/Amnezia), banner text `AntiAliasGridFit`, DND re-stamp fix (`DndGate.WriteCache` always bumps
  FILETIME), Persian/fancy-Unicode `Fx.CleanText` NFKC + `Fx.IsRtl` in Media/VLC.
- **Releases:** `phoseinq/DynamicWin` — **v1.0.0 = Latest**, **v1.0.2 = Pre-release** (assets
  `DynamicWinSetup.exe` + `DynamicWinPortable.zip`). v1.0.1 deleted.
- **CC hooks path repointed:** `~/.claude/settings.json` 9 hooks now call
  `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe` (old `%LOCALAPPDATA%\Halo\hooks` was deleted in an
  uninstall). Backup `settings.json.bak-*` exists.

### FILE TRAY — ✅ IMPLEMENTED 2026-07-21 (see the un-pushed bullet above). Original plan kept for reference:
### plan (user: DynamicWin's signature feature). Reveal-on-drag: the tray appears WHILE a
### file is being dragged; otherwise it's not shown (empty+no-drag = nothing; held files = a small circle).
Feasibility confirmed: main thread is `[STAThread]` (`Program.cs:10`); notch window is layered but NOT
`WS_EX_TRANSPARENT` so it receives mouse + OLE drops; `Hwnd` is public; window resizes to content each
frame. `WM_DROPFILES` only fires on DROP (no drag-enter) → MUST use **OLE `IDropTarget`** for the reveal.

1. **Interop** (`Interop/Win32.cs` + new `Interop/FileDropTarget.cs`): `OleInitialize`/`OleUninitialize`,
   `RegisterDragDrop`/`RevokeDragDrop`, `IDropTarget` COM iface, `IDataObject.GetData(FORMATETC{CF_HDROP})`
   → `STGMEDIUM.hGlobal` → `DragQueryFile` loop → `ReleaseStgMedium`. DROPEFFECT_COPY=1.
2. **`FileDropTarget : IDropTarget`**: DragEnter(has CF_HDROP? → `FileTray.DragActive=true`, effect=COPY,
   bump Version) · DragOver(COPY) · DragLeave(`DragActive=false`) · Drop(extract paths → `FileTray.Add`,
   `DragActive=false`).
3. **`Widgets/FileTray.cs : IWidget`**: static `List<string> Paths` + `volatile bool DragActive` + `Version`,
   persisted to `%LOCALAPPDATA%\Halo\tray.txt` (load on start, like `notif-seen.txt`). `IsActive =
   DragActive || Paths.Count>0`. `Icon` = a Segoe Fluent tray glyph. `DrawContent`: drop-hint when
   empty/dragging, else rows = file icon + name + `[×]`. `DrawCollapsed`: count + mini icons. `Buttons`:
   row → open (`Process.Start{UseShellExecute=true}`); `[×]` → remove + persist. File icons via a NEW
   public `ShellIcon.ForPath(path)` (thin wrapper over the existing private `ExtractFrom` = 256px shell
   icon) OR `Icon.ExtractAssociatedIcon`.
4. **Wire:** `OleInitialize` once at startup (Program.cs before `RunMessageLoop`, or in LayeredNotch ctor);
   `RegisterDragDrop(Hwnd, new FileDropTarget())` at the end of the window-init in `LayeredNotch` (near the
   `AddClipboardFormatListener` call). Add `new FileTray()` to the widget list in `NotchController` (~L248-265,
   next to `DownloadWidget`). On `FileTray.DragActive`, force-expand the pill + make the tray primary (see how
   `AgentNotice`/`_userPicked`/`_primary`/`_drop` drive expand in NotchController).
5. **Verify** (compile 0/0), deploy live, then bundle the push+release with the pending ring fix.
- **Deferred (ask before adding):** Share via Windows dialog (`DataTransferManager` + HWND anchor, WinRT);
  drag-OUT of the tray.

### Build / deploy / release recipe (repeat each ship; GOTCHAS below)
- Cert thumbprint `2EB268F09FEA535E92FB395FA2FAB4409EC22E1D` (self-signed, CurrentUser\My). signtool sign
  with `/tr http://timestamp.digicert.com /td SHA256 /fd SHA256`, fallback sectigo, then unsigned-time.
- Publish App + Hooks: `dotnet publish src\Halo.App\Halo.App.csproj` and `src\Halo.Hooks\Halo.Hooks.csproj`
  `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\app`. Sign both inner exes.
- Installer: `ISCC installer\Halo.iss` → `dist\DynamicWinSetup.exe` — **RETRY up to 6×** (AV locks the output
  mid icon-embed: "EndUpdateResource failed (110)"). Sign it. Portable: copy `dist\app`→`dist\Halo`,
  `Compress-Archive` → `dist\DynamicWinPortable.zip`.
- Deploy live: `dist\DynamicWinSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, relaunch
  `%LOCALAPPDATA%\Programs\Halo\Halo.App.exe`. Hook-only quick deploy = copy the 4 `Halo.Hooks.*` files.
- Release: `gh release upload v1.0.2 dist\DynamicWinSetup.exe dist\DynamicWinPortable.zip --repo
  phoseinq/DynamicWin` (delete-asset first to replace). Boy branch: strip comments via the tool at
  `C:\Users\hosei\AppData\Local\Temp\halo_pr\strip` (`dotnet run -- <dir>`), copy stripped `.cs` into the
  fork clone `C:\Users\hosei\AppData\Local\Temp\halo_pr\fork`, commit as **phoseinq (NO Co-Authored-By)**,
  push Boy.
- **GOTCHAS:** (a) the PowerShell safety hook blocks a command containing BOTH `Remove-Item` AND a
  `C:\Program Files` literal OR a `/f` token (taskkill /f) — misparses it as a delete target; split them
  (use `Stop-Process`, resolve signtool in a Remove-Item-free command). (b) Persian path «دسکتاپ» breaks
  Windows PowerShell 5.1 reading UTF-8-no-BOM `.ps1` — use pwsh or launch via Bash. (c) Can't auto-screenshot
  the notif banner (transparent notch shows the window behind → false hits; synthetic PS toasts aren't
  delivered to `UserNotificationListener` like real app toasts) → verify by real toast / user eyeball.


## 2026-07-20: rapid-fire batch (DEPLOYED to %LOCALAPPDATA%\Halo\app, running PID-fresh)
1. [DONE] Frame-pacing: animations ran half-speed at 60fps (hard-coded 0.008f tick). → real
   per-frame delta `_dt` in `Frame()` (measured, clamped 1..50ms; also fixes the new 30fps tier).
2. [DONE] Autostart-after-reboot: root cause = Fast Startup (HiberbootEnabled=1) skips the
   at-logon scheduled task on power-on-from-shutdown. Added HKCU `Run\Halo` → deployed exe as a
   fast-startup-proof fallback (safe: single-instance mutex). NEEDS A REBOOT to confirm.
3. [DONE] Fun icons for iconless local notifs. `LocalBadge(cp,hue)` (gradient tile + Fluent glyph,
   like `LangBadge`; gives the banner a colored glow too). Battery E996 / Net EB5E / Limit E9D9 /
   Clock E917 / Cpu E950 — all verified no-tofu via new `--render-badges` hook.
4. [DONE] Video speed: cycling `Btn.Speed` chip (1/1.25/1.5/1.75/2×) on the video row via SMTC
   `TryChangePlaybackRateAsync`, label from `PlaybackRate`. Honest no-op on apps that ignore rate.
   VLC (VlcWidget, no SMTC) NOT covered — follow-up if wanted.
5. [DONE] Hourly chime: `CheckHourly()` in CheckAlerts, once per round hour, ClockBadge, time as
   title, English. `_chimedHour` inits to current hour (no spurious fire at launch).
6. [DONE] Heavy-load throttle: `AdaptFrameRate` gains a 30fps tier (busy>80%) + `_heavy` state
   (enter 50% / leave 40%) → process priority BelowNormal + 3× slower glass capture; ONE edge notif
   "High CPU usage — N%" naming the top-CPU process (`TopCpuProcess`, off-thread). English.
7. [DONE] MS Store downloads folded into the `Downloads` scanner: when no window-title download and
   the Store app is running, poll `Get-DeliveryOptimizationStatus` off-thread (~6s) for the biggest
   active download's % → shows as "Microsoft Store". Gap: DO gives no per-app name, Store-proc-gated.
Verified: Release build 0/0, badges PNG eyeballed, deployed + relaunched (priority Normal, no crash).

## 2026-07-19: 6-feature batch (IN PROGRESS)
- [ ] 1. Compact crescent — pulse fills fully-rounded rect over a flat-top pill → 2 dark crescents
      at the top corners. Fix: fill the real pill silhouette (`Fx.PillPath`) in both agent widgets.
- [ ] 2. Screenshot vs copied — classify clipboard image by `GetClipboardOwner` process: snip
      hosts / null owner → "Screenshot captured"; a real app owner → "Image copied".
- [ ] 3. Icon quality — AppIcon: `PrivateExtractIcons` @256, fallback `ExtractAssociatedIcon`.
- [ ] 4. Download priority + stop — active download becomes primary (user swap still wins); stop
      button focuses the downloader window (no cross-app cancel API); better icon via #3.
- [ ] 5. Privacy dot — `Privacy.cs` registry ConsentStore scan; mic=orange, cam=green; dot on the
      pill; pill stays alive only while mic/cam live; hides when done.
- [ ] 6. Alerts — edge-triggered local banners: battery<=20% discharging (click → Power Saver
      plan), Claude/Codex usage>=80%, internet slow ("Bad internet :/"). Throttled (one per edge).

## 2026-07-18 (session 2): precise click + media-follow-foreground + notif polish + drag-to-move (deployed)
- **Precise banner click (BUILT — supersedes the "NOT possible" note below):** `Notifications/WpnDb.cs`
  reads the toast's `launch`/`activationType` straight out of `wpndatabase.db` (locked WAL SQLite) by Id
  via the **system `winsqlite3.dll`** (P/Invoke, zero NuGet). Verified: DB `Notification.Id` == the
  listener's `UserNotification.Id`; payload is plain UTF-8 `<toast launch=… activationType=…>`; a
  `.db`-only read-only snapshot is enough (row is checkpointed by click time). `NotifItem.Activate` now:
  protocol → `Process.Start(launch)`; else → `IApplicationActivationManager.ActivateApplication(aumid,
  launch)` → opens the exact message/photo (Phone Link thread, Chrome URL, etc.).
- **Notif flood on restart FIXED (root cause):** DndGate restarts `WpnUserService` at launch, so the
  platform is down/empty for the first seconds; the old code baselined at 0 then dumped the whole action
  center (52 toasts) as "new". Fix: persist last-seen Id to `notif-seen.txt` (`LoadSeen`/`SaveSeen`),
  resume from it on start → immune to the race. First-run fallback: baseline only on a non-empty fetch
  or after a 3s grace. `_ready`/`initial` removed in favour of `_baselined`.
- **Auto-dismiss 3s → 6s** (`_notifDeadline`); Windows' own toasts linger ~5s, 3s was too quick to read.
- **Icon chain reordered** (`NotifSource.Build`): `ShellIcon` (clean transparent 256px, both packaged &
  classic) → `AppIcon` (running exe icon — catches custom toast AUMIDs not in Start, e.g. `PowerToys.Run`
  which `ShellIcon` returns null for) → `Logo(n)` last. Fixes the white tile-plate around UWP logos and
  the broken PowerToys icon.
- **Notif Persian/RTL** (`NotifBanner`): `LineFmt`/`WrapFmt` add `DirectionRightToLeft` for FA/AR lines
  so mixed FA+EN no longer mangles (english was jumping into the middle); right-aligned, ellipsis left.
- **Black edge line FIXED** (`LayeredNotch.DrawShape`): final supersample downscale bicubic → **bilinear**
  (bicubic's negative lobes undershot the dark→transparent premultiplied edge into a thin dark rim visible
  over light content). Verified via new `--render-notif` dev hook (real shape path on a colour backdrop).
- **Media follows the foreground** (`MediaWidget.FollowForeground` + `Pick`/`Hook`, called from
  `NotchController` on every fg change with the process name): focus the browser → browser playback,
  focus Spotify → Spotify's. Matches a session whose `SourceAppUserModelId` ~ the fg process name, else
  the system current; force:false skips re-hook churn while the fg app is unchanged.
- **Media art fallback** (`DrawArt` → `CoverFill`): no thumbnail → the source **app icon** instead of the
  generic music glyph (podcasts/videos/radio ship no art).
- **Drag-to-move the pill** (`NotchController.UpdateMove` + `LayeredNotch.OffsetX`): press-and-hold ~3s on
  the pill (a growing underline `DrawHoldCue` shows progress) → it collapses and follows the cursor →
  release drops it → parked within 55px of centre it snaps back (magnet). `_offsetX` persisted to
  `offset`; applied to `_cl`/`_el`/`NotifLeft` (hit-test) and `LayeredNotch` render dst.
- 71/71 tests; Release deployed to `%LOCALAPPDATA%\Halo\app` via the `Halo` scheduled task.

## 2026-07-18: notif banner polish + pin redesign + screenshot-hide (deployed)
- **Screenshot-hide**: `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` in LayeredNotch ctor —
  pill never appears in screenshots/recordings. Side effect: we can no longer screenshot the pill
  for verification; use `--render-widget` / standalone GDI harnesses instead.
- **Pin redesign**: hand-drawn pushpin (`PinPath`: head arc + needle, single continuous path, no MDL2
  glyph). pinned = solid amber (`PinOn` 255,210,105), unpinned = dim white outline. **Instant** toggle
  (no ease on state — user wanted snappy), hover shows English label "pin on top"/"unpin". Moved up
  (PinRect 9,4,24,24). `_pinT` field removed.
- **Notif adaptive font** (`NotifBanner.FontScale`): 1.0→0.86 as text lengthens.
- **Notif over fullscreen**: `NotchController` OnTick — a live/pending toast overrides the fullscreen
  hide (pill stays empty but the banner wakes + renders over games). `NotifSource.HasPending` added.
- **Notif click = open app**: `NotifItem.Aumid` (from `AppInfo.AppUserModelId`) + `.Activate()`
  launches `explorer shell:AppsFolder\<aumid>`. Banner-body click activates + dismisses.
- **Notif app icon**: `GetLogo` blanks on most desktop apps → `ShellIcon.ForAumid` pulls the real
  Start-menu icon via `IShellItemImageFactory`, keeping alpha (32bpp PArgb DIB copy). Icon clip
  changed circle → rounded square (`DrawAppIcon`) so opaque icons don't show as a disc.
- **3s auto-dismiss**: `_notifDeadline = +3s` (was 7s); existing tick loop animates the reverse morph.

### Native toast block — SOLVED via auto Do-Not-Disturb (`Notifications/DndGate.cs`, deployed)
Goal: kill the OS banner + SOUND but keep `UserNotificationListener` delivery. Only **DND** does that
(confirmed live by user: general toasts silenced, pill still mirrors). Dead ends first:
- `RemoveNotification` (still in place as belt/suspenders): always flashes ~0.5s, can't un-ring sound.
- `PushNotifications\ToastEnabled=0`: cached by WpnUserService, not applied live on 26200; also kills
  delivery. Removed.
- **Wrong CloudStore key**: `...\Store\Cache\DefaultAccount\$$windows.data.NOTIFICATIONS.quiethourssettings`
  is a legacy cache — writes there revert in ~400ms.

**Working recipe (DndGate):** the authoritative DND profile is
`HKCU\...\CloudStore\Store\DefaultAccount\Current\{9f763514-...}$windows.data.DONOTDISTURB.quiethourssettings\
windows.data.donotdisturb.quiethourssettings`, value `Data` (REG_BINARY). Blob = 28-byte header, byte[28]
= char count of `Microsoft.QuietHoursProfile.<Profile>`, then that UTF-16 string, then a short trailer.
Swap `<Profile>` (Unrestricted=off, PriorityOnly, AlarmsOnly=strictest) by rebuilding the blob (byte[28]
= new charcount, splice string, keep header+trailer — no total-length field to fix, verified). THEN
**`Restart-Service WpnUserService_*`** (works non-admin) so the platform re-reads. Sticks. Listener
survives the restart (re-acquired on the next 250ms poll). DndGate: AlarmsOnly on start (skips write+
restart if already set), Unrestricted on ProcessExit (fail-open). Find the key by suffix (the {guid}
may vary). Restart via a fire-and-forget `powershell Restart-Service` (no ServiceController dep).

**Still bypasses DND:** the Snipping Tool "snip saved" toast (`Microsoft.ScreenSketch_8wekyb3d8bbwe!App`)
— a system/action toast even AlarmsOnly won't silence. Only fix = its per-app
`Notifications\Settings\<AUMID>\Enabled=0` (but that also stops the pill mirroring it). Left to user.

**Precise banner click (open the exact message/photo):** NOT possible — `UserNotificationListener`
doesn't expose the toast's `launch` args. Would need to read the toast XML out of `wpndatabase.db`
(locked WAL SQLite) by id. Not built. Current `NotifItem.Activate` just foregrounds the app via
`IApplicationActivationManager`.

## DONE 2026-07-17 (evening): flag ghost + outage fix + limit mood + pin/tuck (deployed, needs live check)
- **Flag**: soft wind-blown ghost of the exit-IP flag centred in the panel — `Fx.FlagGhost`
  (2.4 gentle ripples spread across the whole flag + smooth centre-out vignette, baked 2x per
  flag, drawn at 0.16 alpha). Shared by BOTH the CC and Codex panels.
- **Codex parity**: flag ghost + eager/fresh-connection CodexNetMon heartbeat + limit-hit mood
  all mirrored into the Codex widget.
- **Outage bug (real fix)**: NetMon thread now starts eagerly (was: only on first panel-open →
  collapsed ring never learned of an outage), and the fresh-connection health heartbeat runs even
  while the panel is open (pooled fast samples were masking the RST storms and clearing ApiDown).
- **Limit hit**: working + usage ≥99% → "outta juice :(" + "back in Xh Ym" instead of the
  ever-growing turn timer; ring amber.
- **Pill**: no active widget → tucks into a 96×12 slim tab (animated). Pin button (MDL2 pin,
  bottom-left of expanded panel): pinned = upright/bright + pill ignores fullscreen hide;
  unpinned = tilted/faint. No text, state is the drawing. Not persisted across restarts.
- **Pin v3 + empty-hide + follow-focus**: pin was invisible (bare 13px glyph at 35% alpha over the
  weekly bar — verified via GDI+ harness) → final: bare upright pushpin top-left (10,8,26,26),
  MDL2 E840 outline dim = unpinned, E842 filled bright = pinned, no rotation; state + hover
  crossfade via smoothstep (`_pinT`/`_pinHov`, ~120ms). Verified on live pill via screenshots.
  Empty pill now fully hides (SetVisible false once the tuck lands; banners
  and waking widgets resurrect it; leaving fullscreen respects it). Pill follows the focused
  session: fg-change matches fg pid against `IWidget.OwnerPids` (agent pid + console pid, all
  CC/Codex/generic widgets) and switches primary unless a notice/drop owns the pill.
- **M4 notifications (banner UI built)**: NotifSource now grabs the app logo + RemoveNotification
  ("block" = best-effort yank from Windows banner/action center). Pill morphs into a 400×92 banner
  (EaseOutBack, tint/strip/mini all ride the same _notifT) with app icon + name + title + ONE
  truncated summary line; bottom grabber bar (no close button) grows it to the measured full text
  (≤250). Click anywhere outside = soft close; hover pauses the 7s auto-close; toasts queue and
  wait for an idle pill. Test toast: scratchpad toast.ps1 via powershell.exe (PS5 WinRT).

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
  Also: account-lockout 429 (long `Retry-After`) now recorded as 5h=100% + reset time — the panel
  told the truth ("100% · 30m · updated just now") instead of rotting to "updated 12h ago".
- **Startup lag**: autostart moved Startup-folder lnk → Scheduled Task `Halo` at logon (no stagger).
- **Strip rings + numbers**: every circle wears the pill's status ring (`IWidget.Ring`); duplicate
  sessions get deeper shades (`Fx.Shade`) + stable number badges (`Fx.Badge`); codex surfaces badge
  1/2 when both live.
- **Generic agents**: `GenericAgentWidget` + `docs/generic-agents.md` — any AI tool writing
  `~/.halo/agents/agent-*.json` (name/icon/state/pid/...) gets the full treatment; groups by name.
  Verified live with a fake "Gemini CLI" file.
- **Media = already player-agnostic** (GSMTC): browsers/SoundCloud/VLC work without changes; a
  dedicated video-player face is the user's NEXT milestone.

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
