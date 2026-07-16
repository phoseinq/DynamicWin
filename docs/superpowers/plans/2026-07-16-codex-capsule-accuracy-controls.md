# Codex Capsule Accuracy and Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep Codex Desktop visible while its app runs, make Stop work on Desktop and CLI, and ship accurate model-aware context, creative activity copy, and throttled Weekly usage.

**Architecture:** Add small Codex-specific runtime, cancellation, copy, and refresh units. Keep rollout watching in `CodexStatusStore`, but normalize Desktop presence from the real packaged process and accept context only from complete latest token events.

**Tech Stack:** C# 13, .NET 9, Win32 `PostMessage`, GDI+, xUnit, local Git.

## Global Constraints

- Windows-only, no new NuGet dependency.
- UI copy remains brief, lowercase, and English.
- No new source comments.
- No private usage API; Weekly values come from local rollout events.
- Desktop cancellation never terminates a process.
- All behavior changes begin with a failing focused test.
- Deploy to `%LOCALAPPDATA%\Halo\app` after a clean Release build and tests.

---

### Task 1: Desktop presence and Stop hotfix

**Files:**
- Create: `src/Halo.App/Codex/CodexDesktopRuntime.cs`
- Create: `src/Halo.App/Codex/CodexDesktopCancel.cs`
- Modify: `src/Halo.App/Codex/Status.cs`
- Modify: `src/Halo.App/Widgets/CodexWidget.cs`
- Modify: `src/Halo.App/Shell/NotchController.cs`
- Test: `tests/Halo.Tests/CodexDesktopTests.cs`
- Test: `tests/Halo.Tests/CodexRolloutTests.cs`

**Interfaces:**
- Produces: `CodexDesktopPresence(bool Running, DateTimeOffset StartedAt)`.
- Produces: `CodexDesktopRuntime.Presence` and `CodexDesktopRuntime.TryCancel()`.
- Consumes: `Func<CodexDesktopPresence>` in `CodexStatusStore`.

- [ ] **Step 1: Write failing presence and cancellation tests**

```csharp
[Fact]
public void DesktopPresence_KeepsQuietRolloutIdleWhileAppRuns()
{
    var now = DateTimeOffset.UtcNow;
    var rollout = Snapshot(CodexSurface.Desktop, now.AddMinutes(-2), alive: false);
    var value = CodexStatusStore.NormalizeDesktop(
        rollout, new CodexDesktopPresence(true, now.AddHours(-1)), now);
    Assert.Equal("idle", value!.State);
}

[Fact]
public void DesktopPresence_DropsSnapshotWhenAppStops()
{
    var now = DateTimeOffset.UtcNow;
    Assert.Null(CodexStatusStore.NormalizeDesktop(
        Snapshot(CodexSurface.Desktop, now, false), new(false, default), now));
}

[Fact]
public void DesktopCancel_PostsOneEscapePair()
{
    var posted = new List<uint>();
    var window = new CodexDesktopWindow(
        "ChatGPT",
        @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__test\app\ChatGPT.exe",
        new IntPtr(42),
        DateTimeOffset.UtcNow.AddHours(-1));
    var runtime = new CodexDesktopRuntime(
        () => [window],
        (_, message, _, _) => { posted.Add(message); return true; },
        () => DateTimeOffset.UtcNow);
    Assert.True(runtime.TryCancel());
    Assert.Equal(new uint[] { 0x0100, 0x0101 }, posted);
}
```

- [ ] **Step 2: Run the focused tests and confirm RED**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter "FullyQualifiedName~CodexDesktop|FullyQualifiedName~DesktopPresence" --nologo`

Expected: compilation failure because the runtime and normalization APIs do not exist.

- [ ] **Step 3: Implement packaged-app presence and Escape delivery**

```csharp
internal readonly record struct CodexDesktopPresence(bool Running, DateTimeOffset StartedAt);
internal sealed record CodexDesktopWindow(
    string ProcessName, string ExecutablePath, IntPtr Handle, DateTimeOffset StartedAt);

internal sealed class CodexDesktopRuntime
{
    internal CodexDesktopRuntime(
        Func<IReadOnlyList<CodexDesktopWindow>> scan,
        Func<IntPtr, uint, IntPtr, IntPtr, bool> post,
        Func<DateTimeOffset> clock);
    internal CodexDesktopPresence Presence { get; }
    internal bool TryCancel();
}
```

Discover `ChatGPT` or `Codex` processes with a nonzero main window and an executable path inside the OpenAI Codex package. Cache presence probes for 500 ms. `TryCancel` posts `WM_KEYDOWN` and `WM_KEYUP` for `VK_ESCAPE` to the validated root window and admits at most one request per second.

- [ ] **Step 4: Normalize Desktop lifecycle from real presence**

```csharp
internal static CodexSnapshot? NormalizeDesktop(
    CodexSnapshot? snapshot, CodexDesktopPresence presence, DateTimeOffset now)
{
    if (!presence.Running) return null;
    if (snapshot is null || snapshot.UpdatedAt < presence.StartedAt)
        return EmptyDesktop(presence.StartedAt);
    if (snapshot.UpdatedAt < now.AddSeconds(-30) &&
        snapshot.State is "working" or "waiting_input" or "compacting")
        return snapshot with { State = "idle", CurrentTool = null, StartedAt = null, ProcessAlive = true };
    return snapshot with { ProcessAlive = true };
}
```

Apply normalization before Desktop/CLI selection during reload and polling.

- [ ] **Step 5: Route widget Stop by surface**

```csharp
private bool CanCancel => _store.Current switch
{
    { Source: CodexSurface.Cli, State: "working", ConsolePid: > 0 } => true,
    { Source: CodexSurface.Desktop, State: "working" } => _canCancelDesktop(),
    _ => false,
};
```

CLI keeps `CcCancel.Request`. Desktop calls `CodexDesktopRuntime.TryCancel()`.

- [ ] **Step 6: Run focused tests and confirm GREEN**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter "FullyQualifiedName~CodexDesktop|FullyQualifiedName~DesktopPresence" --nologo`

Expected: all selected tests pass.

- [ ] **Step 7: Commit the hotfix**

```powershell
git add src/Halo.App/Codex src/Halo.App/Widgets/CodexWidget.cs src/Halo.App/Shell/NotchController.cs tests/Halo.Tests
git commit -m "fix: keep Codex Desktop visible and enable Stop"
```

### Task 2: Authoritative model and context usage

**Files:**
- Modify: `src/Halo.App/Codex/Status.cs`
- Modify: `src/Halo.App/Widgets/CodexWidget.cs`
- Test: `tests/Halo.Tests/CodexRolloutTests.cs`

**Interfaces:**
- Extends: `CodexSnapshot` with `Model` and `TokenUpdatedAt`.
- Produces: a context pair only from one complete `token_count` event.

- [ ] **Step 1: Write failing complete/incomplete token tests**

```csharp
[Fact]
public void Parse_RequiresLastUsageAndWindowFromSameTokenEvent()
{
    var value = CodexRollout.Parse(TempRollout(
        Event("token_count", "\"info\":{\"model_context_window\":353400,\"total_token_usage\":{\"total_tokens\":94070036}}")))!;
    Assert.False(value.PresentFields.HasFlag(CodexSnapshotFields.ContextUsed));
    Assert.False(value.PresentFields.HasFlag(CodexSnapshotFields.ContextMax));
}
```

- [ ] **Step 2: Run and confirm RED**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter FullyQualifiedName~CodexRolloutTests --nologo`

Expected: the cumulative fallback still publishes context.

- [ ] **Step 3: Implement atomic latest-call context**

Read `last_token_usage.total_tokens` and `model_context_window` from the same event. Publish both flags only when both exist, remove the `total_token_usage` fallback, retain the token event timestamp, and capture the latest model slug.

- [ ] **Step 4: Render precise values and freshness**

```csharp
string value = $"{TokenText(st.ContextUsed)} / {TokenText(st.ContextMax)}";
string label = string.IsNullOrWhiteSpace(st.Model) ? "Context" : $"Context · {ModelText(st.Model)}";
```

Use one decimal place for K/M values and source timestamp for freshness.

- [ ] **Step 5: Run parser tests and commit**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter FullyQualifiedName~CodexRolloutTests --nologo`

Expected: all selected tests pass.

```powershell
git add src/Halo.App/Codex/Status.cs src/Halo.App/Widgets/CodexWidget.cs tests/Halo.Tests/CodexRolloutTests.cs
git commit -m "fix: show authoritative Codex context usage"
```

### Task 3: Creative operation-aware copy

**Files:**
- Create: `src/Halo.App/Codex/CodexActivityText.cs`
- Modify: `src/Halo.App/Codex/Status.cs`
- Modify: `src/Halo.App/Widgets/CodexWidget.cs`
- Test: `tests/Halo.Tests/CodexActivityTextTests.cs`

**Interfaces:**
- Produces: `CodexActivityText.From(state, operation, apiDown, netDown, justCompacted)`.
- Produces: nested operation extraction from `custom_tool_call.input`.

- [ ] **Step 1: Write failing phrase and extraction tests**

```csharp
[Theory]
[InlineData("apply_patch", "shaping code…")]
[InlineData("exec_command", "running commands…")]
[InlineData("view_image", "checking pixels…")]
public void OperationHasCreativeCopy(string operation, string expected) =>
    Assert.Equal(expected, CodexActivityText.Operation(operation));
```

- [ ] **Step 2: Run and confirm RED**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter FullyQualifiedName~CodexActivityTextTests --nologo`

Expected: compilation failure because `CodexActivityText` does not exist.

- [ ] **Step 3: Implement extraction and copy**

Extract only `tools.<identifier>` names from outer `exec` input. Do not retain arguments. Map phrases exactly as specified in the design; use `juggling a few things…` when multiple distinct nested operations exist.

- [ ] **Step 4: Replace widget-local Claude mappings and commit**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter "FullyQualifiedName~CodexActivityText|FullyQualifiedName~CodexWidget" --nologo`

Expected: all selected tests pass.

```powershell
git add src/Halo.App/Codex/CodexActivityText.cs src/Halo.App/Codex/Status.cs src/Halo.App/Widgets/CodexWidget.cs tests/Halo.Tests
git commit -m "feat: add operation-aware Codex capsule copy"
```

### Task 4: Weekly refresh and cache anti-spam

**Files:**
- Create: `src/Halo.App/Codex/CodexRefreshGate.cs`
- Modify: `src/Halo.App/Codex/Limits.cs`
- Modify: `src/Halo.App/Widgets/CodexWidget.cs`
- Test: `tests/Halo.Tests/CodexLimitsTests.cs`

**Interfaces:**
- Produces: `CodexRefreshGate.TryEnter()` and `Remaining`.
- Changes: `CodexLimits.RequestRefresh()` performs at most one rescan per 30 seconds.

- [ ] **Step 1: Write failing cooldown, deduplication, and expiry tests**

```csharp
[Fact]
public void RefreshGate_RejectsSecondRequestInsideThirtySeconds()
{
    var gate = new CodexRefreshGate(() => Now);
    Assert.True(gate.TryEnter());
    Assert.False(gate.TryEnter());
}
```

- [ ] **Step 2: Run and confirm RED**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter FullyQualifiedName~CodexLimitsTests --nologo`

Expected: compilation failure because the gate does not exist.

- [ ] **Step 3: Implement one refresh path and deduplicated cache writes**

Panel-open and click call `CodexLimits.RequestRefresh()`. The method enters one 30-second gate and calls the attached status store once. `CodexLimitsStore.Update` skips disk writes for identical values and source timestamps. Loading drops buckets whose reset time has passed.

- [ ] **Step 4: Render cooldown status and commit**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --filter FullyQualifiedName~CodexLimitsTests --nologo`

Expected: all selected tests pass.

```powershell
git add src/Halo.App/Codex src/Halo.App/Widgets/CodexWidget.cs tests/Halo.Tests/CodexLimitsTests.cs
git commit -m "fix: throttle Codex usage refresh and cache writes"
```

### Task 5: Integration, deployment, and visual verification

**Files:**
- Modify: `PROGRESS.md`

**Interfaces:**
- Consumes: all prior tasks.
- Produces: deployed executable and two verification screenshots.

- [ ] **Step 1: Run the full verification suite**

Run: `dotnet test tests/Halo.Tests/Halo.Tests.csproj -c Release --nologo`

Expected: all tests pass with no warnings or errors.

Run: `dotnet build src/Halo.App/Halo.App.csproj -c Release --nologo`

Expected: build succeeds with zero warnings and zero errors.

- [ ] **Step 2: Publish and replace the installed app**

```powershell
dotnet publish src/Halo.App/Halo.App.csproj -c Release -o "$env:LOCALAPPDATA\Halo\app"
Stop-Process -Name Halo.App -Force -ErrorAction SilentlyContinue
Start-Process "$env:LOCALAPPDATA\Halo\app\Halo.App.exe" -WindowStyle Hidden
```

- [ ] **Step 3: Verify live state and screenshots**

Confirm the installed process path, keep the Desktop capsule visible through an idle interval, compare model/context/Weekly values to the latest rollout, click Stop during a disposable active turn, and capture collapsed and expanded PNGs.

- [ ] **Step 4: Update progress and commit**

```powershell
git add PROGRESS.md
git commit -m "docs: record Codex capsule verification"
```
