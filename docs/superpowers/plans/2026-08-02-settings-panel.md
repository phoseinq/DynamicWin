# Settings panel — plan (2026-08-02)

Approved: **WPF**, the visual design from `codex/management-panel-foundation`'s WinUI preview, backend
written here. Not a merge of that branch — its 30 commits sit on a base 13 behind master and rewrite
`NotchController` and `NotifSource`, both of which master changed heavily the same night.

## The design is on the LIQUID-GLASS branch, not the foundation one

Corrected after the first build: `codex/management-panel-foundation` carries the older twelve-page flat
rail, and what was approved is `codex/liquid-glass-settings-preview` — six entries under group headers,
which is a different information architecture, not a restyle.

    Home
    SETTINGS   General · Features · Agents
    SYSTEM     Access
    REFERENCE  Docs & About

Deltas from what is built here now, all visible in the approved screenshot:

1. **Nav**: 6 entries with `SETTINGS` / `SYSTEM` / `REFERENCE` headers between them, plus a Home page
   above the first header. The eleven feature pages collapse into one **Features** page and the three
   agent pages into **Agents**.
2. **Selection**: a blue-tinted rounded pill behind the row, blue icon and blue label — not the frost
   fill built here. Blue is still confined to selection.
3. **Rows**: one card per row with a gap between them, not one grouped container with hairline
   separators.
4. **A slider row exists** (`Pill scale`, a track with a "100%" readout on the right). `RowKind.Slider`
   is not implemented here yet.
5. Type is a step larger: page title ~30px, row label ~13.5px, description ~12px.

Its `MainWindow.xaml.cs` is 1270 lines and also carries `SettingsDraftSession`, `SettingsPanelPolicy`,
`PreviewInteractionPolicy` and a `HaloRestartCoordinator`. The draft/apply machinery is deliberately NOT
wanted: settings here are written on the touch and picked up by the pill's watcher, so there is nothing
to apply and nothing to restart.

## What comes from the branch

Design only, ported by hand:

- `tools/Halo.SettingsPreview/MainWindow.xaml` — 190px fixed nav rail, 44px custom title bar, acrylic
  window with one full-window obsidian tint (`#52151A22`), rounded frosted rail (`CornerRadius 20`,
  `#3210151D`), detail column with a 27px title / 12px description head and an independently scrolling
  body.
- `PreviewCatalog.cs` — the page/section/row shape (`Toggle`, `Choice`, `Slider`, `Action`, `Status`),
  11 pages: General, Appearance, Media, Downloads, FileTray, Bluetooth, Notifications, ClaudeCode,
  Codex, OtherAgents, Access.
- `PreviewVisualPolicy.cs` — 11 tested 16x16 vector nav icons, type ramp 27 / 11.5-12.5 / 9.5px, blue
  confined to selected navigation and focus, toggles mint/graphite, neutral buttons white frost,
  attention coral.
- From the app side: `FeatureGate`, `AccessStatus`, `StartupShortcut`, `TrayIcon` are worth lifting.
  Everything under `src/Halo.App/Settings/` that draws (SettingsWindow / SettingsRenderer /
  SettingsLayout) is the rejected GDI+ panel and is not.

The five faults the WPF attempt was abandoned over are all fixable and are the acceptance criteria here:
extend the frame on **all four** margins, no near-opaque brushes over the backdrop, no default
ScrollViewer/Button chrome, no unrelated Unicode symbols as icons, no body copy under 11.5px.

## Shape

`src/Halo.Settings/` — its own WPF `WinExe`, published beside `Halo.App.exe`. Adds the WPF assemblies
(~20MB self-contained), no NuGet package.

Glass without WinUI: `DwmSetWindowAttribute` with `DWMWA_SYSTEMBACKDROP_TYPE = 3` (acrylic),
`DWMWA_WINDOW_CORNER_PREFERENCE = 2`, `DWMWA_USE_IMMERSIVE_DARK_MODE`, plus
`DwmExtendFrameIntoClientArea` with a -1 margin. `WindowChrome` for the custom title bar. This is the
same compositor path WinUI's `DesktopAcrylicBackdrop` takes.

Taskbar: a plain top-level WPF window is in the taskbar already; set the AppUserModelID and the icon so
it groups under Halo rather than beside it.

## Contract between the two exes

`%LOCALAPPDATA%\Halo\settings.json`, written tmp-then-move (atomic on NTFS), read by both. The shape is
**duplicated** in each project rather than shared through a library — the same decision `AskEnvelope`
already records for `Halo.Hooks`, with the round-trip pinned by tests on both sides.

Halo.App watches the file (`FileSystemWatcher` + a 1s poll, exactly like `StatusStore`) and applies
changes live. **No restart on Apply** — which also retires the reason the greeting had to be keyed on
the app version.

## Order

1. `SettingsFile` contract + store + live watch in `Halo.App`, with the 8 feature gates wired through
   `FeatureGate` so a toggle actually removes a widget. Tests both sides.
2. The WPF shell: window, glass, title bar, nav rail, one real page (General).
3. The rest of the catalog, bound to real settings.
4. Launch paths: clicking the Halo shortcut when an instance is already running opens the panel instead
   of exiting silently (today the mutex just returns); tray icon second.
5. Access page last — it reads real permission state, so it needs its own probes.

## Port it, do not redraw it (2026-08-02, after three rejected passes)

Rebuilding the design from a screenshot produced something different every time. The reference is now in
the repo — `docs/settings-reference/preview-28888.png`, captured from the running preview — and the next
pass is a **mechanical port** of the branch's own source, not another re-derivation:

    git show codex/liquid-glass-settings-preview:tools/Halo.SettingsPreview/MainWindow.xaml.cs
    git show codex/liquid-glass-settings-preview:tools/Halo.SettingsPreview/PreviewCatalog.cs
    git show codex/liquid-glass-settings-preview:tools/Halo.SettingsPreview/PreviewVisualPolicy.cs

WinUI to WPF is a small, known substitution list: `SystemBackdropElement` → a `Border` over the window's
own DWM backdrop, `Spacing` on a StackPanel/Grid → `Margin` on the children, `ColumnSpacing`/`RowSpacing`
→ the same, `x:Bind` → nothing (it is all built in code anyway). Every number — 84px mark, 34px title,
20px nav glyph, 16px corner radius — carries across unchanged.

What the reference shows that this build still gets wrong:

- **Nav icons are Segoe Fluent glyphs, each in its own colour**: Home mint `#74E6C2`, General blue
  `#7CB4FF`, Features pink `#FF91C8`, Agents violet `#D79BFF`, Access amber `#F0AE72`, Docs cyan
  `#5FDFE5`. Selection tints the pill and its border with the page's OWN accent, not one blue.
- **Home is a hero page**: a large gradient RING (the Halo mark, ~76px, not the .ico), "Halo" at 34px,
  the tagline "Your apps, activity and agents — surfaced when they matter.", then an `EXPLORE` eyebrow
  over a 2x2 grid of shortcut cards. Each card: a rounded tinted TILE holding the page's glyph in its
  accent, the page name, and a two-word subtitle ("Behaviour and appearance", "App surfaces", "Coding
  sessions", "Windows controls"). The card's border carries the accent too.
- Home's own subtitle is "A quieter place to begin".
- A `CURRENT DRAFT` section sits under EXPLORE with a status line and a "Reset to defaults" button. Its
  draft/apply wording does not apply here — settings are written on the touch and watched — so that
  section becomes something honest or is dropped.
