# 05 — Claude Code widget (notch side)

Concrete schema + hook scripts live in [06](06-claude-code-hooks.md). This doc is the design.

## Data flow
```
Claude Code (any terminal)
   │  hooks fire on prompt/tool/stop/notification
   ▼
Halo.Hooks.exe  ──writes──►  %USERPROFILE%\.claude\notch\status.json
                                        │  FileSystemWatcher (debounced)
                                        ▼
                              ClaudeCodeWidget  →  bars + activity + peek
                                        │  Cancel
                                        ▼
                     focus status.consoleHwnd → send Esc  (real stop)
```

## What it shows
- **Collapsed pill:** one thin bar (default = session Context %) + a pulsing dot:
  green = working, amber = waiting for input, dim = idle.
- **Expanded panel:**
  - `Session context 42%` bar.
  - `5-hour limit 42% · resets in 1h20m` and `Weekly 13%` bars (best-effort — see decisions.md).
  - Activity line: current tool / `lastPrompt` (e.g. "Editing main.cs…").
  - **Cancel** button (English).

## Attention behavior
- `state = waiting_input` (Claude needs a permission / answer) → widget raises `RequestAttention`
  → shell goes to **Peek** so you notice. This is the first real user of that event (03).

## Cancel = real stop
- Read `pid` / `consolePid` from status.json (see 06 for how they're captured).
- v1: spawn `Halo.Hooks.exe cancel <pid>` → `AttachConsole` + `GenerateConsoleCtrlEvent(CTRL_C_EVENT)`
  → interrupts the running prompt, **no focus steal**. Full mechanism + fallback in 06.
- If pid is missing/stale → Cancel disabled.

## Refresh
- FileSystemWatcher on status.json, debounced ~150ms. On change: parse, update view models.
- Usage-limit numbers may update less often (they come from a poller in the hook helper) — that's
  fine; context/state update instantly.

## Failure modes to handle
- status.json absent → widget shows "No active Claude Code session", Cancel disabled.
- Malformed/partial write → ignore and keep last good (helper writes atomically: temp file + rename).
- Stale file (updatedAt old) → treat as idle.
