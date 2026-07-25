# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Halo is a Windows "Dynamic Island": an always-on-top layered Win32 window at top-center that morphs
into widgets (media, mirrored notifications, file tray, downloads, Bluetooth battery, live Claude
Code / Codex session panels). C# / .NET 9, drawn entirely with GDI+ and blitted via
`UpdateLayeredWindow`. Unpackaged — no MSIX. Ships publicly as **DynamicWin** from
`phoseinq/DynamicWin`; the code and product are named **Halo**.

`PROGRESS.md` at the repo root is the live session log — reverse-chronological, per-feature root
causes, what is deployed vs. pushed. **Read it at the start of a session and append to it after
significant work.** Design docs live in `docs/` (entry point `docs/MAP.md`); `docs/decisions.md` is
the current architectural truth and supersedes the older Composition-based docs.

Note: the README's "built on Windows.UI.Composition" line is stale marketing copy. Composition was
dropped at P2 (see `docs/decisions.md`); the renderer is `UpdateLayeredWindow` + GDI+.

## Commands

```powershell
dotnet build Halo.sln -c Release            # the bar is 0 warnings / 0 errors
dotnet test tests\Halo.Tests\Halo.Tests.csproj

# one test class / one test
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "FullyQualifiedName~NotchGeometryTests"
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "DisplayName~Collapsed"

# run a dev render hook (see below) instead of launching the pill
dotnet run --project src\Halo.App -- --render-widget out.png claude

# full ship: publish both exes self-contained, sign, Inno Setup, portable zip
pwsh installer\build.ps1

# deploy the built installer over the running install, then relaunch
dist\DynamicWinSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
%LOCALAPPDATA%\Programs\Halo\Halo.App.exe
```

`Halo.App` has `InternalsVisibleTo("Halo.Tests")`, so `internal` helpers are testable with no extra
plumbing. `dist/` is git-ignored. Hook-only quick deploy = copy the four
`Halo.Hooks.{exe,dll,deps.json,runtimeconfig.json}` files into `%LOCALAPPDATA%\Programs\Halo\`.

## Verifying UI changes — you cannot screenshot the app

The window carries `WDA_EXCLUDEFROMCAPTURE`, so any screenshot of the running pill shows the window
*behind* it. That has produced false "it works" conclusions. Use the argv hooks in `Program.Main`,
which render the real code paths to a PNG:

| Hook | Renders |
|------|---------|
| `--render-widget <png> [media\|claude\|codex]` | one widget's expanded panel |
| `--render-notif <png>` | notification banner via the real shape path, colourful backdrop, mixed FA+EN text |
| `--render-pin <png>` | pushpin states |
| `--render-badges <png>` | generated local-notification badges (catches tofu glyphs) |
| `--probe-icon <aumid>` / `--probe-tree <pid>` / `--probe-spectrum` | icon resolvers / process ancestry / loopback audio bands |
| `--restore-notifications` | un-silences every app `BannerGate` learned; the uninstaller calls this |

Env knobs: `HALO_CAPTURABLE=1` disables the capture exclusion (used with ffmpeg `ddagrab` to record
the README gif). `HALO_CLAUDE_SURFACE` overrides CLI-vs-desktop detection in `Halo.Hooks`.
Crash dump: `%TEMP%\halo-crash.log`. Add a new `--render-*` hook whenever you build UI worth eyeballing.

## Architecture

**`Shell/LayeredNotch.cs` — the window.** `WS_EX_LAYERED|TOOLWINDOW|TOPMOST|NOACTIVATE` popup with
`ACCENT_ENABLE_ACRYLICBLURBEHIND` for real frosted glass. Deliberately *not* `WS_EX_TRANSPARENT`, so
it receives mouse input and OLE drops. `Render(w,h,radius,tintAlpha,contentFade)` draws a
per-pixel-alpha GDI+ bitmap (flat top flush to the screen edge, rounded bottom) and blits it.

**`Shell/NotchController.cs` — everything else (~1700 lines, one class by design).** An 8ms
`DispatcherQueueTimer`; `Frame()` measures a real per-frame delta `_dt` (clamped 1–50ms) and
`EaseOutBack`-lerps size/radius/tint/contentFade between collapsed and expanded, then calls `Apply` →
`LayeredNotch.Render`. It also owns widget construction and ordering, primary-widget selection and
`_userPicked`, the swap strip (`ActiveIndices`/`AltIndices`/`Groups`), foreground-follow, click
polling via `GetAsyncKeyState`, the notification banner morph, press-and-hold drag-to-move, pin,
fullscreen hide, `AdaptFrameRate` (fps tiers + `_heavy` → BelowNormal priority), and edge-triggered
local alerts (battery / CPU / RAM / agent-limit / internet / hourly chime, each latched so it fires
once per edge). `NotchVisibility` and `AgentNoticeCoordinator` in the same file are the extracted
pure, unit-tested pieces — put new logic in helpers like those rather than growing `Frame()`.

**`Widgets/IWidget.cs` — the entire widget contract**, leaning on default interface members so a
widget opts into only what it needs: `Icon`/`IconImage`, `IsActive` (only active widgets appear),
`Version` (bump = force a re-render), `Animating` (request continuous frames), `Ring`/`RingProgress`,
`AgentNotice`, `OwnerPids` (focus-follow), `DrawContent` (expanded), `DrawCollapsed` (~220×40 pill),
and `Buttons(w,h)` → `(RectangleF, Action<PointF>)` list in pill-local coordinates — the `PointF` is
what makes seek and volume sliders work.

To add a widget: implement `IWidget`, then register it in the `NotchController` constructor's
`widgets` list — **order matters**, it is the strip and fallback order. Multi-session widgets are
registered one instance per slot (`MediaSessions.MaxSlots`, `StatusStore.MaxSessions`). If it must
steal the pill on an event, drive that through the existing `AgentNotice` / `_primary` / `_drop`
machinery instead of adding an ad-hoc expand path.

**Agents (`ClaudeCode/` + `Codex/`)** are two near-mirrored modules, each with `Status.cs` (file
store), `Limits.cs`, `NetMon.cs` and a cancel path — **a change in one almost always needs the
twin**. `src/Halo.Hooks/` is a small console exe the agents' lifecycle hooks invoke; it writes
`~/.claude/notch/status-{agentPid}.json` (plus `app.json` for the desktop surface; CLI wins when both
are live) and `~/.codex/notch/{desktop,cli}.json`, and implements `cancel <pid>`. Any other tool can
join by writing `~/.halo/agents/agent-*.json` (`docs/generic-agents.md`). `hooks/*.ps1` install the
hooks into the user's live config — the user runs those, not you.

**`Notifications/`** mirrors toasts from WinRT `UserNotificationListener` (works unpackaged),
resolves icons through `ShellIcon` → `AppIcon` → the toast's own logo, and reads each toast's `launch`
args out of the locked WAL `wpndatabase.db` via the system `winsqlite3.dll` so a banner click opens
the exact message. Native banner suppression is `BannerGate` — which lives in the misleadingly-named
`Notifications/DndGate.cs`; it learns per-app AUMIDs, records each app's *original* `ShowBanner`
before writing 0, and is fully reversible.

**`Interop/`** holds all P/Invoke and COM (`Win32.cs` is the big one), plus the OLE drag-drop pieces
for the File Tray. Runtime state persists as loose files under `%LOCALAPPDATA%\Halo\`: `offset`,
`pin`, `tray.txt`, `notif-seen.txt`, `limit-fired`, `notif-debug.txt`.

## Invariants and conventions

- **No new NuGet packages.** `System.Drawing.Common` is the only one; WinRT, Core Audio, OLE, shell
  icons and SQLite are all hand-written interop. Adding a dependency is a decision to raise, not a
  default.
- **Threading:** WinRT/COM events arrive off-thread — update a lock-guarded snapshot and bump
  `Version`; do GDI work only inside `Draw*`. Decode album art lazily in `DrawContent`.
- **Every interop/registry/WinRT call is wrapped in `try { } catch { }`.** A failed probe is normal
  and must degrade silently; nothing may crash the pill, least of all on the per-frame render path.
- **Rendering gotchas that must not be "cleaned up"** (each is a fixed bug): the final supersample
  downscale is *bilinear*, because bicubic's negative lobes undershoot the premultiplied dark→
  transparent edge into a visible dark rim; glow/gradient textures must be **PArgb premultiplied** or
  they spray white garbage onto the layered surface; pill-shaped clips use the flat-top `Fx.PillPath`,
  because an all-corners rounded rect leaves dark crescents at the top.
- **Reuse `Widgets/Fx.cs`** before writing drawing code: accent extraction from an icon, the dithered
  radial glow, `PillPath`, `Shade`, `Badge`, `FlagGhost`, `CleanText` (NFKC), `IsRtl`. RTL text must
  be drawn with `DirectionRightToLeft` + `EllipsisCharacter`, or mixed Persian+English mangles and the
  ellipsis lands on the wrong side.
- **Comments explain the root cause or the failed alternative**, in lowercase prose, often citing what
  was verified live (`// non-premul source on the layered surface sprayed white garbage`). They are
  this project's only record of dozens of dead ends. Don't add comments that restate the code.
- **UI strings are English** (`docs/decisions.md` locks this), lowercase-playful for agent moods
  ("outta juice :(", "googling :P"). Chat and docs may be Persian.
- **Never display invented numbers.** If a value isn't obtainable from the OS, show an indeterminate
  or breathing state — fake percentages have been rejected twice. Likewise, hide a control the
  underlying app can't honor (e.g. SMTC playback rate) rather than shipping a silent no-op.
- Tests are logic-only xunit; there is no UI test harness. Extract pure helpers so behaviour *can* be
  tested, the way `NotchVisibility` and `AgentNoticeCoordinator` were.

## Done means

Release build at 0/0, `dotnet test` green with the count reported, a `--render-*` PNG described for
anything visual, and a dated `PROGRESS.md` entry stating root cause, change, how it was verified, and
**deployed vs. pushed** — those two diverge constantly here, since live hot-swapped DLLs routinely run
ahead of git.

## The public fork is a comment-stripped mirror — mind the trap

`docs/decisions.md` locks "no comments in shipped source". In practice, local `master` is the
comment-bearing truth and the public fork's branch is a mechanically stripped mirror: run the strip
tool, copy the stripped `.cs` into the fork clone, and commit **as phoseinq with no `Co-Authored-By`
trailer** (recipe and paths in `PROGRESS.md`). Consequence worth remembering: **a PR merged on the
fork is not in local `master`, so the next stripped push deletes it.** Contributions must be
back-ported into `master` first.

`installer/build.ps1` does publish → sign → Inno Setup → portable zip in one shot. If ISCC fails with
`EndUpdateResource failed (110)` that is antivirus locking the output mid icon-embed — retry (up to
~6×), it is not a script bug.

## Windows shell traps in this repo

- **Use `pwsh`, not Windows PowerShell 5.1**, for any `.ps1` here — the Persian path segment «دسکتاپ»
  breaks 5.1's UTF-8-no-BOM handling. Running via the Bash tool also works.
- A safety hook blocks a single command containing both `Remove-Item` and a `C:\Program Files`
  literal, or `Remove-Item` and a `/f` token. Split such work across commands; prefer `Stop-Process`
  over `taskkill /f`.
- Sandboxed shells run on an isolated desktop — starting Halo from one makes the pill invisible to the
  real session. Deploy and relaunch from an unsandboxed shell.
- `git ls-files` is the fast way to see real sources; a bare recursive glob drowns in `bin/`, `dist/`
  and `.worktrees/` binaries.

## Serena

This repo is onboarded for Serena, and its code-intelligence tools are the preferred way to read and
edit C# here. Memory graph root is `mem:core`, which links out to `mem:shell/core`,
`mem:widgets/core`, `mem:agents/core`, `mem:notifications`, `mem:dev_hooks`, `mem:shipping`,
`mem:conventions`, `mem:tech_stack`, `mem:suggested_commands` and `mem:task_completion`.
