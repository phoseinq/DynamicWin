# Answerable banner — Claude Code's pending question, decided from the pill

**Status:** design approved 2026-08-01. Not yet implemented.

Halo already knows when Claude Code is waiting on you: the `Notification` hook writes
`status["message"]` and the pill shows an amber ring and "your move ;)". You still have to go find the
terminal. This makes the pill the place you answer, without becoming a place that answers *for* you.

## The constraint that shapes everything

The `Notification` hook is fire-and-forget — its output cannot answer the prompt it announced. The only
supported channel that Claude Code takes an answer from is **`PreToolUse`**, whose stdout may carry
`hookSpecificOutput.permissionDecision` of `allow` / `deny` / `ask`.

Two consequences, both accepted deliberately:

1. **Permission prompts** are answerable exactly and cleanly — `allow` is a real allow.
2. **`AskUserQuestion`** is a tool call, so its options are visible in `tool_input` and can be rendered,
   but a hook cannot return *which option was chosen*. The pick is delivered as a `deny` whose
   `permissionDecisionReason` names the choice. It works; the transcript records a denial. This trade was
   put to the author explicitly and taken.

`PreToolUse` also fires for **every** tool call, not only the ones that would have prompted — Claude
decides that after hooks run. Intercepting naively would raise a banner for the `git status` already on
your allowlist and *increase* the number of things you answer. So the hook reads `permissions.allow` from
settings itself and stays silent on anything that matches.

**The safety rule the whole design hangs on:** the hook returns `allow` only when a human clicked a
button. Silence, timeout, Halo not running, a malformed file, an unparseable rule — all of them mean *no
decision*, which leaves Claude's normal flow exactly as it is today. The worst failure available to this
feature is that the terminal behaves the way it already does.

## Decisions taken

| Question | Answer |
| :-- | :-- |
| What is answerable | permission prompts **and** `AskUserQuestion` |
| If you never click | 20s, then the banner goes and the terminal prompt stands |
| Two sessions asking at once | queued, one banner at a time, FIFO, each labelled with its session |
| Transport | rendezvous files beside the existing `status-*.json`, plus an ack |
| Codex twin | **not** built. Codex has no equivalent decision channel; this is deliberate, not an omission |

Transport was chosen over a named pipe because loose JSON under `~/.claude/notch` is already how every
agent surface in this app talks, it adds no dependency, and it can be debugged with `cat`. A pipe buys
latency that does not matter for something that happens once and waits on a human anyway.

## Components

**`Halo.Hooks`, inside the existing `case "tool"`** (PreToolUse is already registered — see
`hooks/install-hooks.ps1`).

- `AskGate.ShouldAsk(toolName, toolInput, allowRules)` — pure. `AskUserQuestion` with exactly one
  question → true. Any other tool → true only when no allow rule matches. Anything unparseable → false.
- `AskRendezvous.Wait(envelope, timeout)` — writes the ask, waits for the ack, then the answer; returns a
  decision or null.
- Settings are read once per `settings.json` mtime, not once per tool call.

**`src/Halo.App/ClaudeCode/AskStore.cs`** — mirrors `Status.cs`: the same `FileSystemWatcher` plus 1s
safety poll already watching that directory, so this adds no timer and no thread. Owns the queue, writes
`ack` and `answer-*.json`, sweeps expired files.

**`NotchController`** — when `AskStore.Pending` is non-null, raises the existing banner morph with the
option chips as click regions, drawn with `Fx.PillPath` like every other pill-shaped clip here.

The chips are **allow** and **deny** for a permission, and one chip per option for a question. An
"always" chip was sketched early and is deliberately **not** in v1: a hook decision only covers the call
in front of it, so "always" would mean Halo writing rules into the user's own `settings.json` — a
different feature, with a different blast radius, that should be asked for on its own.

## Data flow

```
Claude Code → PreToolUse (Halo.Hooks tool)
  ShouldAsk? no ─────────────────────────→ exit 0, silent
  yes ↓
  write ask-<nonce>.json {pid, session, tool, target, question, options[], expiresAt}
  wait for ack ....... 300ms ── none ────→ exit 0, silent (Halo is not running)
  wait for answer .... 20s ─── none ─────→ delete ask, exit 0, silent
                        ↓ answer
                        print permissionDecision, exit 0

Halo (AskStore, on the frame loop that already runs)
  sees ask-*.json → queue → banner with chips
  click → answer-<nonce>.json → banner dismisses → next in queue
```

## Error handling

- **Halo dies after acking** — the hook waits out its 20s and falls back. The ack means "seen", not
  "guaranteed"; buying more than that is not worth the machinery.
- **Orphaned files** — every ask carries `expiresAt` as **wall-clock UTC**, so a sleeping machine expires
  a question rather than extending it. The pill sweeps on startup and drops anything past its deadline.
- **A click after the hook gave up** — the banner dismisses exactly on the deadline, so the window does
  not exist; orphaned answers are swept after a minute regardless.
- **Half-written files** — write to a temp name and rename (atomic on NTFS); every read in `try/catch`,
  as everywhere else in this codebase.
- **Any exception in the hook** — `exit 0`, no output. A hook may not break Claude Code.
- **Trust** — anything that can write `~/.claude/notch` can forge an answer, but that same thing can
  already rewrite your Claude config. The boundary is unchanged; it is recorded here rather than defended
  against.
- **`AskUserQuestion` carrying 2–4 questions** — not intercepted at all. A 220px pill is not a
  four-question form, and half an answer is worse than none.

## Testing

Logic-only xunit, so the logic is extracted to be testable — the way `NotchVisibility` and
`AgentNoticeCoordinator` were:

- `AskGate.ShouldAsk` over a case table, including the malformed input that must fall to `false`.
- The `permissions.allow` matcher (`Bash(git status:*)`) as its own table — this is where being wrong is
  expensive in both directions.
- `AskEnvelope` round-trip: serialisation, expiry honoured, unknown fields ignored.
- The queue: two pending asks show one at a time, FIFO; an expired head does not block the next.
- **The hook's stdout asserted as an exact string.** It is a contract with an external tool, not an
  internal detail.
- A `--render-ask <png>` dev hook, because the window carries `WDA_EXCLUDEFROMCAPTURE` and anything
  visual that cannot be screenshotted needs a render path or it cannot be reviewed at all.

Not covered, consistent with the rest of the repo: real file-rendezvous timing, COM, and the UI itself.

## Cost

Effectively nothing in steady state. `AskStore` reuses the watcher and 1s poll that already scan that
directory; the hook process already spawns for every `PreToolUse` and gains one mtime-cached settings
read; while a question is pending the hook sleeps rather than spins, and the banner costs what a
notification banner already costs.

Measured on this machine for context: with a live session the pill already sits at 13–27% of one core and
143 MB, driven by the agent widget's animation. That is pre-existing and worth its own look, separately.
