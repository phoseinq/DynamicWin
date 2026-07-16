# Prompt for Codex — build the Codex widget in Halo

Copy-paste everything below this line into Codex.

---

You are working in `C:\Users\hosei\OneDrive\دسکتاپ\Halo` — a from-scratch Windows "Dynamic
Island" notch app (C# / .NET 9, Win32 layered window + GDI+, no WPF/WinUI). It already has a
finished **Claude Code widget**. Your job: build the **same widget for Codex CLI (ChatGPT)** —
pixel-for-pixel the same UI and behavior, only the data sources and the icon change.

## Read these first, in order

1. `docs/claude-code-widget.md` — the complete spec of the widget you are cloning: data flow,
   status.json schema, ring color semantics, collapsed-pill zones + text-emerge animation,
   moods/emoticons, limit bars (color lerp, hover), network graph (dual HTTPS series — read the
   "why not ICMP/TCP" rationale), usage-endpoint hard rules (429 backoff, never-clobber, disk
   cache, spam guard), heartbeat flags, Apple-style notification. **Its final section
   "Porting notes for a Codex widget" is your task list.**
2. `docs/MAP.md` then `docs/decisions.md` — repo layout + architecture ground truth.
3. Source of the widget you're cloning:
   - `src/Halo.App/Widgets/ClaudeCodeWidget.cs` (all drawing/interaction)
   - `src/Halo.App/ClaudeCode/Status.cs`, `Limits.cs`, `NetMon.cs`
   - `src/Halo.Hooks/Program.cs` + `hooks/install-hooks.ps1`
   - `src/Halo.App/Shell/NotchController.cs` (widget registration `_widgets[]`, notification
     auto-expand `_noticeUntil`, click routing `Buttons(w,h)`)

## What to build

1. **`src/Halo.App/Codex/`** — mirror of `ClaudeCode/`: a `StatusStore` reading
   `~/.codex/notch/status.json`, a `Limits` for ChatGPT-plan usage, a `NetMon`-equivalent is
   NOT needed (reuse the existing `NetMon`, just add an OpenAI api series or make the api URL
   per-widget — your call, keep it simple).
2. **Status source**: investigate what Codex CLI actually exposes on this machine
   (`codex --help`, `~/.codex/config.toml`, `~/.codex/sessions/*.jsonl`, `~/.codex/auth.json`).
   Use its `notify` hook if present; otherwise derive state from the newest session JSONL
   (mtime + last entry type) and process liveness. Fill the same schema Claude uses
   (`state`, `currentTool`, `startedAt`, `message`, `session.contextUsed/contextMax/promptTokens`,
   `pid`, `consolePid`) — degrade gracefully for fields Codex can't provide (widget already
   hides what's missing).
3. **Usage limits**: find the endpoint Codex's `/status` command hits (token in
   `~/.codex/auth.json`). Apply ALL the hard rules from the spec: GET-only, never clobber good
   values, 429 → 2 min backoff, disk cache `%LOCALAPPDATA%\Halo\codex-usage-cache.json`,
   refresh-on-open with the >2-opens-per-minute cache guard, manual `⟳ refresh` line. If no
   endpoint exists, hide the bars (do NOT fake numbers).
4. **`src/Halo.App/Widgets/CodexWidget.cs`**: clone ClaudeCodeWidget's layout exactly —
   circular icon + muted status ring (same color semantics), balanced collapsed zones,
   text-emerge animation keyed on the verb, the same moods (`let's work :)`, `your move ;)`,
   `api error :(`, `offline :(`, `outta juice XD`), context/limit bars with the smooth
   blue→amber→red lerp + hover detail, stop-circle button (Ctrl+C via
   `Halo.Hooks.exe cancel <pid>` — works for any console CLI), net graph with the api series
   pointed at `https://chatgpt.com/` (keep the HTTPS-RTT approach), `updated Xm ago · ⟳ refresh`.
   Icon: embed an OpenAI logo PNG as `Halo.Assets.openai.png` (same embedding pattern as
   `claude.png` in the csproj).
5. **Register it**: add to `NotchController._widgets[]`. The swap-circle/dropdown UI already
   handles multiple active widgets. Extend the notification auto-expand to fire for whichever
   agent widget enters `waiting_input` (it's currently keyed to index 0 — generalize it).

## Rules

- Match the existing code style: file-scoped namespaces, ponytail-minimal (no interfaces with
  one impl, no config for constants), comments only where something is non-obvious.
- Don't touch the Claude widget's behavior. Shared helpers may be extracted only if the diff
  stays small and both widgets use them identically.
- No new NuGet packages.
- All UI text English, lowercase minimal style.

## Build / deploy / verify (must actually do this)

```powershell
dotnet build src\Halo.App\Halo.App.csproj -c Release
# deploy (what autostart runs):
Get-Process Halo.App -EA SilentlyContinue | Stop-Process -Force
dotnet publish src\Halo.App\Halo.App.csproj -c Release -o $env:LOCALAPPDATA\Halo\app
Start-Process $env:LOCALAPPDATA\Halo\app\Halo.App.exe
```

Verify with a real screenshot (primary screen is 2560 wide; notch is top-center):
move the cursor to (1280,18) to expand, wait ~2s, `CopyFromScreen(980,0 → 600x240)` and look
at the PNG. Cursor to (400,600) to collapse. There's also a dev hook:
`Halo.App --render-widget out.png <name>` (add a `codex` case to `Program.RenderWidget`).
To test status transitions without a live Codex session, write
`~/.codex/notch/status.json` by hand (or via your status writer) and watch the pill react.

Don't claim done without: clean build, deployed, and screenshots of (a) collapsed pill with a
verb + timer, (b) expanded panel with bars/graph, (c) the mood state `let's work :)`.
