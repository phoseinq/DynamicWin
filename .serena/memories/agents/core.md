# Agent integration (Claude Code, Codex, generic)

Two near-mirrored modules, `ClaudeCode/` and `Codex/`, each with `Status.cs` (file-watching store),
`Limits.cs` (usage/rate-limit windows), `NetMon.cs` (independent connectivity health graph) and a cancel
path. **A change in one almost always needs the twin** — the widgets are deliberately symmetric.

## Data flow
- `src/Halo.Hooks/Program.cs` is invoked by the agent's lifecycle hooks and writes JSON the app watches:
  - Claude Code → `~/.claude/notch/status-{agentPid}.json` per session (pid stamped on **every** event:
    a mid-turn-born file without a pid evades dedupe), plus `app.json` for the desktop surface.
    session-end deletes its file; session-start sweeps dead-pid files. `HALO_CLAUDE_SURFACE` overrides
    CLI-vs-desktop detection; **CLI wins when both are live**.
  - Codex → `~/.codex/notch/{desktop,cli}.json`; live state/context/model window/real rate-limit windows
    additionally come from Codex's **rollout JSONL**, opened with shared access so an active session stays
    readable. Ceiling: N Codex CLI sessions still share `cli.json`.
  - Any other tool → `~/.halo/agents/agent-*.json` (name/icon/state/pid/…) drives `GenericAgentWidget`;
    schema documented in `docs/generic-agents.md`.
- `StatusStore` / `CodexStatusStore` scan those files into stable slots (`MaxSessions = 4`, per-pid dedupe
  keeps the freshest) with `FileSystemWatcher` + version poll. `IsLive`/`SessionLive(slot)` are cached ~1s
  because they're hit per frame. A dead/reused pid makes the widget inactive.
- Hook installers: `hooks/install-hooks.ps1` (Claude, merges hooks into `~/.claude/settings.json`) and
  `hooks/install-codex-hooks.ps1`. The user runs these against their live config; backups are written
  (`~/.codex/hooks.json.halo-bak`, `settings.json.bak-*`). The CC hooks point at
  `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe`.

## Semantics worth preserving
- Ring/mood colour: working **with** a tool = green, working with no tool = **amber ("thinking")**,
  idle = white, api error / outage = red. `tool-done` clears `currentTool` to make thinking real. The
  expanded panel's dot must come from the same `RingColor(st)` as the collapsed ring — hardcoding it
  desyncs them (fixed bug, don't regress).
- Cancel = inject **Esc** into the session (`WriteConsoleInput` / SendInput), never Ctrl+C to the process
  group — Ctrl+C closes the user's terminal. Codex Desktop is Electron: `PostMessage` never lands, so it's
  restore-if-iconic + `SetForegroundWindow` + `SendInput(Esc)`.
- Compacting: shown as a whole-pill breathing fill + elapsed; percent is paced against the *previous*
  compact's real duration (`lastCompactMs`), because true progress is unknowable. Esc-cancelled compacts
  fire no hook → detected by polling VK_ESCAPE while state=compacting with an agent host focused, plus a
  3-min expiry, and self-heal on the next `PostCompact`.
- Limits: 5-minute heartbeat timer (not panel-open-only, or the panel rots to "59m ago"); an account-lockout
  429 with a long `Retry-After` is recorded as 100% + reset time. At ≥99% while working the pill says
  "outta juice :(" + "back in Xh Ym" instead of a growing turn timer.
- `NetMon`: threads start **eagerly** at boot (not on first panel-open, or the collapsed ring never learns
  of an outage), and the fresh-connection heartbeat keeps running while the panel is open (pooled fast
  samples masked RST storms). Any HTTP 5xx — including 529 Overloaded — counts as **Lost**, not healthy.
- Agents only show while their app is actually running (Codex needs desktop/CLI presence, Claude a live
  pid); "limits without a session" applies while the app is open but idle, not when closed.
