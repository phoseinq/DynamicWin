# Offline Codex Hook Installer Design

**Date:** 2026-07-26

**Status:** Approved

## Goal

Install Halo's Codex lifecycle integration from `DynamicWinSetup.exe` without internet access or
dependencies on the repository, `dotnet`, PowerShell 7, or a separately downloaded component.

## Architecture

`Halo.Hooks.exe`, already shipped self-contained beside `Halo.App.exe`, gains two setup commands:

```text
Halo.Hooks.exe install-codex-hooks <absolute-hook-exe-path>
Halo.Hooks.exe uninstall-codex-hooks
```

The install command owns JSON merge behavior. It reads `%USERPROFILE%\.codex\hooks.json`, preserves
unrelated handlers, removes every stale Halo Codex handler regardless of its old install path, adds
exactly one handler for each supported event, writes a backup, and replaces the file atomically.

The uninstall command removes only Halo Codex handlers. It does not restore the complete backup,
because doing so could erase hooks another tool or the user added after Halo was installed.

## Installer Flow

Inno Setup adds a default-enabled, `checkedonce` task named `codexhooks`. After files are copied it
runs the installed self-contained helper:

```text
{app}\Halo.Hooks.exe install-codex-hooks "{app}\Halo.Hooks.exe"
```

The command is local-only and performs no network access. During uninstall, Inno runs
`uninstall-codex-hooks` before deleting the helper. Upgrades are idempotent and leave one Halo
handler per event.

The repository script `hooks/install-codex-hooks.ps1` remains a developer/manual entry point, but
delegates the merge to `Halo.Hooks.exe` so installer and script behavior cannot drift.

## Events

The installer manages these mappings:

| Codex event | Halo command |
| --- | --- |
| `SessionStart` | `codex session-start` |
| `UserPromptSubmit` | `codex prompt` |
| `PreToolUse` | `codex tool` |
| `PostToolUse` | `codex tool-done` |
| `PreCompact` | `codex pre-compact` |
| `PostCompact` | `codex post-compact` |
| `Stop` | `codex stop` |

`PreCompact` and `PostCompact` retain the `manual|auto` matcher.

## Failure Handling

- Missing settings create a new valid `hooks.json`.
- Missing parent directories are created.
- Malformed existing JSON causes a nonzero setup-command exit and leaves the original untouched.
- The backup is written before replacement.
- A failed temporary write or replace leaves the original untouched.
- Normal lifecycle hook commands continue degrading silently; only explicit setup commands report
  failure to the installer.

## Verification

- Command-level tests cover missing config, preservation of unrelated handlers, stale-handler
  replacement, idempotent reinstall, surgical uninstall, malformed JSON, backup, and exact paths.
- The PowerShell wrapper is tested against a temporary user profile.
- Release tests and solution build must pass with zero warnings/errors.
- Build the signed installer, install it silently with the Codex task enabled, verify all seven live
  handlers target `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe`, then run a temporary CLI hook probe.
- Confirm installer execution requires no network, `pwsh`, repository, or system .NET runtime.
