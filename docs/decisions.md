# Decisions

## Locked
- **Stack:** C# + WinUI 3 (Windows App SDK) + **Composition API** for shell animation and glass.
  Chosen because compositor-thread animation runs at the monitor's real refresh rate and real
  acrylic blur fixes the "not smooth / low-res LED look / not glassy" complaints about DynamicWin.
- **Claude Code bar scope:** *Everything* — account usage limit (5h + weekly), session context,
  live activity, and Cancel.
- **Cancel = real stop.** The button interrupts the running Claude Code prompt, not just closes the
  panel. v1 = spawn a helper that does `AttachConsole` + `GenerateConsoleCtrlEvent(CTRL_C_EVENT)` (no
  focus steal); fallback = focus + Esc if Ctrl+C exits CC instead of interrupting. See 05/06.
- **UI language:** English everywhere in the app. (Chat/docs may be Persian.)
- **No comments in shipped source.** Write with none from the start; a strip pass runs before any
  GitHub push. `ponytail:` markers are the only comments allowed during dev and get stripped too.
- **Default collapsed-pill bar:** session **Context %** (always accurate). The 5h-limit view is
  configurable. Reason: context is a reliable number; account-limit % is best-effort (see below).

## Open / to refine
- **Account usage-limit %** (5h / weekly) has no clean public API. It's the one *best-effort* data
  source — estimated from transcript/cost. Context % is solid; ship that first, refine limit later.
- Multi-monitor follow behavior (stay on primary vs. follow mouse) — default primary, decide in P5.
- Third-party widget DLL hot-load — deferred until someone actually ships one (P2 ships compiled-in).

## Working name
**Halo** — rename freely; it's only in namespaces/paths so far.
