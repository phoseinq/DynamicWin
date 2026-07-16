# Task 4 report — Codex widget

## Status

Implemented and committed as a pixel-matched Codex widget: OpenAI mark, Codex status/limits/network bindings, CLI-only stop eligibility, dual local refresh, rendering-state test, and `--render-widget codex`.

## Verification

`dotnet test tests\Halo.Tests\Halo.Tests.csproj -c Release --filter FullyQualifiedName~CodexWidgetTests`

Result: 4 passed, 0 failed.

The Claude developer render wrote a 560×220 PNG. The Codex developer render currently fails before drawing because `CodexRollout.Number` calls `JsonElement.TryGetInt64` for a `null` field in a local rollout; that parser is concurrently owned and was not changed here.

## Concern

The Codex render-hook comparison must be rerun after the status parser handles JSON `null` numeric fields. The widget implementation itself compiles and its focused rendering-state test passes.
