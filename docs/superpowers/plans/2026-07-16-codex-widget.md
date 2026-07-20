# Codex Widget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a pixel-matched Codex agent widget that supports both Codex Desktop and CLI, prefers Desktop when both run, and preserves Claude Code behavior.

**Architecture:** Codex lifecycle hooks write source-specific status files while a rollout parser enriches them with token and rate-limit data. A broker chooses active Desktop over CLI. `CodexWidget` clones the existing Claude drawing/interaction contract and uses independent OpenAI health monitoring.

**Tech Stack:** C# 13, .NET 9, Win32, GDI+, JSON/JSONL, FileSystemWatcher, xUnit; no new NuGet packages.

## Global Constraints

- Work in `C:\Users\hosei\OneDrive\دسکتاپ\Halo` with local git only.
- Keep Claude Code widget behavior unchanged.
- No private ChatGPT endpoint calls and no fake usage values.
- UI copy is lowercase English except the product title `Codex`.
- Desktop wins over CLI while both are active.
- CLI Stop injects Esc; Desktop Stop is disabled.
- Build and deploy the executable used by Startup: `%LOCALAPPDATA%\Halo\app\Halo.App.exe`.

---

## File map

- Create `src/Halo.App/Codex/Status.cs`: models, rollout parser, candidate liveness, Desktop-over-CLI broker.
- Create `src/Halo.App/Codex/Limits.cs`: last-good rate-limit cache and refresh-by-rescan.
- Create `src/Halo.App/Codex/NetMon.cs`: OpenAI HTTPS health/graph samples, isolated from Claude state.
- Create `src/Halo.App/Widgets/CodexWidget.cs`: all Codex drawing and interaction.
- Create `src/Halo.App/Assets/openai.png`: embedded OpenAI icon.
- Create `hooks/install-codex-hooks.ps1`: safe merge into `~/.codex/hooks.json`.
- Create `tests/Halo.Tests/CodexRolloutTests.cs`: parser and broker tests.
- Modify `src/Halo.Hooks/Program.cs`: explicit `codex` command mode and source-specific output.
- Modify `src/Halo.App/Shell/NotchController.cs`: registration and generic agent notifications.
- Modify `src/Halo.App/Program.cs`: Codex prefetch and render hook.
- Modify `src/Halo.App/Halo.App.csproj`: embedded OpenAI PNG.
- Modify `PROGRESS.md`: completed behavior and verification evidence.

---

### Task 1: Rollout parser and source broker

**Files:**
- Create: `src/Halo.App/Codex/Status.cs`
- Create: `tests/Halo.Tests/CodexRolloutTests.cs`
- Modify: `src/Halo.App/Properties/AssemblyInfo.cs` only if internals are not already visible to tests

**Interfaces:**
- Produces: `CodexSnapshot? CodexRollout.Parse(string path)`.
- Produces: `CodexSnapshot? CodexStatusStore.Current`, `int Version`, `void ForceRefresh()`.
- `CodexSnapshot` includes `Source`, `State`, `CurrentTool`, `StartedAt`, `CompactedAt`, `Message`, `Cwd`, `Pid`, `ConsolePid`, `ContextUsed`, `ContextMax`, `PromptTokens`, `PrimaryLimit`, `SecondaryLimit`, and `UpdatedAt`.

- [ ] **Step 1: Write failing parser tests**

Create sanitized JSONL fixtures inline and assert exact fields:

```csharp
[Fact]
public void Parse_UsesLatestTokenCountAndTaskState()
{
    var path = TempRollout(
        Event("task_started", "{\"model_context_window\":353400}"),
        TokenCount(total: 18420, context: 353400, primaryUsed: 37, primaryWindow: 300, primaryReset: 1784808749),
        ToolCall("functions.exec"));

    var value = CodexRollout.Parse(path)!;

    Assert.Equal("working", value.State);
    Assert.Equal("exec", value.CurrentTool);
    Assert.Equal(18_420, value.ContextUsed);
    Assert.Equal(353_400, value.ContextMax);
    Assert.Equal(37, value.PrimaryLimit!.UsedPercent);
}

[Fact]
public void Select_PrefersActiveDesktopOverCli()
{
    var now = DateTimeOffset.UtcNow;
    var cli = Snapshot("cli", now, alive: true);
    var desktop = Snapshot("desktop", now.AddSeconds(-2), alive: true);
    Assert.Same(desktop, CodexStatusStore.Select(desktop, cli, now));
}

[Fact]
public void Select_FallsBackFromStaleDesktopToCli()
{
    var now = DateTimeOffset.UtcNow;
    var desktop = Snapshot("desktop", now.AddMinutes(-10), alive: false);
    var cli = Snapshot("cli", now, alive: true);
    Assert.Same(cli, CodexStatusStore.Select(desktop, cli, now));
}
```

- [ ] **Step 2: Run tests and verify red**

Run:

```powershell
dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release --filter CodexRolloutTests
```

Expected: compile failure because `Halo.Codex` types do not exist.

- [ ] **Step 3: Implement the parser and broker**

Implement these exact public-internal shapes:

```csharp
internal enum CodexSurface { Cli, Desktop }

internal sealed record CodexLimit(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);

internal sealed record CodexSnapshot(
    CodexSurface Source, string State, string? CurrentTool, DateTimeOffset? StartedAt,
    DateTimeOffset? CompactedAt, string? Message, string? Cwd, int Pid, int ConsolePid,
    long ContextUsed, long ContextMax, long PromptTokens, CodexLimit? PrimaryLimit,
    CodexLimit? SecondaryLimit, DateTimeOffset UpdatedAt, bool ProcessAlive);

internal static class CodexRollout
{
    internal static CodexSnapshot? Parse(string path);
}

internal sealed class CodexStatusStore : IDisposable
{
    internal CodexSnapshot? Current { get; }
    internal int Version { get; }
    internal void ForceRefresh();
    internal static CodexSnapshot? Select(CodexSnapshot? desktop, CodexSnapshot? cli, DateTimeOffset now);
}
```

Parser rules:

```text
task_started                  => working, startedAt from event timestamp
custom_tool_call             => working, map tool name to short verb key
request_user_input/approval  => waiting_input, preserve message when present
task_complete                => idle, clear tool/start
PreCompact/PostCompact files => compacting/compactedAt from hook status
token_count                  => latest model_context_window, total usage, primary/secondary limits
```

`Select` considers a snapshot active when `ProcessAlive` is true or `UpdatedAt >= now - 30 seconds`
and its state is not `ended`. Desktop is evaluated first.

- [ ] **Step 4: Run tests and verify green**

Run the filtered test command; expected all Codex rollout tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Halo.App/Codex/Status.cs tests/Halo.Tests/CodexRolloutTests.cs src/Halo.App/Properties/AssemblyInfo.cs
git commit -m "feat: parse Codex rollouts and select active surface"
```

---

### Task 2: Codex lifecycle hooks and installer

**Files:**
- Modify: `src/Halo.Hooks/Program.cs`
- Create: `hooks/install-codex-hooks.ps1`
- Test: `tests/Halo.Tests/CodexHookTests.cs`

**Interfaces:**
- Consumes: status schema from Task 1.
- Produces: `Halo.Hooks.exe codex <event>` writing `desktop.json` or `cli.json` atomically.

- [ ] **Step 1: Write failing command-level tests**

Run the hooks process with an overridden test directory:

```csharp
[Fact]
public async Task CodexPrompt_WritesSurfaceSpecificWorkingStatus()
{
    var dir = NewTempDirectory();
    var result = await RunHooks("codex prompt", "{\"cwd\":\"C:\\\\repo\",\"prompt\":\"fix it\"}", dir,
        new Dictionary<string,string?> { ["HALO_CODEX_SURFACE"] = "desktop" });
    var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "desktop.json")))!;
    Assert.Equal(0, result.ExitCode);
    Assert.Equal("working", json["state"]!.GetValue<string>());
    Assert.Equal("desktop", json["source"]!.GetValue<string>());
}
```

- [ ] **Step 2: Run and verify red**

Expected: current helper treats `codex` as an unknown command and writes no file.

- [ ] **Step 3: Implement explicit Codex mode**

At argument dispatch:

```csharp
bool codex = args.Length >= 2 && args[0] == "codex";
string cmd = codex ? args[1] : args[0];
string dir = codex ? CodexDir : ClaudeDir;
string path = codex ? CodexStatusPath(DetectCodexSurface()) : ClaudeStatusPath;
```

Detection order:

```text
HALO_CODEX_SURFACE override (tests)
ancestor ChatGPT.exe or codex-code-mode-host.exe => desktop
ancestor terminal host => cli
fallback => cli
```

Map hooks: `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PreCompact`,
`PostCompact`, and `Stop`. Preserve the existing Claude path unchanged.

- [ ] **Step 4: Implement installer merge**

`install-codex-hooks.ps1` must:

```powershell
$settingsPath = Join-Path $env:USERPROFILE '.codex\hooks.json'
Copy-Item $settingsPath "$settingsPath.halo-bak" -Force
$events = [ordered]@{
  SessionStart='session-start'; UserPromptSubmit='prompt'; PreToolUse='tool'
  PostToolUse='tool-done'; PreCompact='pre-compact'; PostCompact='post-compact'; Stop='stop'
}
```

For each event, remove only commands containing `Halo.Hooks.exe" codex `, append the new handler,
and preserve all unrelated handlers. Print a final reminder to review/trust through `/hooks`.

- [ ] **Step 5: Verify tests and a temporary installer fixture**

Run hook tests, then run installer logic against a temporary HOME and assert an unrelated hook remains.

- [ ] **Step 6: Commit**

```powershell
git add src/Halo.Hooks/Program.cs hooks/install-codex-hooks.ps1 tests/Halo.Tests/CodexHookTests.cs
git commit -m "feat: add Codex lifecycle status hooks"
```

---

### Task 3: Rate-limit cache and OpenAI network monitor

**Files:**
- Create: `src/Halo.App/Codex/Limits.cs`
- Create: `src/Halo.App/Codex/NetMon.cs`
- Create: `tests/Halo.Tests/CodexLimitsTests.cs`

**Interfaces:**
- Produces: `CodexLimits.Current`, `LastSuccess`, `Version`, `UpdateFrom(CodexSnapshot)`, `ForceRefresh()`.
- Produces: `CodexNetMon.Poke()`, `Snapshot()`, `ApiDown`, `NetDown`, `Version`.

- [ ] **Step 1: Write failing cache tests**

```csharp
[Fact]
public void MissingLimits_DoNotClobberLastGoodCache()
{
    var store = new CodexLimitsStore(CachePath);
    store.Update(GoodLimits(22, 41));
    store.Update(new CodexSnapshotBuilder().WithoutLimits().Build());
    Assert.Equal(22, store.Current!.Primary!.UsedPercent);
    Assert.Equal(41, store.Current.Secondary!.UsedPercent);
}
```

- [ ] **Step 2: Run red, implement, run green**

Cache schema:

```json
{
  "primary": { "usedPercent": 22, "windowMinutes": 300, "resetsAt": "..." },
  "secondary": { "usedPercent": 41, "windowMinutes": 10080, "resetsAt": "..." },
  "savedAt": "..."
}
```

Only replace a bucket when `0 <= usedPercent <= 100`. Write through `.tmp` + atomic move.
`ForceRefresh()` calls the active status store's rollout rescan and never performs HTTP.

- [ ] **Step 3: Add isolated OpenAI monitor**

Copy the proven cadence/data model from Claude `NetMon` but keep separate static state and use:

```csharp
const string ApiTarget = "https://chatgpt.com/";
const string NetTarget = "https://1.1.1.1/";
```

Do not modify Claude's URLs, arrays, health flags, or cadence.

- [ ] **Step 4: Verify full tests and commit**

```powershell
dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release
git add src/Halo.App/Codex/Limits.cs src/Halo.App/Codex/NetMon.cs tests/Halo.Tests/CodexLimitsTests.cs
git commit -m "feat: cache Codex limits and monitor OpenAI health"
```

---

### Task 4: Codex widget and OpenAI asset

**Files:**
- Create: `src/Halo.App/Widgets/CodexWidget.cs`
- Create: `src/Halo.App/Assets/openai.png`
- Modify: `src/Halo.App/Halo.App.csproj`
- Create: `tests/Halo.Tests/CodexWidgetTests.cs`

**Interfaces:**
- Consumes: `CodexStatusStore`, `CodexLimits`, and `CodexNetMon`.
- Produces: `IWidget` implementation with `Id = "codex"` and `AgentState` metadata used by Task 5.

- [ ] **Step 1: Add OpenAI asset**

Use an official monochrome OpenAI mark, crop to square transparent PNG, and embed:

```xml
<EmbeddedResource Include="Assets\openai.png" LogicalName="Halo.Assets.openai.png" />
```

- [ ] **Step 2: Write failing rendering-state tests**

Tests assert pure helpers rather than pixels:

```csharp
[Theory]
[InlineData("working", "Edit", false, false, "writing…")]
[InlineData("waiting_input", null, false, false, "your move ;)")]
[InlineData("idle", null, false, false, "let's work :)")]
[InlineData("working", null, true, false, "api error :(")]
public void MoodAndVerbMatchClaudeSemantics(string state, string? tool, bool apiDown, bool netDown, string expected)
    => Assert.Equal(expected, CodexWidget.DisplayText(state, tool, apiDown, netDown));
```

- [ ] **Step 3: Implement pixel-matched widget**

Clone the current Claude geometry constants and drawing order exactly. Change only:

```text
title                 Claude Code -> Codex
icon resource         Halo.Assets.claude.png -> Halo.Assets.openai.png
status store          Claude StatusStore -> CodexStatusStore
limits                Claude Limits -> CodexLimits
health graph          Claude NetMon -> CodexNetMon
stop eligibility      source == Cli && consolePid > 0 && state == working
manual refresh        CodexStatusStore.ForceRefresh + CodexLimits.ForceRefresh
```

Expose no values when source fields are missing; rows collapse upward using the same spacing.

- [ ] **Step 4: Add dev render and compare output dimensions**

Add `codex` to `Program.RenderWidget`. Render both agent widgets at 560×220 and verify title/icon
differ while row, graph, stop, and footer rectangles match.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release
git add src/Halo.App/Widgets/CodexWidget.cs src/Halo.App/Assets/openai.png src/Halo.App/Halo.App.csproj src/Halo.App/Program.cs tests/Halo.Tests/CodexWidgetTests.cs
git commit -m "feat: add pixel-matched Codex widget"
```

---

### Task 5: Controller registration and generic agent notifications

**Files:**
- Modify: `src/Halo.App/Widgets/IWidget.cs`
- Modify: `src/Halo.App/Widgets/ClaudeCodeWidget.cs`
- Modify: `src/Halo.App/Widgets/CodexWidget.cs`
- Modify: `src/Halo.App/Shell/NotchController.cs`
- Create: `tests/Halo.Tests/AgentNoticeTests.cs`

**Interfaces:**
- Add to `IWidget`: `AgentNotice AgentNotice { get; }` with default `AgentNotice.None`.
- `AgentNotice` contains `State`, `CompactedAt`, and `Message`.

- [ ] **Step 1: Write failing notification-selection tests**

```csharp
[Fact]
public void WaitingCodex_TemporarilyBecomesPrimaryThenRestores()
{
    var state = new AgentNoticeCoordinator(primary: 0);
    state.Observe(widgetIndex: 2, new AgentNotice("waiting_input", null, "approve?"), Now);
    Assert.Equal(2, state.Primary);
    state.Tick(Now.AddSeconds(7));
    Assert.Equal(0, state.Primary);
}
```

- [ ] **Step 2: Implement generic coordinator**

Replace `_lastCcState` and the index-0 assumption with per-widget previous notice state:

```csharp
private readonly Dictionary<int, AgentNotice> _agentNotices = new();

for (int i = 0; i < _widgets.Length; i++)
    ObserveNotice(i, _widgets[i].AgentNotice, now);
```

Waiting input holds 6 seconds; compact completion holds 4 seconds; restore the pre-notice primary.
Simultaneous notices prefer the currently selected agent, otherwise Desktop-backed Codex, otherwise
the first transition observed.

- [ ] **Step 3: Register widget and prefetch**

Controller order:

```csharp
new MediaWidget(),
new ClaudeCodeWidget(...),
new CodexWidget(codexStore, CancelCodex)
```

Program startup pokes Claude and Codex monitors independently.

- [ ] **Step 4: Run all tests, build, and commit**

```powershell
dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release
dotnet build src\Halo.App\Halo.App.csproj -c Release --nologo
git add src/Halo.App/Widgets/IWidget.cs src/Halo.App/Widgets/ClaudeCodeWidget.cs src/Halo.App/Widgets/CodexWidget.cs src/Halo.App/Shell/NotchController.cs src/Halo.App/Program.cs tests/Halo.Tests/AgentNoticeTests.cs
git commit -m "feat: integrate Codex and generalize agent notices"
```

---

### Task 6: Install, deploy, and visual verification

**Files:**
- Modify: `PROGRESS.md`
- Produce transient screenshots outside git.

**Interfaces:**
- Consumes all prior tasks.
- Produces installed hooks, deployed app, screenshot evidence, and a clean working tree.

- [ ] **Step 1: Install hooks safely**

```powershell
pwsh -File hooks\install-codex-hooks.ps1
```

Verify `~/.codex/hooks.json.halo-bak` exists and unrelated pre-existing hook commands remain.
Open `/hooks` in Codex if the new hashes require trust.

- [ ] **Step 2: Write deterministic test snapshots**

Create source-specific status files and a sanitized rollout fixture for:

```text
desktop working + tool Edit + elapsed timer + 37% primary limit
cli working simultaneously (must lose to Desktop)
desktop idle (mood: let's work :))
```

- [ ] **Step 3: Run final verification**

```powershell
dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release --nologo
dotnet build src\Halo.App\Halo.App.csproj -c Release --nologo
```

Expected: exit 0, no failed tests, no compiler errors.

- [ ] **Step 4: Publish the autostart build**

```powershell
Get-Process Halo.App -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish src\Halo.App\Halo.App.csproj -c Release -o $env:LOCALAPPDATA\Halo\app
Start-Process $env:LOCALAPPDATA\Halo\app\Halo.App.exe
```

- [ ] **Step 5: Capture and inspect required screenshots**

Use `SetCursorPos(1280,18)`, wait two seconds, and `CopyFromScreen(980,0,600,240)` for expanded
states. Move to `(400,600)` for collapsed. Capture:

```text
codex-collapsed-working.png  verb + elapsed timer
codex-expanded.png           context + real available limits + graph
codex-idle-mood.png          let's work :)
codex-desktop-priority.png   both sources active, Desktop selected
```

Open every PNG and verify no clipping, stale Claude branding, fake rows, or enabled Desktop stop.

- [ ] **Step 6: Update progress and commit**

Record exact verification commands/results and remaining limitations in `PROGRESS.md`.

```powershell
git add PROGRESS.md
git commit -m "docs: record Codex widget verification"
git status --short
```

Expected: clean working tree.
