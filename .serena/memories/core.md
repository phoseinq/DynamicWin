# Halo — core

Windows "Dynamic Island" / notch pill: a always-top layered Win32 window at top-center that morphs
into widgets (media, notifications, agents, downloads, file tray, bluetooth…). C# / .NET 9, GDI+ drawn,
no XAML/WinUI/Skia. Unpackaged (no MSIX). Ships as Inno Setup installer + portable zip under the
name **DynamicWin** (repo `phoseinq/DynamicWin`), product/app name **Halo**.

## Source map
- `src/Halo.App/` — the app (`RootNamespace=Halo`, WinExe).
  - `Program.cs` — `[STAThread] Main`: dev/CLI hooks (see `mem:dev_hooks`), single-instance mutex
    `Halo.Notch.SingleInstance`, `OleInitialize`, then `LayeredNotch` + `NotchController` + message loop.
    Crash dump: `%TEMP%\halo-crash.log`.
  - `Shell/` — the window + state machine: see `mem:shell/core`.
  - `Widgets/` — every pill face implements `IWidget`: see `mem:widgets/core`.
  - `Notifications/` — toast mirroring (`NotifSource`, `WpnDb`), icon resolution (`ShellIcon`),
    native-banner suppression (`DndGate.cs`, which holds class **`BannerGate`** — file name is stale),
    `BtBattery` (Bluetooth battery levels).
  - `ClaudeCode/` + `Codex/` — per-agent status/limit/net-health providers (`Status.cs`, `Limits.cs`,
    `NetMon.cs`, `*Cancel.cs`). Parallel, near-mirrored designs; changes to one usually need the other.
  - `Interop/` — all P/Invoke lives here (`Win32.cs` is the big one), plus OLE drag-drop
    (`FileDropTarget`, `FileDrag`), `Clipboard`, `Dispatcher`.
- `src/Halo.Hooks/` — tiny console exe that Claude Code / Codex lifecycle hooks invoke; writes the
  agent status JSON the app watches, and implements `cancel <pid>` (Esc injection).
- `tests/Halo.Tests/` — xunit, logic-only (no UI). `hooks/` — PowerShell installers for the agent hooks.
- `installer/` — Inno Setup script + icon build. `docs/` — spec; entry point `docs/MAP.md`,
  current truth `docs/decisions.md` (supersedes the older Composition-based docs).
- `PROGRESS.md` (repo root) — **the live session log**: reverse-chronological, per-feature root causes,
  what is deployed vs pushed, and the ship recipe. Read it at the start of any session; append to it.

## Invariants
- No new NuGet packages: everything is hand-rolled P/Invoke / COM interop (only `System.Drawing.Common`).
  Adding a dependency is a decision to raise with the user, not a default.
- All rendering is GDI+ into a per-pixel-alpha bitmap blitted with `UpdateLayeredWindow`; WinRT/COM work
  happens off-thread and only mutates lock-guarded snapshots + bumps `Version`. Never touch GDI off the UI thread.
- The window has `WDA_EXCLUDEFROMCAPTURE`, so the pill is invisible to every screen-capture API.
  Verify visuals with the `--render-*` dev hooks instead. `HALO_CAPTURABLE=1` disables the exclusion.
- Comments are stripped before pushing to the public fork — see `mem:shipping`.

## Where to go next
- Language/framework/dependency rules, project layout of the `.sln`: `mem:tech_stack`.
- Code style, the "explain the root cause" comment convention, UX rules (no fake numbers, English strings,
  RTL handling), and the PROGRESS.md workflow: `mem:conventions`.
- Build / test / publish / deploy commands **and the Windows shell traps** (Persian path breaks PS 5.1, the
  `Remove-Item` safety hook, isolated-desktop deploys): `mem:suggested_commands`.
- What must pass before claiming a task is done: `mem:task_completion`.
- How to eyeball UI when the window can't be screenshotted — the `--render-*` argv hooks and env knobs:
  `mem:dev_hooks`.
- Signing, Inno Setup, GitHub release, and the comment-stripped public-fork push: `mem:shipping`.
- The layered window, the 8ms frame loop, and the rendering gotchas that must not be "cleaned up":
  `mem:shell/core`.
- The `IWidget` contract, how to add a widget, and the shared drawing/icon/audio helpers: `mem:widgets/core`.
- Toast mirroring, per-app banner suppression, and the long list of DND dead ends: `mem:notifications`.
- Claude Code / Codex / generic-agent status files, cancel semantics, limits and net-health rules:
  `mem:agents/core`.
