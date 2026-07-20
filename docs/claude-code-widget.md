# Claude Code widget — full spec (as shipped 2026-07-16)

This documents everything the Claude Code panel in Halo does and exactly how, so an equivalent
widget can be built for another agent CLI (e.g. Codex). Source files:

- `src/Halo.App/Widgets/ClaudeCodeWidget.cs` — all drawing + interaction
- `src/Halo.App/ClaudeCode/Status.cs` — status model + `StatusStore` (FileSystemWatcher)
- `src/Halo.App/ClaudeCode/Limits.cs` — real 5h/weekly usage fetch + cache + spam guard
- `src/Halo.App/ClaudeCode/NetMon.cs` — connectivity graph sampling + always-on health flags
- `src/Halo.Hooks/Program.cs` — tiny exe the CLI's hooks call; writes `~/.claude/notch/status.json`
- `hooks/install-hooks.ps1` — merges the hook entries into `~/.claude/settings.json`

## Data flow

```
Claude Code CLI ──hooks──▶ Halo.Hooks.exe ──▶ ~/.claude/notch/status.json
                                                   │ FileSystemWatcher
Anthropic API (usage + latency probes) ──▶ Limits / NetMon (static classes)
                                                   ▼
                                          ClaudeCodeWidget (GDI+ draw)
```

### status.json (written by Halo.Hooks.exe)

Hook events → fields:

| event (argv[0]) | effect |
|---|---|
| `session-start` | `sessionId`, `cwd`, `state=idle`, records `pid`/`consolePid`; `source=clear/startup` drops the stale `session` (context numbers) |
| `prompt` | `state=working`, `lastPrompt` (truncated 120), `startedAt`=now (turn start), `message=null`, context update |
| `tool` | `state=working`, `currentTool` = tool name |
| `tool-done` | context update from transcript |
| `notify` | `state=waiting_input`, `message` = what Claude asks (truncated 160) |
| `pre-compact` | `state=compacting`, `startedAt`=now (drives the elapsed timer) |
| `post-compact` | `compactedAt`=now, `lastCompactMs`=duration; `trigger=auto` → `state=working` (mid-turn), else `state=idle`; context update |
| `stop` | `state=idle`, clears `currentTool`/`startedAt`/`message`, context update |
| `session-end` | same as stop |

Compact end has two real edges: the **`PostCompact` hook** (Claude Code ≥2.1.x) and
**`SessionStart` with `source=compact`** on the session restart — both write `compactedAt`.
A **cancelled** compact (Esc) fires nothing; that's what the widget's 3-minute expiry covers.

Context update parses the CLI transcript (`transcript_path` from hook stdin JSON, JSONL):
- `contextUsed` = latest `input_tokens + cache_read_input_tokens + cache_creation_input_tokens`
- `contextMax` by model family (Opus/Fable/Sonnet → 1M, Haiku → 200K)
- `promptTokens` = sum of `input + cache_creation + output` for every entry with
  `timestamp >= startedAt` (this turn's own consumption; cache reads excluded)

Cancel: `Halo.Hooks.exe cancel <pid>` = `AttachConsole(consolePid)` + `GenerateConsoleCtrlEvent(CTRL_C)`
— interrupts the running prompt exactly like pressing Esc/Ctrl+C in that terminal.

### Two surfaces: CLI and the desktop app (CLI wins)

The hook exe splits Claude sessions by surface: a session with a **terminal ancestor**
(Windows Terminal, conhost, pwsh, VS Code, …) writes `status.json`; one without (the Claude
desktop app's engine) writes `app.json` (override with `HALO_CLAUDE_SURFACE=cli|app`).
`StatusStore` reads both and selects: **live CLI → CLI; else live app → app**. Liveness =
pid alive (start time checked against `updatedAt` to catch pid reuse) or an active state
updated <30s ago. Selection is memoized for 1s so per-frame reads don't hammer process
lookups.

## Collapsed pill (220×40)

- **Circular icon** (Claude logo, clipped to a circle) at left, with a **status ring**:
  thin (1.9px), muted (55% alpha). Colors mirror the CLI spinner, except its normal orange →
  **green** (orange would vanish against the icon): green = running a tool, **amber** = deep
  thinking (working with `currentTool` empty) or `waiting_input`, **red** = outage
  (`NetMon.ApiDown/NetDown`), white = idle.
- **Balanced zones**: activity verb hugs the icon (left-aligned), elapsed timer owns the right
  edge (dimmer, 13px). Verb font shrinks to fit its zone so nothing ever overflows.
  Mood lines (idle) are centred but leaned toward the icon (zone narrowed ~34px).
- **Text-emerge animation**: when the verb *changes*, it slides out from behind the icon
  (16px slide + fade, easeOutCubic ~0.3s, clipped so it's "born" from the icon). The per-second
  timer tick does NOT retrigger it (animation keys on the verb only). Needs `IWidget.Animating`
  = true while animating so the controller renders ~30fps during it.
- **Verbs** (lowercase, minimal): writing… / reading… / running… / digging… / fetching… /
  googling :P / delegating… / planning… / using a skill… / asking you :) / hmm… (no tool).
- **Moods**: idle → `let's work :)`; `waiting_input` → `your move ;)`; 5h limit ≥95% →
  `outta juice XD`; API unreachable → `api down :(`; internet dead → `offline :(`;
  within 20s after a compact finished (`compactedAt`) → `compacted :)`.
- **Outage override**: mid-work, if health flags trip, the verb is replaced by
  `api error :(` / `net error :(` and the ring goes red.
- **Compacting state** (`state=compacting`): verb `compacting…`, ring and panel dot turn
  **blue**, and the whole pill background **breathes blue** — a rounded-rect fill (radius
  h/2) whose alpha oscillates 0.05→0.16 on a 2.4s cosine loop; the right zone shows
  `~42% · 31s`. The percent is **openly approximate** (no real signal exists — even Claude
  Code's spinner only shows a token counter hooks never see): elapsed ÷ the LAST compact's
  duration (`lastCompactMs`, recorded by the post-compact hook; 60s default), clamped 1–99
  so it never claims done. **Cancel detection**: an Esc-cancelled compact fires no hook, so
  the controller watches `GetAsyncKeyState(VK_ESCAPE)` while `state=compacting` and the
  foreground window belongs to a terminal/agent host — a hit marks that compact (keyed by
  `startedAt`) cancelled and the pill drops back to the idle mood instantly. A wrong guess
  self-heals: post-compact still fires on real completion. Backstop: `compacting` older
  than 3 min is ignored anyway. `IWidget.Animating` true while genuinely compacting.

## Expanded panel (560×220)

Header: state dot (11px, aligned to title cap height) + "Claude Code" 21px + activity line
(verb · elapsed). While `waiting_input` with a `message`, the activity line shows the actual
question in amber.

Rows (bar = label left, value right, 6px rounded track):
1. **Context** — `usedK / 1M` (or 200K), blue fill, fraction = contextUsed/contextMax.
2. **5-hour limit** — real percent + `resets 4h 21m`.
3. **Weekly limit** — same for the 7-day bucket.

Limit bar color blends smoothly (lerp, no steps): ≤50% blue → 50–75% blue→amber → 75–100%
amber→red. Hovering a bar row swaps its value for the precise form:
`34.2% · resets Thu 18:09` (local time).

Bottom-right: `updated 3m ago · ⟳ refresh` — clickable (ForceRefresh), brightens on hover.

**Stop button**: small red circle (34px) top-right with a rounded-square stop glyph; red +
clickable only while a prompt is running (`state=working && pid>0`), else dimmed.

**Network graph** (left of stop button, 26px gap): two REAL end-to-end HTTPS series
(24 samples, 5px apart, 22px tall, L-frame axes labeled `0`/`cap`):
- `net` (green) = `https://1.1.1.1/` — your internet via Cloudflare edge
- `api` (blue) = `https://api.anthropic.com/` — the actual path to Anthropic

Why HTTPS and not ICMP/TCP-connect: local TUN/proxy layers answer TCP instantly (measured
1 ms fake) and can eat ICMP; an HTTP response can't be faked locally. A keep-alive HttpClient
(`PooledConnectionLifetime` 5 min) makes every sample after the first a single true RTT
(first sample shows the TLS handshake spike). Y-scale is dynamic: `max(150, ceil(max/50)*50)`.
Lost samples pin to the top and paint that stretch of the line **red**. Legend
`net 116 · api 177 ms` is color-coded; a lost side shows `:(`.

Hover on the graph: dotted guide line at nearest sample, markers on both series, tooltip box:
sample values, `loss net a/24 · api b/24`, targets, plus a diagnosis line when relevant —
`Anthropic's side :(` (api lost, net fine) or `your internet :(` (net lost).

## Limits (real 5h/weekly usage)

`GET https://api.anthropic.com/api/oauth/usage` with headers
`authorization: Bearer <accessToken>` (from `~/.claude/.credentials.json` → `claudeAiOauth`)
and `anthropic-beta: oauth-2025-04-20`. **Zero token cost** (no inference). Response:
`five_hour.utilization` (percent float), `five_hour.resets_at` (ISO), same for `seven_day`.
This is the exact source the claude.ai usage page reads — numbers match the site.

Robustness (all learned the hard way):
- **Never clobber good data**: only assign when utilization ≥ 0 (a 429/error response used to
  blank the bars).
- **429 handling**: the endpoint itself rate-limits if hammered → back off 2 min, keep old values.
- **Spam guard**: refresh on panel open, but >2 opens within 60s → serve cache until data is
  5+ min old; manual `⟳ refresh` bypasses (still ≥5s between calls).
- **Disk cache** `%LOCALAPPDATA%\Halo\usage-cache.json` (values + savedAt) so restarts never
  start blank; `LastSuccess` drives the "updated Xm ago" label.

## NetMon health heartbeat

Graph sampling runs only while the panel is open (Poke keeps an 8s window, 700ms cadence,
both probes concurrently). When collapsed, a **10s heartbeat** still probes the api URL
(and 1.1.1.1 only if api failed) to set `ApiDown` / `NetDown` — this is what turns the ring
red and swaps the collapsed text mid-work. Flag changes bump a Version counter → immediate
re-render.

## Notification (Apple-style)

Two triggers, same mechanism (controller auto-expands the pill with the hover easing, holds,
collapses back; if another widget was primary it temporarily switches to Claude Code and
restores after):

- `state → waiting_input` transition: holds **6s**, panel shows the question in amber.
- fresh `compactedAt` (changed and <30s old): holds **4s**, pill/panel show `compacted :)`
  (the mood stays for 20s). A state transition alone is NOT enough — leaving `compacting`
  without a new `compactedAt` means the compact was cancelled, and announcing success
  there would be a lie.

See `NotchController`: `_noticeUntil`, `_lastCcState`, `_noticeRestore`; expand condition is
`hovered || notice`.

## Porting notes for a Codex (ChatGPT) widget

Same widget shell, swap the data sources:

1. **Status/hooks**: Codex CLI has a `notify` hook (`~/.codex/config.toml`) and writes session
   rollout JSONL under `~/.codex/sessions/`. Map its lifecycle to the same status.json schema
   (`state`, `currentTool`, `startedAt`, `message`, token usage from the rollout file). If its
   hook surface is thinner than Claude Code's, degrade: state machine from notify + process
   watching (codex.exe alive/CPU) still covers working/idle/waiting. If its compaction
   lifecycle is detectable (session file events / notify), map it to `state=compacting` +
   `compactedAt` and the sweep/notification come for free.
2. **Limits**: find the ChatGPT-plan usage endpoint Codex's `/status` uses (OAuth token in
   `~/.codex/auth.json`). Same rules apply: GET-only, never clobber, 429 backoff, disk cache,
   spam guard.
3. **Net graph**: identical, just point the api series at `https://chatgpt.com/` or
   `https://api.openai.com/` (keep HTTPS-RTT approach — same anti-fake reasoning).
4. **Cancel**: same AttachConsole+Ctrl+C trick works for any console CLI given its console PID.
5. Keep every UI element identical (ring semantics, zones, emerge animation, moods, graph,
   refresh UX) — only the icon (OpenAI logo) and brand accents change.
