# MAP — read this first, then open only the one file you need

Each doc is small and self-contained. Don't load them all. Use this table to jump.

| Doc | What's inside | Open it when… |
|-----|---------------|---------------|
| [decisions.md](decisions.md) | Locked choices (stack, cancel semantics, defaults) + open questions | You need to know "what did we already decide / why" |
| [01-overview.md](01-overview.md) | Vision, the 3 DynamicWin problems we fix, scope, non-goals | Onboarding, or checking if something is in scope |
| [02-architecture.md](02-architecture.md) | Notch window, state machine, threading, **code/folder layout** | Placing a new file, or wiring the shell |
| [03-widget-system.md](03-widget-system.md) | `IWidget` contract, `WidgetHost`, **how to add an app** | Building or adding any widget/app |
| [04-rendering-glass.md](04-rendering-glass.md) | Springs, backdrop blur, anti-banding, collapsed↔expanded interpolation | Working on P1 / anything visual or smoothness |
| [05-claude-code.md](05-claude-code.md) | Claude Code feature: data flow, notch UI, Cancel design | Building the CC widget (notch side) |
| [06-claude-code-hooks.md](06-claude-code-hooks.md) | `status.json` schema, hook events, script sketches, settings snippet | Wiring the CC side (hooks + status file) |
| [07-build-phases.md](07-build-phases.md) | P0→P6 order + done-criteria per phase | "Where are we / what's next" |
| [08-config-packaging.md](08-config-packaging.md) | Config file, autostart, single-instance, packaging, comment-strip-before-push | P6, or shipping |
| [09-reference-dynamicwin.md](09-reference-dynamicwin.md) | DynamicWin findings: why it's low-res (Skia), widget model, media done right, verified toolchain | Before P5 media, or "why WinUI not Skia" |
| [bug-reports.md](bug-reports.md) | Crash + manual report delivery: the allowlisted payload, why email is not the transport, opt-in send | Touching crash handling, or anything that leaves the machine |

## Plans (executable, step-by-step)
| Plan | Covers |
|------|--------|
| [plans/2026-07-13-p0-p1-shell.md](plans/2026-07-13-p0-p1-shell.md) | P0+P1: layered window, system-Composition host, glass pill, springs, anti-banding |

## Routing shortcuts
- **"Make it smoother / glassier"** → 04, then 02.
- **"Add an app"** → 03 (contract), 07 (which phase).
- **"Claude Code bar / cancel"** → 05 (design) + 06 (concrete schema & scripts).
- **"Where do I put this file?"** → 02 (layout section).
- **"What did we decide about X?"** → decisions.md.

Keep this table in sync when a doc is added or renamed.
