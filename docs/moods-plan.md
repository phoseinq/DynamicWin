# The pill's voice — next pass

> **Shipped 2026-07-30.** All of it, plus two things this plan did not know about: the reason the pill only
> ever said one line (a hold that slid on every read, so nothing ever expired) and the same signals driving
> the ring's colour. `PROGRESS.md` has the outcome; this file is kept for what was intended beforehand.

Written 2026-07-29, to be picked up in a later session. Current state: `src/Halo.App/Agents/Moods.cs`
holds a hand-written table, **47 keys / 279 lines**, average 11.8 chars, ceiling 22. Slots resolve
through three duration bands (`@long` past 2 min, `@ages` past 8 min). No network, no subprocess, no
tokens — and that stays true here.

## Two things to change

1. **More situations, from data Halo already has.** Right now only elapsed time modifies a line.
2. **A voice with something behind it.** Trade-and-kitchen metaphors — the agent as somebody working
   with their hands — instead of generic wit. Fixing a fridge, cooking, laying bricks, plumbing.
   Those are *directions*, not lines to copy; the point is that "kettle's on…" tells you it will be
   a while in a way "still running…" does not.

## Signals available honestly

Every one of these is already on screen or already in the snapshot. Nothing here is invented, which
is the rule that killed the API version: if a value cannot be read, the modifier simply does not apply.

| Signal | Where it comes from | Suffix |
|---|---|---|
| turn elapsed | `StartedAt` → `Running(st)` | `@long`, `@ages` *(done)* |
| context nearly full | `Session.ContextUsed / ContextMax` ≥ 0.80 | `@tight` |
| usage window nearly spent | `Limits.FiveHour` ≥ 0.90 (`CodexLimits` on the twin) | `@thin` |
| big turn | `Session.PromptTokens` above a threshold | `@heavy` |
| same tool over and over | count `CurrentTool` transitions per `StartedAt` | `@again` |
| wall clock | `DateTime.Now.Hour` — 00–05 and 05–08 read differently | `@late`, `@early` |
| outage / no credit | `NetMon`, `Limits` | already their own slots |

Deliberately **not** used: `Cwd`. Naming the user's project on a pill that ends up in screenshots is
a privacy leak for a joke.

## Design — one modifier, by priority

Stacking suffixes would explode combinatorially and most pairs read badly anyway. Pick exactly one,
by fixed precedence, most urgent first:

```
outage/credit (own slots) > @tight > @thin > @ages > @long > @again > @heavy > @late/@early > plain
```

Resolution walks *down* that list until a key exists, so a slot needs only the sets worth writing and
everything else falls through to the plain wording. That is the same rule the bands already follow.

## Steps

1. `readonly record struct MoodContext(TimeSpan? Running, float ContextFrac, float UsageFrac,
   long PromptTokens, int ToolRuns, int LocalHour)` — built by each widget from what it already holds.
   `default` must mean "know nothing", so every field is optional and negative/zero = unknown.
2. `internal static string Modifier(in MoodContext ctx)` — **pure**, returns the suffix to try, or "".
   This is the whole of the new logic and the only part that needs real tests.
3. `Line(string slot, in MoodContext ctx)` — resolve `slot + Modifier(...)`, fall down the priority
   list, then plain. Keep the existing `Line(slot)` and `Line(slot, TimeSpan?)` delegating so no call
   site has to change in one go.
4. Counting `ToolRuns`: the widget already sees `CurrentTool` change per frame. Hold
   `(startedAt, lastTool, count)` and bump on a transition — no new plumbing, and it resets itself
   when the turn's stamp changes.
5. Write the sets. Only where the metaphor earns its place; a half-hearted `@heavy` set is worse than
   none because it dilutes the plain one.
6. Tests: a precedence table for `Modifier`, plus the existing width / ascii / duplicate / band-has-a-
   base guards, which already cover anything new automatically.
7. Both widgets, same change — the Codex twin is not optional.

## Voice sketches

Only to set the tone. Keep them short; the table averages twelve characters for a reason.

- `running@ages` — "kettle's on…", "low and slow…", "still simmering…"
- `running@again` — "same drill…", "round three…"
- `digging` — "torch and gloves…", "under the floor…", "behind the panel…"
- `patching` — "duct tape…", "wd-40 moment…", "bit of filler…"
- `writing` — "laying bricks…", "mixing cement…", "measuring twice…"
- `planning` — "envelope maths…", "chalk on the wall…"
- `compacting` — "reducing it…", "boiling down…"
- `unknown@tight` — "running out of desk", "no room to think…"
- `idle@late` — "still up?", "night shift", "burning oil"
- `idle@early` — "kettle first", "morning", "coffee then work"
- `@thin` — "rationing now…", "last of the tank…"

## Also owed on the same files

`CodexWidget` still has no `TurnOver` twin, so cancelling a Codex turn leaves the pill stuck exactly
the way the Claude one did. Fix that in the same pass — the Claude implementation is
`ClaudeCodeWidget.TurnOver` plus `Shown(st)` at each display site, and `NotchController.CancelCodex`
and `DetectAgentCancel` are where the latch gets set.
