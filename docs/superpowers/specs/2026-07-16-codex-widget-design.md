# Codex widget design

## Goal

Add a Codex agent widget to Halo that matches the shipped Claude Code widget's layout, animation,
health states, usage presentation, and notification behavior. It supports both Codex Desktop and
Codex CLI. When both are active, Desktop is displayed.

The Claude Code widget remains behaviorally unchanged.

## Local capability findings

- Installed Codex is `0.144.4`.
- Codex supports lifecycle hooks from `~/.codex/hooks.json`, including `SessionStart`,
  `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PreCompact`, `PostCompact`, and `Stop`.
- Rollout JSONL files under `~/.codex/sessions/` contain `task_started`, `task_complete`, tool calls,
  and `token_count` events.
- `token_count` exposes `model_context_window`, total/last token usage, and real plan rate-limit
  windows in `rate_limits.primary` and optional `secondary`.
- Desktop and CLI both write rollouts. Process ancestry distinguishes `ChatGPT.exe`/app-hosted Codex
  from a terminal-hosted `codex.exe`.
- External stop is safe for CLI through console Esc injection. No documented safe external stop
  mechanism exists for Codex Desktop, so its stop control stays disabled rather than killing the app.

## Architecture

### Status files

Extend `Halo.Hooks.exe` with an explicit Codex mode:

```text
Halo.Hooks.exe codex session-start
Halo.Hooks.exe codex prompt
Halo.Hooks.exe codex tool
Halo.Hooks.exe codex tool-done
Halo.Hooks.exe codex pre-compact
Halo.Hooks.exe codex post-compact
Halo.Hooks.exe codex stop
```

The writer determines the source from process ancestry and writes atomically to one of:

```text
~/.codex/notch/desktop.json
~/.codex/notch/cli.json
```

Each file uses the existing agent schema: state, currentTool, startedAt, compactedAt, message, cwd,
pid, consolePid, updatedAt, contextUsed, contextMax, promptTokens, and rate limits.

### Session broker

`Codex/Status.cs` owns a `StatusStore` with three responsibilities:

1. Watch the two status files and the newest rollout files.
2. Enrich hook state from rollout events with context, token, model, and rate-limit data.
3. Select the visible candidate using this order: active Desktop, active CLI, none.

A candidate is active when its process is alive or its rollout has a running task and was updated
recently. Stale files do not keep the widget visible.

Hook data is authoritative for immediate lifecycle state. Rollout data is authoritative for token
and rate-limit values. Missing values remain absent and the UI hides their rows.

### Usage limits

Use rollout `token_count.payload.rate_limits` as the primary source. This is the same data Codex
shows in `/status` and `/usage`, avoids private endpoint discovery, does not expose OAuth tokens,
and cannot trigger HTTP 429s.

Persist the last valid values to `%LOCALAPPDATA%\Halo\codex-usage-cache.json`. New parse failures or
missing fields never overwrite good cached values. Manual refresh rescans the newest active rollout;
it does not make a network request. If no valid limit data exists, limit rows are hidden.

### Widget

`Widgets/CodexWidget.cs` clones the Claude Code widget's visual structure and interaction geometry:

- OpenAI logo in the collapsed circular icon.
- Identical muted status ring semantics and text-emerge animation.
- The same verbs and moods, with the title changed to `Codex`.
- Context and available plan-limit bars with identical color interpolation and hover detail.
- Network graph with `net = https://1.1.1.1/` and `api = https://chatgpt.com/`.
- `updated Xm ago · ⟳ refresh` rescans rollout/cache data.
- CLI stop uses `Halo.Hooks.exe cancel <consolePid>`.
- Desktop stop is visibly disabled and has no destructive fallback.

Embed `Assets/openai.png` as `Halo.Assets.openai.png` using the existing Claude asset pattern.

### Network monitor

Make the existing HTTPS monitor target configurable through a small shared monitor instance. The
Claude instance keeps its current Anthropic URL and behavior unchanged; the Codex instance uses
ChatGPT. Both retain independent samples and health flags.

### Controller integration

Register `CodexWidget` alongside Media and Claude Code. The existing active-widget dropdown handles
selection. Generalize agent notification tracking so any registered agent widget entering
`waiting_input`, or completing compaction, can temporarily become primary and auto-expand. Restore
the previous primary widget afterward.

## Installation

Add `hooks/install-codex-hooks.ps1`. It publishes `Halo.Hooks`, backs up
`~/.codex/hooks.json`, merges Halo handlers without removing unrelated hooks, and leaves Codex's
trust workflow intact. Changed hooks may require approval through `/hooks` before they run.

The legacy `notify` command remains untouched because it is already used by Codex Desktop tooling.

## Error handling

- Atomic status and cache writes.
- Malformed or partially-written JSON is ignored without clearing the last good snapshot.
- Rollout watcher retries after file-sharing failures.
- Missing auth, rate limits, session fields, icons, or console handles degrade by hiding only the
  unavailable control or row.
- Desktop always wins only while genuinely active; stale Desktop data immediately falls back to CLI.

## Verification

- Unit tests for rollout parsing, rate-limit mapping, stale-candidate rejection, and Desktop-over-CLI
  priority.
- Hook writer tests with sanitized Desktop and CLI payloads.
- Release build and existing Halo tests.
- Dev renderer gains `--render-widget <out.png> codex`.
- Screenshot verification:
  1. collapsed Codex pill with verb and elapsed timer;
  2. expanded Codex panel with context, available limits, and network graph;
  3. idle mood `let's work :)`;
  4. both Desktop and CLI active, proving Desktop selection;
  5. CLI stop enabled and Desktop stop disabled.
- Publish to `%LOCALAPPDATA%\Halo\app`, launch that deployed executable, and verify the running copy.

## Non-goals

- No private ChatGPT endpoint reverse engineering when rollout data already contains real limits.
- No fake usage values.
- No force-killing Codex Desktop to simulate Stop.
- No unrelated Claude Code rendering or behavior changes.
