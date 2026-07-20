# Halo — Claude Code hooks

`Halo.Hooks.exe` is a tiny helper that Claude Code's hooks call on each event. It writes
`%USERPROFILE%\.claude\notch\status.json`, which the Halo notch watches and renders (usage bars,
activity, Cancel). The same exe also handles `cancel <pid>` (interrupts the running prompt via
`AttachConsole` + Ctrl+C).

## Install
```powershell
pwsh -File hooks/install-hooks.ps1
```
This publishes the helper to `%LOCALAPPDATA%\Halo\hooks\` and merges 7 hooks into
`~/.claude/settings.json` (idempotent; backs up to `settings.json.halo-bak`). Then start a new
Claude Code session.

## What each hook writes
| Event | subcommand | status.json |
|-------|-----------|-------------|
| SessionStart | `session-start` | sessionId, cwd, state=idle, pid/consolePid |
| UserPromptSubmit | `prompt` | state=working, lastPrompt, pid/consolePid, context |
| PreToolUse | `tool` | state=working, currentTool |
| PostToolUse | `tool-done` | context |
| Notification | `notify` | state=waiting_input |
| Stop | `stop` | state=idle, context |
| SessionEnd | `session-end` | state=idle |

`session.contextUsed` is parsed from the transcript (reliable). `usage.fiveHourPct` / `weeklyPct`
(the account rate-limit bars) have **no clean public API** — best-effort / not yet populated by the
helper; the panel shows them when present in the file.

## Uninstall
Restore `~/.claude/settings.json.halo-bak`, or remove the `Halo.Hooks` entries under `hooks`.
