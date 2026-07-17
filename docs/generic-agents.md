# Generic agents — plug ANY AI tool into the notch

Beyond the built-in Claude Code / Codex integrations, any tool can appear in the pill + strip by
writing a small JSON status file. Halo watches the directory live; no restart needed.

## Contract

Write (and keep updated) a file at:

```
%USERPROFILE%\.halo\agents\agent-<anything>.json
```

one file per session, e.g. `agent-gemini-1234.json` (using the tool's pid in the name keeps
parallel sessions apart). Schema:

```json
{
  "name": "Gemini CLI",              // display name; sessions with the same name group together
  "icon": "C:\\path\\to\\icon.png",  // optional: circle icon (png); omit for a generic glyph
  "state": "working",                // working | waiting_input | idle | error
  "currentTool": "search",           // optional: verb shown while working
  "message": "needs your approval",  // optional: one-liner shown in the expanded panel
  "cwd": "C:\\repo",                 // optional
  "pid": 1234,                       // the tool's process id — liveness follows this pid
  "startedAt": "2026-07-17T12:00:00Z", // turn start; drives the elapsed timer
  "updatedAt": "2026-07-17T12:00:05Z"  // bump on every write
}
```

Rules (same engine as the Claude Code integration — `StatusStore`):
- **Liveness = the pid.** Process dead → session disappears. No pid → the file must have an active
  `state` and an `updatedAt` fresher than 30s.
- Delete the file when the session ends (dead-pid files are also swept opportunistically).
- Up to 4 concurrent sessions get widgets; same `name` sessions group under one circle and fan out
  rightward with number badges + status rings.

## What you get

- Collapsed pill face: icon + status ring + `verb · elapsed`.
- Expanded panel: name, state, cwd, message, accent glow from the icon.
- Strip circle with ring, grouping, arrival toss, swap-in — everything the built-ins have except
  agent-specific extras (context bar, usage limits, cancel button).
