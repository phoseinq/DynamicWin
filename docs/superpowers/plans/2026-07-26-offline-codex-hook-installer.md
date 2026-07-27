# Offline Codex Hook Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `DynamicWinSetup.exe` install and uninstall Halo's Codex hooks fully offline.

**Architecture:** A focused `CodexHookInstaller` in the already-shipped self-contained
`Halo.Hooks.exe` owns idempotent JSON merge/removal. Inno Setup invokes its setup commands; the
PowerShell developer wrapper delegates to the same command so behavior has one source of truth.

**Tech Stack:** C# 13, .NET 9, `System.Text.Json`, Inno Setup 6, PowerShell 7, xUnit.

## Global Constraints

- No new NuGet packages.
- Installation must require no internet, repository, `dotnet`, or `pwsh`.
- Preserve unrelated Codex hooks and never restore a whole stale backup during uninstall.
- Existing lifecycle hook commands must continue to degrade silently.
- Setup commands must return nonzero on malformed JSON or write failure.

---

### Task 1: Offline merge and removal commands

**Files:**
- Create: `src/Halo.Hooks/CodexHookInstaller.cs`
- Modify: `src/Halo.Hooks/Program.cs`
- Modify: `tests/Halo.Tests/CodexHookTests.cs`

**Interfaces:**
- Produces: `CodexHookInstaller.Install(string settingsPath, string hookExePath)`.
- Produces: `CodexHookInstaller.Uninstall(string settingsPath)`.
- Produces CLI commands `install-codex-hooks <hook-exe>` and `uninstall-codex-hooks`.
- Consumes `HALO_CODEX_HOOKS_PATH` only as a deterministic test override.

- [x] **Step 1: Write failing command-level tests**

Add tests that run the real `Halo.Hooks` process and assert:

```csharp
Assert.Equal(0, install.ExitCode);
Assert.Equal(7, HaloCommands(settings).Length);
Assert.All(HaloCommands(settings), command => Assert.Contains(hookExe, command));
Assert.Contains("keep.exe", AllCommands(settings));
Assert.True(File.Exists(settingsPath + ".halo-bak"));
```

Add separate tests for idempotent reinstall, surgical uninstall, and malformed JSON returning nonzero
without modifying the original file.

- [x] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~CodexHook"
```

Expected: setup commands are unknown or return zero without changing the fixture.

- [x] **Step 3: Implement the focused installer**

Use `JsonNode` to preserve arbitrary hook entries. For each managed event, remove handlers whose
command matches a `Halo.Hooks.exe ... codex ...` lifecycle command, delete empty entries, and append
exactly one new entry. Write `<settings>.halo-bak`, serialize to `<settings>.tmp`, then atomically
replace/move the settings file.

`Uninstall` performs the same filtered removal but appends nothing and never restores the backup.

- [x] **Step 4: Dispatch setup commands before lifecycle handling**

At the start of `Program.Main`, recognize:

```csharp
install-codex-hooks <absolute-hook-exe-path>
uninstall-codex-hooks
```

Resolve the settings path from `HALO_CODEX_HOOKS_PATH` or
`%USERPROFILE%\.codex\hooks.json`. Return `1` for setup failures while retaining the existing silent
`0` behavior for lifecycle hook failures.

- [x] **Step 5: Run focused tests and verify GREEN**

Run the Task 1 filter and require zero failures.

### Task 2: Delegate the wrapper and wire Inno Setup

**Files:**
- Modify: `hooks/install-codex-hooks.ps1`
- Modify: `installer/Halo.iss`
- Modify: `tests/Halo.Tests/CodexHookTests.cs`

**Interfaces:**
- Consumes: `Halo.Hooks.exe install-codex-hooks <path>`.
- Produces: Inno task `codexhooks`, enabled by default with `checkedonce`.
- Produces: an uninstall call to `Halo.Hooks.exe uninstall-codex-hooks`.

- [x] **Step 1: Update the wrapper test to exercise delegation**

Run the wrapper against a temporary profile and assert the generated commands use the selected
installed executable, unrelated handlers survive, the backup exists, and reinstall leaves one
handler per event.

- [x] **Step 2: Replace PowerShell JSON merge with helper invocation**

Keep installed-exe selection and development publish fallback, then execute:

```powershell
& $exe install-codex-hooks $exe
if ($LASTEXITCODE) { throw "Halo.Hooks setup failed: $LASTEXITCODE" }
```

- [x] **Step 3: Add offline installer tasks**

Add:

```ini
Name: "codexhooks"; Description: "Integrate with Codex"; GroupDescription: "Integrations:"; Flags: checkedonce

[Run]
Filename: "{app}\Halo.Hooks.exe"; Parameters: "install-codex-hooks ""{app}\Halo.Hooks.exe"""; Tasks: codexhooks; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{app}\Halo.Hooks.exe"; Parameters: "uninstall-codex-hooks"; Flags: runhidden waituntilterminated
```

Keep the existing post-install Halo launch entry.

- [x] **Step 4: Run hook tests and verify GREEN**

Run all `CodexHookTests`; require zero failures.

### Task 3: Package, install, and verify offline behavior

**Files:**
- Modify: `PROGRESS.md`

**Interfaces:**
- Consumes: the self-contained publish produced by `installer/build.ps1`.
- Produces: signed installer and portable zip plus a deployed local installation.

- [x] **Step 1: Run complete verification**

```powershell
dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release --nologo
dotnet build Halo.sln -c Release --nologo
```

Require all tests green and zero warnings/errors.

- [x] **Step 2: Build and install the signed package**

```powershell
pwsh -NoProfile -File installer\build.ps1
dist\DynamicWinSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="codexhooks"
```

- [x] **Step 3: Verify the live install**

Assert all seven managed commands point to
`%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe`, an unrelated fixture hook survives a reinstall, the
backup exists, and a temporary `codex prompt` probe writes a CLI `working` snapshot.

- [x] **Step 4: Record deployment state**

Append root cause, implementation, exact test count, build result, installer result, live process,
and deployed-vs-pushed state to `PROGRESS.md`.
