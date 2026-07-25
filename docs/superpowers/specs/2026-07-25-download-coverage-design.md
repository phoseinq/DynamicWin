# Download coverage — browsers, Steam, and any learned app

Date: 2026-07-25. Status: approved, staged implementation.

## Problem

`Downloads.Scan()` finds a download by regex-matching a leading `NN%` in a visible **window title**.
Two consequences, both reported by the user:

- **Browsers are unsupported by design.** `Downloads.cs:118` skips them (`// "50% off" page, not a
  download`) because a page title can contain a percentage. That guard is correct but blinds Halo to the
  single most common source of downloads.
- **Apps that never put a percentage in their title are invisible.** Steam is the reported case.

Only Microsoft Store (`StoreInstall` → `AppInstallManager`) and Xbox (`GameInstall` → staging folder)
have real integrations today.

## Verified facts (measured on this machine, not assumed)

| Fact | Evidence |
|---|---|
| Chromium `History` is readable while the browser holds it open | `sqlite3_open_v2` with `?immutable=1` returned rc=0 and real rows with Chrome running (17 procs) |
| The `downloads` table carries what we need | `current_path`, `total_bytes`, `received_bytes`, `state` (0=in progress, 1=complete, 2=cancelled) |
| Chrome has more than one profile here | `Default` and `Profile 3`, both with a `History` file |
| A file's owning process is obtainable **without admin** | Restart Manager (`RmStartSession`/`RmRegisterResources`/`RmGetList`) correctly reported the holder of a test file; `elevated: false` |
| Steam exposes real byte counts | `appmanifest_*.acf` has `name`, `StateFlags`, `BytesDownloaded`, `BytesToDownload` |
| Steam has three libraries here | `libraryfolders.vdf` lists `C:\Program Files (x86)\Steam`, `H:\SteamLibrary`, `D:\SteamLibrary` |
| Firefox has no clean downloads table | downloads live in `places.sqlite` annotations, not a dedicated table |

## Architecture — three tiers of evidence, one output

No single source covers everything, so tier by **data quality** and let the best available win:

```
Tier A  a growing partial file      -> detection + live progress   (any app, any browser)
Tier B  the browser's own SQLite     -> final size + clean name     (=> a real percentage)
Tier C  first-party integration      -> Steam / Store / Xbox        (exact bytes, name, percent)
```

Tier A is the backbone: it watches the filesystem, not the app, so it covers **every** browser and any
app that writes a partial file. Tier B only supplies the missing number (`total_bytes`) that turns file
growth into a percentage.

`Downloads.cs` stays the coordinator. The `IsBrowser` guard at line 118 **stays** — browser downloads
now arrive through Tier A, and the guard still prevents "50% off" page titles from false-positiving.

## Components

| File | Responsibility | Pattern it follows |
|---|---|---|
| `Widgets/PartialFiles.cs` | scan roots for `*.crdownload` `*.part` `*.download` `*.opdownload` `*.partial` `*.!ut` `*.aria2`; track growth; resolve owner via Restart Manager | `Downloads.cs` |
| `Widgets/BrowserDownloads.cs` | Chromium `History` + Firefox best-effort, via `winsqlite3.dll` with `immutable=1` | `Notifications/WpnDb.cs` |
| `Widgets/SteamInstall.cs` | `libraryfolders.vdf` -> per-library `appmanifest_*.acf` | `Widgets/GameInstall.cs` |
| `Widgets/Downloaders.cs` | learned (app, directory) pairs persisted to `%LOCALAPPDATA%\Halo\downloaders.tsv` | `Notifications/BannerGate` |

## Browsers — all of them

Chrome, Edge, Brave, Opera and Vivaldi share the Chromium `downloads` schema, so one reader covers all
five; only the profile root differs. Every `Default` and `Profile N` directory is scanned, because this
machine already has two active profiles.

Firefox is the exception: its downloads are annotations in `places.sqlite`, not a table. Firefox
therefore gets **Tier A only** — real bytes downloaded, no percentage. That is honest and matches the
project's "never display invented numbers" rule.

## Steam

`BytesDownloaded < BytesToDownload` is the primary signal because it is unambiguous. `StateFlags` is
secondary: its exact bit semantics have **not** been verified against a live Steam download, so it must
not be the sole gate. `libraryfolders.vdf` is parsed so downloads on any of the three libraries count.

## Learning other downloading apps

What learning actually buys is the **directory**, not the app. Halo cannot watch the whole filesystem, so
it starts at `Downloads` and `Temp`. When a partial file with an identified owner appears, the pair
(app, directory) is recorded, and that directory is watched from then on — including for files with no
partial-file suffix, which is how a launcher downloading into `D:\Games\...` gets picked up.

Guard against false positives (log files and databases also grow): sustained growth across several
samples, a meaningful rate, and a size floor. Halo's own process and known non-download writers are
blacklisted.

## UI

Percent whenever a total is known — full progress bar plus the number. When the total is unknown
(Firefox, a learned app, Xbox staging) fall back to the **existing** indeterminate presentation rather
than inventing one: `Downloads.NoPct` already exists and `DownloadWidget` already renders the
Claude-compacting-style whole-pill breathing glow for the queued case. The unknown-total state reuses
that glow with the label **"Downloading"** plus the real byte count, and no percentage.

## Errors and threading

Every probe wrapped in `try { } catch { }` and degrading silently, per the project invariant. All work
happens off the UI thread on the existing `Downloads.Poke()` timer. SQLite is opened read-only with
`immutable=1`. Each Restart Manager session is closed in a `finally`.

## Testing

Pure, unit-testable helpers: the `.acf` parser, the `libraryfolders.vdf` parser, partial-suffix
classification, the growth tracker, and bytes-to-percent conversion. Plus a `--probe-downloads` dev hook
that prints all three tiers, matching the project's existing `--probe-*` convention.

## Staging

Each stage is independently useful:

1. Tier A — every browser and any app, bytes only.
2. Tier B — real percentages for Chromium.
3. Steam.
4. Learned directories.

## Out of scope

Download speed and ETA (they live in window bodies, needing cross-process UIA), per-download cancel for
another app (no API exists; the existing Stop reveals the downloader), and a browser extension.
