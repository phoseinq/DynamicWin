# Claude Code widget liveness fix

## Result

`StatusStore.IsLive` now treats a status as active only when its PID is alive, or when PID is exactly `0` and the state is an active Claude state with an `UpdatedAt` timestamp within 30 seconds. `ClaudeCodeWidget.IsActive` delegates to that property. The user's status file is never deleted, and the production `StatusStore()` constructor remains unchanged.

## TDD evidence

- Initial focused run (before implementation):
  `dotnet test tests/Halo.Tests/Halo.Tests.csproj --filter FullyQualifiedName~ClaudeStatusTests --no-restore`
  failed to compile because `StatusStore.IsLive` and the injectable test constructor did not yet exist (`CS1061`, `CS1729`).
- Boundary regression run after adding the negative-PID test but before its fix: 1 failed, 8 passed; `IsLive_IsFalseForNegativePid` failed because negative PIDs incorrectly used the PID-less fallback.
- Focused green run:
  `dotnet test tests/Halo.Tests/Halo.Tests.csproj --filter FullyQualifiedName~ClaudeStatusTests --no-restore`
  passed 9/9.
- Full green run:
  `dotnet test tests/Halo.Tests/Halo.Tests.csproj --no-restore`
  passed 27/27, 0 failed, 0 skipped.
- `git diff --check`: no whitespace errors.

## Scope review

Changed only:

- `src/Halo.App/ClaudeCode/Status.cs`
- `src/Halo.App/Widgets/ClaudeCodeWidget.cs`
- `tests/Halo.Tests/ClaudeStatusTests.cs`
- this report

The requested commit subject is `fix: hide Claude widget after its process exits`.

## Review fix evidence

Implemented all requested review changes:

- `NotchController` now tracks fullscreen hiding and no-active-widget hiding independently. It hides and returns when no widgets are active, and selects, shows, and renders an active widget immediately when activity returns. Active widget indices are captured in one pass so dynamic liveness cannot invalidate the controller snapshot between counting and selection.
- Process queries now return a process start timestamp. Positive-PID liveness requires the process to exist and to have started no more than two seconds after the status update, preventing materially newer reused PIDs from reactivating stale status.
- Expected process-query failures (`Win32Exception`, `UnauthorizedAccessException`, `ArgumentException`, and `InvalidOperationException`) resolve to inactive without escaping the UI polling path.
- Recent PID-less fallback explicitly covers literal `waiting`, runtime `waiting_input`, `working`, and `compacting`.

TDD runs:

- Process access failure RED: focused Claude run failed 1/10 with `Win32Exception: Access is denied` escaping `StatusStore.IsLive`.
- PID identity RED: focused Claude run failed compilation with `CS1503` because the store still accepted `Func<int,bool>` instead of an injectable process-start lookup.
- Controller visibility RED: focused controller run failed compilation because `NotchVisibility` and `NotchVisibilityAction` did not exist.
- Focused GREEN:
  `dotnet test tests/Halo.Tests/Halo.Tests.csproj --filter "FullyQualifiedName~ClaudeStatusTests|FullyQualifiedName~NotchControllerTests" --no-restore`
  passed 18/18, 0 failed, 0 skipped.
- Full GREEN:
  `dotnet test tests/Halo.Tests/Halo.Tests.csproj --no-restore`
  passed 36/36, 0 failed, 0 skipped.
