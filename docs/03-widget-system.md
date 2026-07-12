# 03 — Widget system

Goal: adding an app = implementing one interface. The shell never changes.

## The contract
```csharp
interface IWidget
{
    string Id { get; }
    string Name { get; }
    int Priority { get; }                 // order in the pill / panel

    FrameworkElement CollapsedView { get; }  // small: what shows in the pill (icon + mini bar)
    FrameworkElement ExpandedView { get; }   // full panel section

    event EventHandler<AttentionEventArgs>? RequestAttention;  // → shell goes to Peek/Expanded
    void Activate();                      // called when shown
    void Deactivate();                    // called when hidden — stop timers/watchers here
}
```
- `CollapsedView` stays tiny (fits the pill). `ExpandedView` is a normal XAML UserControl.
- A widget owns its own data source (media session, battery API, file watcher). The shell only
  lays out views and reacts to `RequestAttention`.
- `Activate/Deactivate` let a widget stop work when the panel is closed (don't poll while hidden).

## WidgetHost
- Holds `IReadOnlyList<IWidget>` sorted by `Priority`.
- Renders each `CollapsedView` into the pill row; stacks `ExpandedView`s in the panel.
- Subscribes to `RequestAttention` and asks the `NotchController` to Peek/Expand.
- v1: widgets registered in one static list. `ponytail: static registration now; AssemblyLoadContext
  scan of a widgets/ folder only when a real third-party widget exists.`

## How to add an app (the recipe)
1. New folder `src/Halo.App/Widgets/<Name>/`.
2. Implement `IWidget` with a collapsed and an expanded view.
3. Add it to the static registration list in `WidgetHost`.
4. Done — no shell/rendering changes.

## Built-in widgets (v1)
- **ClaudeCode** — see 05/06. The proving ground for `RequestAttention` (waiting-for-input peek).
- **NowPlaying** — `GlobalSystemMediaTransportControls`.
- **Volume** — audio endpoint volume.
- **Battery** — `Windows.System.Power`.

First widget built (P2) is a trivial **Clock** just to prove the contract end-to-end before CC.
