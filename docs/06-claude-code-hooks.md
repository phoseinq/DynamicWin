# 06 — Claude Code: status file + hooks (concrete)

The bridge is `Halo.Hooks.exe`, invoked by Claude Code hooks. It writes one status file that the
notch watches. Authoritative for schema and the Cancel mechanism.

## status.json
Path: `%USERPROFILE%\.claude\notch\status.json`. Written **atomically** (temp file + rename) so the
watcher never reads a half-written file.
```json
{
  "sessionId": "…",
  "cwd": "C:\\path\\to\\project",
  "state": "working | idle | waiting_input",
  "pid": 4321,
  "consolePid": 8899,          // terminal process, for Cancel (see below)
  "currentTool": "Edit",       // from PreToolUse; cleared on Stop
  "lastPrompt": "…",           // truncated
  "session": { "contextUsed": 84213, "contextMax": 200000 },
  "usage": {                   // best-effort, may lag or be estimated
    "fiveHourPct": 0.42, "fiveHourResetsAt": "2026-07-13T18:00:00Z",
    "weeklyPct": 0.13,   "weeklyResetsAt": "2026-07-19T00:00:00Z"
  },
  "updatedAt": "2026-07-13T14:22:05Z"
}
```

## Hooks → helper mapping
Each hook passes Claude Code's event JSON on stdin (`session_id`, `cwd`, `transcript_path`,
`hook_event_name`, plus event fields). The helper merges into status.json.

| Hook event | Helper call | Writes |
|-----------|-------------|--------|
| SessionStart | `Halo.Hooks.exe session-start` | init file, sessionId, cwd, consolePid, state=idle |
| UserPromptSubmit | `Halo.Hooks.exe prompt` | state=working, lastPrompt, refresh consolePid, recompute usage |
| PreToolUse | `Halo.Hooks.exe tool` | state=working, currentTool=tool_name, heartbeat |
| PostToolUse | `Halo.Hooks.exe tool-done` | heartbeat, recompute session.contextUsed from transcript |
| Notification | `Halo.Hooks.exe notify` | state=waiting_input |
| Stop | `Halo.Hooks.exe stop` | state=idle, clear currentTool, recompute usage |
| SessionEnd | `Halo.Hooks.exe session-end` | state=idle (or remove file) |

- **session.contextUsed** = sum token counts from the session JSONL at `transcript_path`
  (usage fields on the last assistant turn). `contextMax` from the model id (lookup table).
- **usage.*** (5h/weekly): best-effort estimate from cumulative token/cost in the transcript, or a
  cached value. Marked best-effort in decisions.md. Context is the reliable bar; ship that first.

## settings.json snippet (installed into ~/.claude/settings.json)
```json
{
  "hooks": {
    "SessionStart":    [{ "hooks": [{ "type": "command", "command": "Halo.Hooks.exe session-start" }] }],
    "UserPromptSubmit":[{ "hooks": [{ "type": "command", "command": "Halo.Hooks.exe prompt" }] }],
    "PreToolUse":      [{ "hooks": [{ "type": "command", "command": "Halo.Hooks.exe tool" }] }],
    "PostToolUse":     [{ "hooks": [{ "type": "command", "command": "Halo.Hooks.exe tool-done" }] }],
    "Notification":    [{ "hooks": [{ "type": "command", "command": "Halo.Hooks.exe notify" }] }],
    "Stop":            [{ "hooks": [{ "type": "command", "command": "Halo.Hooks.exe stop" }] }],
    "SessionEnd":      [{ "hooks": [{ "type": "command", "command": "Halo.Hooks.exe session-end" }] }]
  }
}
```
Installer adds `Halo.Hooks.exe` to PATH (or uses an absolute path) and merges this block without
clobbering existing hooks.

## Capturing the terminal for Cancel
The helper records `consolePid` — the terminal process hosting Claude Code — by walking the parent
chain from itself up to the terminal (WindowsTerminal.exe / conhost / pwsh host). `GetConsoleWindow()`
is unreliable under Windows Terminal (returns a hidden pseudo-console window), so we key Cancel off
**pid**, not HWND.

## Cancel mechanism (real stop, no focus steal)
Primary: Halo spawns a short-lived `Halo.Hooks.exe cancel <pid>` which does
`AttachConsole(pid)` → `GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)` → `FreeConsole`, then exits.
This delivers Ctrl+C to Claude Code without stealing window focus, and runs in a throwaway process
so it can't disturb Halo's own state. A single Ctrl+C interrupts the current prompt.

Fallback: if Ctrl+C exits Claude Code instead of just interrupting, switch to focus + Esc
(`SetForegroundWindow` the terminal window found from pid → `SendInput` Esc → restore focus).
`ponytail: ship AttachConsole+Ctrl+C first; only add the Esc/focus fallback if Ctrl+C proves wrong.`
