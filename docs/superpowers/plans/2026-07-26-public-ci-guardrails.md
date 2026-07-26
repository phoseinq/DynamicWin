# Public CI Guardrails Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add tested build, repository-policy, dependency, and CodeQL gates to the public `V3` branch.

**Architecture:** A Windows build workflow runs the real solution and a focused public test project.
A small PowerShell checker enforces repository-specific rules that generic formatters cannot express.
A separate least-privilege CodeQL workflow performs GitHub-native C# security analysis.

**Tech Stack:** GitHub Actions, .NET 9, xUnit, PowerShell 7, CodeQL.

## Global Constraints

- Target the public `V3` branch.
- Pin third-party actions to full commit SHAs.
- Do not add production NuGet dependencies.
- Do not modify application behavior.
- Do not post additional PR comments.

---

### Task 1: Public test foundation

**Files:**
- Create: `tests/Halo.Tests/Halo.Tests.csproj`
- Create: `tests/Halo.Tests/NotchVisibilityTests.cs`
- Modify: `Halo.sln`

- [ ] Run the absent test-project command and record the expected failure.
- [ ] Add the test project and pure characterization tests for `NotchVisibility.Decide`.
- [ ] Add the project to `Halo.sln`.
- [ ] Run the focused tests and require a non-zero test count with zero failures.

### Task 2: Repository policy and build workflow

**Files:**
- Create: `scripts/verify-public-source.ps1`
- Create: `.github/workflows/ci.yml`
- Create: `.github/CODEOWNERS`

- [ ] Add a self-test mode to the policy script and observe it fail before implementation.
- [ ] Implement checks for trailing whitespace, C# tabs, shipped-source comments, and production
      package additions.
- [ ] Run the self-test and the repository scan.
- [ ] Add the pinned, least-privilege Windows build/test/policy workflow.

### Task 3: Security workflows and deployment

**Files:**
- Create: `.github/workflows/codeql.yml`
- Modify: `docs/superpowers/plans/2026-07-26-public-ci-guardrails.md`

- [ ] Add pinned CodeQL and dependency-review jobs with minimal permissions.
- [ ] Run full Release build, tests, policy scan, YAML parsing, and `git diff --check`.
- [ ] Commit as `phoseinq` with no co-author trailer and push the verified commit to `V3`.
- [ ] Dispatch workflows manually and wait for successful terminal conclusions.
