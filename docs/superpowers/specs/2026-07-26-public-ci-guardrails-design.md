# Public CI Guardrails Design

## Goal

Protect pull requests targeting `V3` with a real Windows build, executable tests, repository-style
policy checks, dependency review, and CodeQL analysis without claiming that automation can prove
intent or guarantee that code is non-malicious.

## Design

- Add a small public `Halo.Tests` xUnit project and include it in `Halo.sln`. Its initial
  characterization tests cover existing pure shell coordination logic; future PRs must add focused
  tests for new pure behavior.
- Add a Windows CI workflow for pushes and pull requests targeting `V3`, plus manual dispatch. It
  restores once, builds Release with warnings as errors, runs the test project without rebuilding,
  and invokes a PowerShell repository-policy checker.
- The policy checker rejects trailing whitespace, tabs in C# source, new production
  `PackageReference` items outside the existing `System.Drawing.Common` dependency, and comments in
  shipped C# source. The last rule reflects the public fork's comment-stripped-source policy; design
  reasoning belongs in the PR and the comment-bearing private source.
- Add a separate CodeQL workflow for C# on `V3` pushes, pull requests, weekly schedule, and manual
  dispatch. Use least-privilege permissions and pin every action to a full commit SHA.
- Add dependency review for pull requests. It complements CodeQL by rejecting vulnerable dependency
  additions; neither check is described as proof that a contribution is harmless.
- Add `CODEOWNERS` so security-sensitive workflow and source changes request review from
  `@phoseinq`.

## Verification

- Observe `dotnet test tests/Halo.Tests/Halo.Tests.csproj` fail before the test project exists.
- Run the policy checker, Release build, and tests locally.
- Push the commit to `V3`, manually dispatch both workflows, and wait for terminal success.
- Confirm both open PRs receive the new checks on their next synchronization; do not add extra PR
  comments beyond the existing change-request reviews.
