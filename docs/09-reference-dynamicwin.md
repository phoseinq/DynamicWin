# 09 — Reference notes: DynamicWin (what to copy, what to avoid)

Repo: FlorianButz/DynamicWin. Source is on branch **main** (V2 branch is readme-only).
Read for reference only; don't vendor its code.

## Confirmed: why it looks low-res / not smooth (validates our stack)
- Its whole UI is drawn with **SkiaSharp** (`SKCanvas.DrawRoundRect`, blur via
  `SKImageFilter.CreateBlur`) inside an `Update(float deltaTime)` game-loop.
- That means blur is a software-ish filter (banding / the "LED" look) and animation is tied to a
  manual loop, not the compositor → not refresh-rate synced.
- → Our WinUI 3 + Composition choice (real backdrop blur + compositor-thread springs) is the direct
  fix. Don't reproduce the Skia path.

## Widget model (worth mirroring, simplified)
- `IRegisterableWidget { bool IsSmallWidget; string WidgetName; WidgetBase CreateWidgetInstance(...) }`
  — a factory + registration pattern. Our `IWidget` + static registration (03) is the simpler
  equivalent; keep ours.
- Files: `UI/Widgets/WidgetBase.cs`, `Small/SmallWidgetBase.cs`, `IRegisterableWidget.cs`.

## Media: do NOT copy their approach
- `Big/MediaWidget.cs` gets track info by reading the **Spotify process MainWindowTitle** and
  splitting `"artist - title"`. Spotify-only, breaks on ads/pause, no artwork, no controls for other
  players.
- We use `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` instead: works for
  every player, gives title/artist/thumbnail/playback status and real play/pause/next/prev. Strictly
  better. (P5)

## Toolchain (verified 2026-07-13)
- WinUI 3 builds from CLI on this machine with a hand-authored csproj (`net9.0-windows10.0.19041.0`,
  `UseWinUI=true`, `WindowsPackageType=None`, `Microsoft.WindowsAppSDK 1.6.*`). No VS workload/
  templates needed. `dotnet build` → Build succeeded, 0 errors. Ship self-contained for runtime.
