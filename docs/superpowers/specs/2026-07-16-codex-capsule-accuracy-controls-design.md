# Codex Capsule Accuracy and Controls Design

**Date:** 2026-07-16

**Status:** Approved for specification by the user

## Goal

Make the Codex capsule feel concise and alive while ensuring every displayed context and plan-limit value is traceable to fresh Codex data. Make Stop work for both Codex CLI and Codex Desktop, and prevent repeated refreshes or cache writes from spamming local resources.

## Scope

This change includes:

- Creative English activity text for the collapsed Codex capsule.
- Detection of the real nested operation inside Codex `exec` tool calls.
- Model-aware context capacity and accurate latest-call token usage.
- Freshness metadata for context and plan-limit values.
- A working Stop path for Codex CLI and Codex Desktop.
- Cooldowns for Stop and manual plan-limit refresh.
- Deduplicated, non-expired plan-limit caching.
- Automated tests, Release verification, deployment, and collapsed/expanded screenshots.

It does not add a private usage API, modify the Codex installation, or attempt to connect to the Desktop app's private stdio app-server connection.

## Evidence and Corrections

The active session currently reports:

- Model: `gpt-5.6-sol`
- Model metadata: `372,000` raw tokens with `95%` effective context
- Effective context emitted by Codex: `353,400`
- Latest inference: `122,271` input plus `1,457` output, totaling `123,728`
- Cached input: `121,600`, which is already part of input and must not be added again
- Cumulative thread usage: `94,070,036`, which is not context occupancy
- Plan window: `10,080` minutes, therefore Weekly

The existing context parser prefers `last_token_usage.total_tokens`, but falls back to `total_token_usage.total_tokens`. That fallback can put a cumulative multi-million-token value into a per-call context bar and must be removed.

The existing Desktop Stop path is explicitly disabled. CLI Stop injects Escape into a console, but Desktop has no console. The Desktop app exposes a top-level packaged `ChatGPT.exe` window, while its accessibility tree does not expose a stable Stop element. Its existing app-server uses stdio owned by the Desktop process and cannot be safely joined by Halo.

The existing limits facade performs duplicate rescans on a manual click, rescans whenever the panel opens, and can save the same cache value repeatedly while the panel renders. These are local disk/CPU problems; Halo does not call a remote usage endpoint for Weekly data.

## Architecture

New behavior is split into focused files so future work does not require rereading the large widget or status-store files:

- `Codex/CodexActivityText.cs` owns operation classification and user-facing English copy.
- `Codex/CodexDesktopCancel.cs` owns packaged-window discovery and Desktop Escape delivery.
- `Codex/CodexRefreshGate.cs` owns cooldown decisions and remaining-wait text.
- `Codex/Status.cs` remains responsible for rollout parsing and store selection, but only receives the minimal fields needed for model, token freshness, and nested operation input.
- `Codex/Limits.cs` remains responsible for accepted/cached limits, with deduplicated writes and expiration.
- `Widgets/CodexWidget.cs` only renders the prepared data and delegates behavior.

No source comments are added to the new implementation files. Names and tests carry the intent.

## Context Data Contract

### Capacity

`ContextMax` comes from the latest `token_count.info.model_context_window` event for the selected rollout. This is the effective context Codex selected for the active model, so Halo does not maintain a hardcoded model table. A model switch updates the value on the next `token_count` event.

The model slug comes from the latest rollout event that carries `model`. It is presentation metadata and never overrides `model_context_window`.

### Used tokens

`ContextUsed` comes only from `token_count.info.last_token_usage.total_tokens` in the same event as `ContextMax`. If either field is absent, the context row is hidden until a complete token event arrives.

`total_token_usage` is never used for context. `cached_input_tokens` is never added to input or total. Halo may validate that input plus output equals total when all three are present, but the emitted total remains authoritative.

The value means "tokens processed by the latest model inference." It updates after each inference and can temporarily lag newly produced tool output until the next model call. The UI must not claim sub-call instantaneous occupancy.

### Freshness and rendering

The snapshot stores the timestamp of the token event separately from lifecycle `UpdatedAt`. A new task shows no inherited context before its first complete token event. Switching to another rollout cannot retain context from the previous rollout.

The expanded row displays one decimal place where useful, for example `123.7K / 353.4K`, and includes the model display name when available. Its freshness text uses the token timestamp.

## Weekly and Plan-Limit Contract

Rate-limit buckets continue to come from rollout `rate_limits` events:

- `300` minutes renders as `5-hour limit`.
- `10,080` minutes renders as `Weekly limit`.
- Other positive windows render as `Plan limit`.

The rollout watcher remains the primary update path and is not throttled. Manual refresh and panel-open refresh share one 30-second gate and perform one status-store rescan. Repeated clicks inside the gate do no work and render a short `refresh in Ns` status.

The cache writes only when accepted bucket data or its source timestamp changes. A bucket whose reset time has passed is discarded on load and is not rendered. A missing or malformed update preserves a still-valid bucket but cannot refresh its age.

The UI displays source freshness, not render time. Opening the panel cannot make an old value look new.

## Desktop and CLI Stop

### CLI

CLI behavior remains console Escape injection using the existing helper and `ConsolePid`.

### Desktop

Halo discovers a visible top-level window owned by the installed Codex package. Accepted targets must have a process name of `ChatGPT` or `Codex`, a nonzero main window handle, and an executable path belonging to the OpenAI Codex package. Halo posts one Escape key-down/key-up pair directly to that window without activating it.

The Desktop Stop button is enabled only while the selected Desktop snapshot is `working` and a valid target window exists. A one-second gate prevents duplicate Escape pairs. Failure to find or post to a valid target leaves the app running and returns the button to its enabled state; Halo never kills the Desktop process.

Direct window messaging is preferred over UI Automation because the current app exposes no stable named Stop element. A separate app-server is not used because it cannot interrupt a turn owned by the Desktop app's existing stdio server.

## Capsule Copy

The parser inspects the `input` string on `custom_tool_call` events whose outer name is `exec`. It extracts nested `tools.<name>` calls without retaining arguments. Multiple nested operations render a general parallel-work phrase.

Primary phrases are:

| State or operation | Capsule text |
| --- | --- |
| no current operation while working | `thinking in diffs…` |
| `apply_patch` or file edit | `shaping code…` |
| command execution | `running commands…` |
| file search or read | `tracing the code…` |
| image inspection | `checking pixels…` |
| web lookup | `checking the web…` |
| plan update | `mapping the route…` |
| skill loading | `loading a playbook…` |
| waiting on a running command | `letting it cook…` |
| multiple concurrent operations | `juggling a few things…` |
| compacting | `packing context…` |
| waiting for user input | `your move ;)` |
| idle | `ready when you are :)` |
| just compacted | `fresh context :)` |
| API failure | `api's having a moment :(` |
| network failure | `connection ghosted :(` |

Copy stays lowercase, brief, and English. Factual values such as model, tokens, percentages, reset times, and elapsed time are not made playful.

## Error Handling

- Malformed or partially written rollout lines are ignored without replacing the last complete event.
- Missing complete token data hides the context row.
- Expired limit cache data is hidden.
- A throttled refresh keeps current data and exposes the remaining cooldown.
- Desktop Stop never falls back to terminating a process.
- CLI Stop remains unavailable without a valid console PID.

## Testing

Tests are written before implementation and cover:

- A complete token event publishes model, used, max, and token timestamp.
- Cumulative usage alone cannot publish context.
- Model/context changes replace the prior pair atomically.
- Cached input is not double-counted.
- Nested operation extraction and every public capsule phrase.
- Refresh admission, cooldown, remaining time, and automatic watcher bypass.
- Identical limit observations do not rewrite cache.
- Expired cached buckets do not load.
- Desktop target validation rejects unrelated windows.
- Desktop Stop posts one Escape pair and respects its cooldown.
- CLI Stop routing remains unchanged.

Final verification requires the full Release test suite, a warning-free Release build, deployment to `%LOCALAPPDATA%\Halo\app`, live-process restart, a collapsed screenshot, an expanded screenshot, and confirmation that the displayed model/context/Weekly values match the latest rollout event.

## Success Criteria

- The collapsed capsule uses the new creative text and reflects the nested operation instead of showing `exec…`.
- Context capacity follows the model metadata emitted by Codex without a hardcoded table.
- Context used equals the latest inference total and never cumulative thread usage.
- Weekly values update through the watcher and repeated manual refreshes do not rescan or rewrite.
- Stop interrupts both CLI and Desktop turns without closing either application.
- Missing, stale, or expired data is clearly absent or aged rather than presented as current.
