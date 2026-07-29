# Conventions

## Code style
- File-scoped-ish layout: `internal` types by default (`internal interface IWidget`, `internal sealed class …`);
  `public` only where interop or the hook exe needs it. Namespaces mirror folders (`Halo.Widgets`,
  `Halo.Shell`, `Halo.Notifications`, `Halo.ClaudeCode`, `Halo.Codex`, `Halo` for interop/Program).
- Terse names, heavy use of short private fields with `_` prefix (`_notifT`, `_stripT`, `_shrink`), one-line
  expression-bodied helpers (`Lerp`, `SmoothStep`, `Max`, `InRect`), `const` blocks for tunables at the top
  of a class (`CollapsedW`, `OpenSeconds`, `WaitGraceMs`). Animation state is a `float 0..1` named `*T`.
- Nullable is on; use `?` + `try { } catch { }` around every interop/registry/WinRT call. Failure of a
  probe is normal and must degrade silently, never crash the pill.
- 4-space indent, allman braces, but single-line `if (x) return;` guards are idiomatic here.

## Comments — the distinctive convention
Comments explain **why / the root cause / the failed alternative**, in lowercase prose, often citing what
was verified live. e.g. `// Windows won't deliver the snip toast → mirror it from the clipboard`,
`// non-premul source on the layered surface sprayed white garbage`. Keep writing them this way: they are
the project's only record of dozens of dead ends. Do **not** add restating-the-code comments.
Comments are stripped mechanically before pushing to the public fork (`mem:shipping`), so the local
`master` history is the comment-bearing copy.

## Product / UX conventions
- All user-facing strings are **English**, lowercase-playful for agent moods ("outta juice :(",
  "googling :P", "back in Xh Ym"). Text rendering must handle Persian/RTL (`Fx.CleanText` + `Fx.IsRtl`).
- Never display invented numbers. If a value isn't obtainable, show an indeterminate/breathing state.
  The user has explicitly rejected fake percentages twice.
- Hide a control the underlying app can't honor instead of showing a silent no-op.
- Instant feedback beats easing for discrete toggles (the pin was deliberately de-animated).

## Process
- `PROGRESS.md` is the session log: append a dated section per work batch, stating root cause, what was
  changed, what was **verified how**, and whether it's deployed vs pushed. Read it first, update it after
  each significant step.
- Tests are logic-only xunit; UI is verified through the `--render-*` hooks (`mem:dev_hooks`).
  Extract pure helpers (like `NotchVisibility`, `AgentNoticeCoordinator`) so behaviour *can* be tested.
- Feature work of any size goes through a brainstorm → spec → plan cycle; artefacts land in
  `docs/superpowers/specs/` and `docs/superpowers/plans/` (dated filenames), roadmaps in `docs/plans/`.
