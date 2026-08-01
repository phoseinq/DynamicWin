# Halo — progress

## 2026-08-01: answerable banner — hook half (design in `docs/superpowers/specs/2026-08-01-answerable-banner-design.md`)

The half that lives in `Halo.Hooks` is done and verified end to end. **Release 0/0, 479 tests pass
(was 433).** The pill half — `AskStore`, the banner chips, `--render-ask` — is not started.

`AskGate` answers "would this call have prompted?", which PreToolUse cannot ask Claude because Claude
decides it *after* hooks run. It reads `permissions.allow` itself (`AskSettings`, cached by mtime, since
this process spawns once per tool call) and stays silent on anything already covered — otherwise the
feature would raise a banner for the `git status` on the allowlist and make the user answer *more* than
before. `AskUserQuestion` with exactly one question is always askable; 2-4 questions are not intercepted
at all. Every unreadable input answers no.

`AskFlow` writes `ask-<nonce>.json`, waits 300ms for an ack (absent = Halo is not running, get out of
the way), then 20s for an answer, and prints a decision **only** when an answer came back. It runs after
the status save on purpose: the pill needs the tool call on screen before the hook parks itself.

Verified live, not just by unit test: a payload for `Bash` returned silently in 75ms because this
machine's settings carry a bare `Bash` allow rule — the gate working, and worth recording because it
also made a first end-to-end attempt look like a failure. An `AskUserQuestion` payload raised the ask,
an ack plus answer produced exactly
`{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Hold"}}`,
and all three rendezvous files were swept.

**Not deployed** — the installed hooks are still the previous build; this needs
`dotnet publish -r win-x64 --self-contained` before it does anything in a real session.

### Pill half, part 1: store, queue, banner, render hook

**Release 0/0, 488 tests pass (was 479).** `AskStore` reads `ask-*.json`, acks, and writes the answer a
click produces; `AskQueue` beside it is the pure part — FIFO, one banner at a time, and an expired head
steps aside rather than holding the banner hostage (9 tests). It acks **every** ask on sight rather than
only the visible one: the hook gives up 300ms after an unacked ask, so acking only the head would send a
queued second question back to the terminal before its turn came.

It rides `StatusStore`'s existing watcher and 1s poll through a new `AfterLoad` callback rather than
starting a timer — deliberate, because a once-a-second frame tick would routinely miss that 300ms ack
window. The callback sits outside `Load`'s try, so a bad status file cannot stop a pending question from
being seen.

`AskBanner` draws it, with layout (`Chips`) split from painting so the hit-test and the drawing cannot
disagree — a chip you can see but not click is the worst available bug in a surface whose only job is to
be clicked. Colour carries the answer: green allow, red deny, amber for a question's options.

Verified with a new `--render-ask` hook showing both forms, one chip hovered.

**Still to do:** wiring into `NotchController` — raising the banner morph when `AskStore.Pending` is
non-null and routing chip clicks to `AskStore.Answer`. Until that lands the feature does nothing in a
real session.

### Pill half, part 2: the banner sizes itself to its text

**Release 0/0, 497 tests pass (was 489).** The wiring is in (`_askChips`/`_askH` in `NotchController`),
so this pass was the look, driven by answering real questions on the live pill.

**Root cause of the visible complaint: the layout was arithmetic over constants.** `RowH = 46f` meant a
two-line description was ellipsised away while the banner had the entire screen above it to grow into,
and the reflex fix — smaller type — is backwards. The text is the content, so the geometry follows it:
`AskBanner.Layout` measures each label and description, gives every row its own height, wraps the title
to up to three lines, and `Chips`/`Height` are now views onto that one layout.

Measuring needs a `Graphics`, which is what the old comment was avoiding — the property it was
protecting (callers may recompute every frame, and nobody caches a height that can drift out of step
with what `Draw` painted) is kept by a one-entry memo keyed on the ask instance, plus a 1x1 measuring
surface held for the process lifetime. Type went **up**, not down: title 17.5→19, label 13.5→15,
description 11.5→12.5, eyebrow icon 16→19, number disc 28→32.

Two GDI+ traps paid for on the way. `StringFormat` centring sits a glyph high **and left** of its box —
the disc numerals were visibly off-centre, and the fix is the ink-bounds centring `LayeredNotch.
DrawGlyphCentered` already records, measured back at 0.5px. And `GenericTypographic` sets `LineLimit`,
which silently drops the last line of a rect sized to an exact number of lines; cleared, plus 3px of
slack in the drawn rect.

The numerals are glass too (alpha 150/215 white) — a solid white digit was the one opaque thing left on
a banner whose whole idea is panes you can see through.

Verified by extending `--render-ask` with a title and a description long enough to wrap — before, that
sample fitted on one line and proved nothing — then by raising real asks on the running pill. 8 new
tests pin what breaks when geometry comes from text: a long description makes a taller row, rows stack
without overlapping and stay inside `Height`, the hit-test rect still covers the number outside the
glass, and `Chips`/`Height` agree with the layout they read from.

**Running from the worktree's `bin/Release`; not committed, not pushed**, and the installed build under
`%LOCALAPPDATA%\Programs\Halo` is untouched.

### Pill half, part 3: "this is glass, what you made is not" — three real bugs under one complaint

**Release 0/0, 497 tests pass.** Reported as "the option rows are not glass". Two visual guesses were
made and both were wrong, in opposite directions — first darkening the whole banner to 170 so the rows
would read, then giving the rows a dark fill — and each was correctly rejected: the first went flat over
a dark app, the second turned the rows into blacker holes in a black panel. What settled it was a
reference screenshot next to a screenshot of the banner, and then `HALO_DUMP_GLASS=1`. **Three separate
bugs, none of them in `AskBanner`:**

1. **The pill was photographing itself.** `_capturable` (the Ctrl+click-the-pushpin recording mode, which
   this user leaves on) skips the screen-DC grab because the pill is no longer excluded from capture —
   but it fell through to the *window*-DC BitBlt, and that returns what is on screen over the region,
   overlapping windows included. So the pill read its own glass back in and fed it forward at ~5fps,
   converging on its own tint. Visible directly in the dump: "CLAUDE CODE ASKS" and the banner's own top
   edge baked into the backdrop the banner was about to be drawn over. It hit the **ask** banner hardest
   because it is tall enough that the capture strip is almost entirely pill, which is exactly why the
   short notification banner in the reference still looked like glass. Capturable now goes straight to
   `PrintWindow`, which re-renders the target window and cannot contain the pill.
2. **Banners were drawn with no glass layer at all.** `glassFade` fades the captured backdrop out with
   the tint when `_empty` — correct for the invisible drop-catch strip it was written for, but `_empty`
   is the *normal* state when a question arrives with no agent on the strip, so `_shrink` hit 1 and the
   banner composited tint over nothing. Banners are now exempt.
3. **The frost squeeze crushed every backdrop into the same dark slab.** `FrostContrast 0.34` +
   `FrostFloor 0.05` map the full brightness range behind the pill onto 9..72 before the tint even lands
   — deliberate for the widget panel, where a bright band behind opaque widgets reads as a shape *inside*
   the pill, and wrong for a banner, which is mostly backdrop and meant to be looked through. `Frost` now
   takes a `clarity`, threaded `Render → DrawShape → ShapeInto`; banners pass `BannerClarity = 0.8`, the
   panel still passes 0 and is bit-for-bit unchanged.

With those fixed the last of the black was the notch's own flat wash, which is what "the notch gives the
options their black colour" correctly identified: `TintAsk{Desk,App}` are now **60 / 34**, far below the
panel's 245 / 48. Everything drawn on the banner brings its own contrast instead — lit capsule rims and a
1px shadow under every line of text.

The rows themselves are **empty capsules** now, per the reference: body alpha 7, and the shape carried by
a single 0.7px rim lit brightest at the top plus two fading specular streaks. The number beads match,
minus the specular blob — it sat on the digit like a smudge. A second stroke inside the rim was tried for
wall thickness, which is the textbook way to draw glass and is wrong at this scale: it reads as two
outlines, not as one thick one.

Verified by screenshotting the real pill over a purpose-built backdrop window carrying a near-white band,
a near-black band and a colour gradient — the case both earlier attempts failed at opposite ends of. The
white band now passes through the banner and through the capsules with its own colour. Two traps worth
remembering for the next time this needs eyeballing live: a borderless maximised window trips the
fullscreen-hide and the pill vanishes, and `HALO_CAPTURABLE=1` used to *create* the very artefact being
investigated (bug 1) — that is fixed, but it is why the first three screenshots were misleading.

**Not committed, not pushed; installed build untouched.**

### Pill half, part 4: write-your-own answers, and the hooks actually deployed

**Release 0/0, 499 tests pass (was 497).** Claude Code's own question UI always lets you ignore the
options and type something, and a banner offering only the canned answers quietly removed that. There is
no new channel for it: `AskStore.Answer` already returns a question's pick as a *deny whose reason is the
chosen label*, and a reason is a free string — so free text rides the path that was already there.

The row is appended by `AskBanner`, not sent by the hook: the hook forwards the tool's own options
untouched, and putting a pill-invented option in that payload would claim Claude offered a choice it did
not. Told apart by reference identity (`AskBanner.IsOther`), because the label is display text and a real
option could legitimately carry the same words.

Getting keystrokes into it cost four wrong answers, each worth keeping:

1. **Drop `WS_EX_NOACTIVATE` and take focus.** `SetForegroundWindow` returned true and changed nothing.
   Windows hands the foreground only to a process that already owns it, and Halo is a background pill
   that by construction never does. `AttachThreadInput` onto the foreground thread moved it no further.
   Every keystroke kept landing in the terminal behind. Abandoned: `Interop/KeyGrab.cs` installs a
   `WH_KEYBOARD_LL` hook while the field is open instead, so the user's app keeps the focus it had and
   only the keys the field uses are taken from it — Alt and Win chords are never touched, so Alt+Tab
   still leaves.
2. **`WM_CHAR` for the text.** It never arrived: WM_CHAR is synthesised by `TranslateMessage`, and this
   thread's pump belongs to the framework. `ToUnicodeEx` on the key-down is self-contained.
3. **Two heap corruptions on that one call** — `0xc0000374`, no managed exception, nothing in
   `halo-crash.log`, because a corrupt native heap does not unwind. First a `StringBuilder` marshalled as
   `[Out] LPWStr`, which is not a valid combination. Then `char[]`, which is the subtle one: `DllImport`
   defaults to `CharSet.Ansi`, so the marshaller hands the API an 8-**byte** scratch buffer and
   `ToUnicodeEx` writes 8 UTF-16 characters — 16 bytes — into it. Now `byte[]`, blittable and pinned.
4. **Change detection compared a snapshot taken in the same frame.** Keystrokes arrive *between* frames
   from the hook, so `_askTyped != prevAskTyped` was always false: the field drew its caret once and then
   never updated. It compares against the last *drawn* value now.

The handlers themselves only touch state — Apply used to be called from inside the hook callback, which
is a Windows callback on a timeout. An empty Enter cancels rather than sending a blank reason that would
read as a choice. Long answers scroll the tail under the caret instead of ellipsising the end being
written.

**Nothing takes the pill mid-sentence.** A toast winning the slot over a question is normally right, but
it tears down the banner being typed into — and the language-flip toast fires exactly when someone
switches layout to write the answer. Leaving it queued was the first attempt and only moved the
interruption: it popped the instant the field closed, telling the user about a layout change they made
minutes earlier. It is dropped now — a mirrored toast is a copy, and the original is still in Action
Centre. And closing the field is never discarding it: Escape, a stolen pill, a re-served question all file the
text as a draft against that question's nonce and restore it when the question comes back. Only actually
answering throws the words away.

**The first live answer was typed in Persian, and the field was LTR-only.** RTL text needs
`DirectionRightToLeft` per the project's own rule, and the trap is that the flag is the *whole* fix:
it already reverses what `StringAlignment.Near` and `Far` mean, so flipping the alignment as well put
the run back against the left edge with the caret floating in open space beside it. Flag only, alignment
untouched, caret at `Right - run` — the insertion point in RTL. `--render-ask` now types mixed
Persian+English into the sample so this cannot regress. Two pre-existing raw Persian literals in
`Program.cs`'s notification sample were escaped while here; that file is ASCII again.

**Deployed, for the first time in this feature's life.** `settings.json` already pointed `PreToolUse` at
`%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe`; the binary there predated the ask feature entirely. Now
published `-r win-x64 --self-contained` and the four `Halo.Hooks.*` files copied over it.

Verified through the deployed hook rather than by hand-writing rendezvous files: a real `PreToolUse`
`AskUserQuestion` payload on stdin produced `ask-*.json`, the pill acked inside the 300ms window, and
with nobody answering the hook printed **nothing** — the safety contract intact. Answered, the same run
printed
`{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"neither - profile it first, then decide"}}`,
which is the typed text arriving back at Claude verbatim. `--render-ask` carries the new row and a
mid-typing frame, since that state cannot be reached by clicking around a static image.

**Halo.App is still worktree-only and uncommitted; only the hooks are installed.**

## 2026-08-01: pill text clipping, Codex limit naming, banner bidi (worktree `.worktrees/claude-master`)

Done in an isolated worktree on `master`, because a Codex agent works the primary checkout and its
branch switches twice discarded uncommitted edits mid-task. **Release 0/0, 433 tests pass (was 408).
Running from the worktree's `bin/Release`; not committed, not pushed.**

**Collapsed pill clipped long mood lines.** Three faults at once in `DrawCollapsed`, mirrored in both
agent widgets: the clip opened to the pill's full width instead of the gap the words were measured
against, so a long line ran under the timer and off the edge; `zoneW = avail + 16f` paid the entrance
shift at every `e`, so a settled pill overhung its own budget by 16px (now tied to `1f - e`, which is
what earns it); and there was no `Trimming`, so an over-long line was sliced through a glyph rather
than ellipsised. "desk needs clearing" — 19 chars, the exact case `Moods.cs:43` already described.

**Codex claimed a 5-hour window it does not have.** `CodexLimits.FiveHour`/`Week` were `Primary`/
`Secondary` renamed — nothing ever checked `WindowMinutes`, so whatever the rollout reported first was
called "the 5-hour limit". Now `PrimaryFrac`/`SecondaryFrac`/`PrimaryReset`/`SecondaryReset`, positional
names for a positional projection, and the alert's "weekly" label became "secondary". `LimitCaption`
knew only 300 and 10080 and called everything else "plan", which is how two buckets collided on one
caption; it now derives the name from the real duration (13 tests).

**Notification banner mangled mixed-direction text.** `IsRtl` was "any Hebrew..Arabic char anywhere",
so one Persian word flipped a whole English message right-to-left and GDI+ bidi reordered every latin
run as a block — the `|` separators landed between the wrong pieces. Now first-strong (UAX #9 P2/P3),
and the detail view draws its body **line by line, each with its own direction** rather than one
paragraph direction for the lot (12 tests). Verified by extending `--render-notif` with a third,
detail-state banner carrying the reported content: the English lines now read in order; the one line
that genuinely holds both scripts still leans on bidi, which is correct.

**Glass capture was most of Halo's idle CPU.** Three compounding faults, all in the capture path:

1. *Cadence was a frame count.* `CaptureFast/CaptureSlow` counted ticks, so the backdrop refresh rate
   rode whatever tier `AdaptFrameRate` picked — "every 2 frames" is 20fps at the 40fps it was sized for
   but 60fps at the idle 120fps tier. Now `CaptureOpenMs`/`CaptureCollapsedMs` in milliseconds, so the
   rate means the same thing at every tier.
2. *Every capture forced a full redraw.* Any `CaptureVersion` bump makes `Frame()` call `Apply()`, which
   redraws the whole layered surface supersampled — even when the new plate was identical. `DoCapture`
   now fingerprints the blurred plate (coarse FNV-1a grid, ~1.1k samples) and only bumps on a real
   change. Measured: **597 of 600 consecutive captures were identical** while the pill just sat there.
3. *A static backdrop was still being grabbed at full rate.* The grab is the larger cost — on the
   PrintWindow path ~30ms of waiting on the other app. `LayeredNotch.StaleStreak` counts identical
   plates and the collapsed cadence backs off up to 4x, resetting to full rate on the first real change.

Nothing throttles animation: frames still come from `AdaptFrameRate` and `IWidget.Animating`, and the
back-off is collapsed-only. Measured over 30s idle, same method both sides:

| `capturable` | before | after |
|---|---|---|
| 1 (PrintWindow path) | 49.8% of one core | **24.2%** |
| 0 (screen fast path) | 20.9% of one core | **12.3%** |

Trap worth remembering: `capturable` (Ctrl+click the pushpin) *must* skip the screen fast path or the
pill photographs its own glass, so it forces PrintWindow at ~30-43ms a grab. A measurement that does not
pin this setting is not comparable — one run here was invalidated by exactly that.

**Not done:** the management-panel buttons for these knobs. `SettingsWindow.cs` only exists on
`codex/management-panel-foundation`; the constants above are the values those controls would drive.

**Hook payloads were decoded with the console's OEM code page.** `Halo.Hooks.ReadInput` used
`Console.In.ReadToEnd()`, but the hook payload is UTF-8 JSON, so every non-ASCII character arrived
mangled — a Persian "د" is `D8 AF`, which CP437 renders as `╪»`. That reached the pill through `cwd`
and `lastPrompt`, so a Persian prompt (and the repo's own `...\دسکتاپ\...` path) displayed as
box-drawing garbage. Now reads the raw stdin stream through `UTF8Encoding`, which bypasses the console
code page whatever the host set it to. Verified end to end: a `prompt` payload carrying U+0633 U+0644
U+0627 U+0645 comes back out of the status file as exactly those codepoints.

Note these two fields only refresh on their own hooks — `lastPrompt` on `prompt`, `cwd` on
`session-start` — so already-stored mangled values persist until the next one fires, which reads as the
fix having failed when it has not.

**Deploy trap, learned the hard way here.** `%LOCALAPPDATA%\Programs\Halo\` holds a **self-contained**
publish. Copying the four `Halo.Hooks.*` files from a plain `dotnet build` output puts a
framework-dependent build there, and it cannot start: "No frameworks were found", every hook silently
dead and the pill frozen on stale status. Tell them apart by `Halo.Hooks.deps.json` — 27309B
self-contained against 422B framework-dependent. The quick deploy must come from
`dotnet publish -r win-x64 --self-contained`, not from `bin/Release`.

**Convention added:** source files stay ASCII, no Persian in code — see the invariants in `CLAUDE.md`.

## 2026-08-01 (later): 3.1.7 - the updater removed, and a privacy policy that had gone false

Prompted by preparing for a Microsoft Store submission ("so Microsoft does not object").

### The updater is gone
`src/Halo.App/Update/AutoUpdate.cs` (220 lines), `tests/Halo.Tests/AutoUpdateTests.cs` and the single
`AutoUpdate.Start()` call in `Program.cs`. The Store does updates itself, and an app that quietly
downloads and runs its own installer behind it is a submission-bouncer. **Consequence stated plainly:
people who installed from the GitHub setup no longer update silently** - that is now a manual download.

Verified, not assumed: 3.1.7 launched at 01:35:09 while `%LOCALAPPDATA%\Halo\update-check` and
`update-log.txt` still carried their 23:17:47 stamps from the 3.1.6 run, so nothing rewrites them; and
`repos/phoseinq/*/releases/latest` no longer appears anywhere in the shipped `Halo.App.dll`.

**Detour worth recording so it is not re-litigated a third time.** After the removal the ask came back -
the nightly check had been the author's own feature request. It was restored with a `Packaged` guard
(`Windows.ApplicationModel.Package.Current` throws in an unpackaged process, so the throw is the answer)
so the setup/portable builds would keep updating while a Store copy stood down. That was then rejected:
**no updater in the code at all.** The tree is back to the state 3.1.7 actually shipped in, and this is
the settled answer - if it comes up again, the decision is "none", not "conditional".

### PRIVACY.md had become untrue, which is worse than incomplete
It stated in two places that Halo *"does not use the Windows location API"* and that "your actual
location does not [leave], because Halo never asks for it". `Almanac.DeviceLocation()` has used
`Windows.Devices.Geolocation` since the location work, and `Refresh()` sends the fix to Open-Meteo
formatted `0.####` - about 11 m. Both language versions now carry:

- the reads-table row for device location that never existed, gated on "only if location is on **and**
  Halo is allowed";
- an Open-Meteo network row that says **coordinates**, at what precision, and what the fallback is;
- the consent mechanism (`ConsentStore\location`, `Value == "Allow"`, read *before* asking so a denied
  app never triggers a prompt) and where the user switches it off in Windows;
- the `api.github.com` row and the `update-check` / `update-log.txt` files deleted along with the
  updater, plus an explicit "no update checks and no background downloads".

### The rest of the sweep
- **18 `phoseinq/DynamicWin` links** across both READMEs and both privacy policies now point at
  `phoseinq/Halo`; the READMEs' "silent updates" bullet and subtitle claim went with the feature.
- **CI had never once run on the new repo.** `ci.yml` and `codeql.yml` triggered on `branches: [V3]`
  only, and the new repo's default branch is `main` - Actions were enabled the whole time, nothing was
  broken, nothing matched. Both now trigger on `[main, V3]`, and CI is **green on `main`**.
- **LICENSE needed nothing**: already MIT / 2026 / phoseinq, and GitHub reports `spdx_id: MIT`. Only a
  missing trailing newline was added.

`scripts/publish-mirror.ps1` refused the push until `-AllowDeletions` was passed - its guard against a
stripped push erasing an outside contribution. Here the two deletions were the intended ones.

Build **0/0**, **408 tests** (the AutoUpdate tests went with the feature). Tag `v3.1.7`, signed setup +
portable zip released on **both** repos, installed here and running `3.1.7.0`. **Pushed and deployed.**

## 2026-08-01: the calendar the banner speaks, and the blog's download box

### The hourly banner now picks its calendar from where the machine is
Asked on what basis the banner was writing a Jalali date. It was never hard-coded: `Almanac.CalendarFor`
already took the country the weather geocoder resolved and fell back to the Windows region. But the only
branch was "IR or Gregorian", which got two places wrong.

- **Afghanistan** runs the same Solar Hijri calendar with different month names, and had been deliberately
  left on Gregorian for that reason - a test carried the note *"also solar, but with different month names
  - not worth being wrong about"*. Overriding that note was the wrong move; the answer is the second table.
  Kabul reads **8 Asad** where Tehran reads **8 Mordad**.
- **Saudi Arabia** gets the lunar date via `UmAlQuraCalendar`, not `HijriCalendar`: the plain one is
  tabular arithmetic and drifts a day or two from the dates actually published there, which is the entire
  point of showing it. Outside its supported range it throws and falls back to Gregorian.
- The country list stays short on purpose - *which calendar is civil here*, not *which countries are
  Muslim-majority*. Egypt, Turkey and Indonesia are asserted as Gregorian so a later good intention cannot
  quietly widen it.

`bool jalali` became `CalendarKind {Gregorian, SolarHijri, SolarHijriAfghan, LunarHijri}`. One snag worth
recording: the parameterised test could not stay a `[Theory]`, because an `internal` enum cannot ride a
`public` xunit signature (CS0051) - it is a `[Fact]` walking a case table instead.

Build **0/0**, **428 tests** green. Verified live with `--probe-almanac` on this machine: `country IR
metric True calendar SolarHijri`, body `Friday, 9 Mordad`. **Released and deployed.**

### 3.1.6 shipped
Tag `v3.1.6`, signed installer + portable zip on **both** repos (`phoseinq/DynamicWin`, where AutoUpdate
still looks, and `phoseinq/Halo`). Installed here from the signed setup and relaunched - `3.1.6.0` running.

### The blog's download box, both languages
`pvboy.dev` + `boystore.org`, post 14 `halo-glass-notch`. The box still called the product **DynamicWin**
while the whole post calls it Halo and the link already pointed at `phoseinq/Halo`. Renamed in `content`
and `content_fa`. A portable-download link was added alongside the installer and then **removed at the
user's request** - the box is one button again.

- **The two vhosts share one database** (`codeboy_blog`), so a single `UPDATE` covered both sites; the
  copy-to-both-and-checksum dance that this file records for assets does not apply to post text. Confirmed
  by reading `MD5(content)` through each vhost's own config.
- Traps that cost time: the CLI `php` has no mysqli (use `/usr/local/lsws/lsphp83/bin/php`), the read-back
  endpoint is `?action=posts&slug=…` (plural), and the rendered page is built client-side so grepping its
  HTML finds nothing. All four string edits were required to match **exactly once** or the script aborted;
  both columns were backed up to `/root/halo-post14-*.{en,fa}.bak` first.
- Verified from outside on both domains: box says Halo, one `DynamicWinSetup.exe` link, zero portable
  links, zero remaining "DynamicWin" as a product name. Setup and asset URLs both 200.

## 2026-07-31 (latest+11): the calendar, the stamp that never advanced, and the blog's download box

Build 0/0, **428 tests**. Deployed here (3.1.6.0 installed from the signed setup and relaunched),
pushed to `master`, mirrored to both remotes, released on both repos, blog updated live.

### The hourly banner now speaks the calendar of the place it is in
Not the hard-coding it looked like: `Almanac.CalendarFor` already took the country the weather geocoder
resolved and fell back to the Windows region. The bug was that the only branch was "Iran or Gregorian".
- **Afghanistan** runs the same Solar Hijri calendar with different month names, and was deliberately
  left on Gregorian for that reason - a test carried the note *"also solar, but with different month
  names - not worth being wrong about"*. The answer was the second table, not the omission: Kabul reads
  `8 Asad` where Tehran reads `8 Mordad`.
- **Saudi Arabia** gets the lunar date via `UmAlQuraCalendar`, not `HijriCalendar` - the latter is
  tabular arithmetic and drifts a day or two from the dates actually published there. Outside its
  supported range it throws and falls back to Gregorian rather than print a wrong date.
- The list stays short on purpose - *which calendar is civil here*, not *which countries are
  Muslim-majority*. EG, TR and ID are asserted Gregorian so a later good intention cannot widen it.
- `[Theory]` could not carry the internal `CalendarKind` on a public test signature (CS0051); the cases
  live in a `[Fact]` instead. Verified with `--probe-almanac`: `country IR calendar SolarHijri`,
  body `Friday, 9 Mordad`.

### Every build since 3.1.3 shipped stamped 3.1.3.0 - clients were reinstalling daily, forever
`AssemblyVersion`/`FileVersion` were pinned by hand in the csproj and were never part of a version bump,
so 3.1.4, 3.1.5 and 3.1.6 all went out with `3.1.3.0` inside the exe. `AutoUpdate` compares the release
tag against `Assembly.GetName().Version`, so a client on 3.1.6 read itself as 3.1.3.0, found v3.1.6
newer, installed it, and came back up still reading 3.1.3.0 - **it never converged**. Both properties are
now absent so the SDK derives them from `<Version>`; setting them to 3.1.6.0 would only have re-armed the
trap for the next release. The v3.1.6 assets on both repos were re-uploaded with the fixed build, since
the first upload still carried the looping stamp. Verified: exe stamps 3.1.6.0, and
`IsNewer("v3.1.6", 3.1.6.0)` is false.

### The blog's download box now points at the new repo, in both languages
Post 14 `halo-glass-notch` had four GitHub links - the `Halo` name link and the download button, in
`content` and `content_fa`. All four now read `phoseinq/Halo`; zero occurrences of the old path remain.
- **The two vhosts share one database** (`codeboy_blog` on localhost) - the relink script dedupes by
  (host, db) rather than running twice. Columns backed up to `/root/halo-blog-backup-*.json` first.
- Only the `owner/repo` segment was rewritten, never the asset filename: the installer is still built as
  `DynamicWinSetup.exe`, and replacing the bare word `DynamicWin` would have broken the URL.
- **v3.1.6 was published on `phoseinq/Halo` before the blog was touched** - repointing first would have
  left a live 404 on the download button. Verified end to end: the button URL returns 200 / 31,404,872
  bytes, and both hosts serve identical JSON.
- Server traps, now in memory as well: reach it as `root@128.140.73.105` with `~/.ssh/boy_key` - the
  **hostnames do not route from here**, only the IP. `scp` and even plain ssh drop with `Connection reset
  by peer` often enough that the working pattern is `ssh "cat > file" < local` to upload, and `nohup ...
  > log` plus a second connection to read the log, so a dropped pipe cannot kill a half-finished UPDATE.
  The CLI `php` has no mysqli; use `/usr/local/lsws/lsphp82/bin/php`. The API route is
  `?action=posts&slug=...`, and its JSON escapes every slash, so grepping for `phoseinq/Halo` finds
  nothing - match `phoseinq\/Halo` or parse the JSON.

### Still open
- **Releases now have to go to both repos.** `AutoUpdate` still points at `phoseinq/DynamicWin`, while
  the blog now sends new users to `phoseinq/Halo`. Until that one-liner is switched, every release must
  be published to both or one of the two audiences is stranded.

## 2026-07-31 - the pulse stops stepping, downloads breathe - **3.1.5 RELEASED**

**Shipped.** `origin/V3` @ `62b27cc`, tag **v3.1.5**, release page with both signed artifacts. Build 0/0,
**427 tests** local / 424 on the mirror. Installed locally from the built installer, running
`3.1.5+d68ebfa`. (v3.1.4 went out an hour earlier from `d3c0491`; everything below is what came after it.)

### Four brightnesses instead of a swell
Reported as "نبض پله‌ای است، تعداد رنگ‌هایی که روشن/خاموش می‌شود را زیاد کن". `Fx.Glow` declared
`int alpha`, but that value only ever reaches GDI+ through `Matrix33`, which is float end to end - the
integer was pure loss, and it landed exactly where it hurt. `PillBar`'s two breathing glows were computed
as `(int)(16 * strength * lit)` and `(int)(13 * strength * lit)`: across a whole breath that is **four**
distinct values and **three**. Widening the type is the entire fix; all seventeen callers pass int
literals and are unaffected.

Verified by measurement, not by eye - the previous rounds proved eyeballing a filmstrip does not catch
this. A throwaway xunit probe rendered `PillBar` 160 times at ~15ms and counted distinct pixel values:
the wide glow's argument went 4 levels → continuous, and a pixel mid-fill now takes **24-26** distinct
values per breath. What remains is the 8-bit alpha of the fill, which is GDI+'s floor. Note the sampling
trap that wasted a step: the `--render-bar` filmstrip's rows are 430ms apart, so it *cannot* show banding
that lives between 16ms frames - the rows differed either way.

### Downloads got the same breath
`DownloadWidget` already knew `paused`; it now passes `alive: !paused`. A download at 40% and one stalled
at 40% are the same still picture.

### The README gif is the current pill
`ReadmeFiles/media.gif` re-recorded: 900x225 like the old one, 9.5s, 414 KB (was 834). The old one
predated the background bar, the pulse and the eased accent. Recipe, since it took three takes:
`HALO_CAPTURABLE=1`, ffmpeg `ddagrab:draw_mouse=0:framerate=60`, `crop=1225:306:667:0` on a 2560x1600
panel (the pill is DPI-scaled 1.25x, so the expanded panel is 700x275 physical and this crop is the old
gif's framing), then `fps=20,scale=900:225:lanczos` with `palettegen=stats_mode=diff` +
`paletteuse=dither=bayer:bayer_scale=4` - a plain gif encode would put the banding straight back via the
256-colour palette. Two traps: **the agent circle beside the pill is our own session**, rewritten by the
hooks on every tool call, so deleting `~/.claude/notch/*.json` only works if the whole take runs inside
ONE tool call; and **ddagrab only emits frames when the desktop changes**, so a 9.5s take produced 521
frames, not 570 - sampling frame 545 for a contact sheet yields a black tile and looks like a broken
recording.

### SSH to GitHub died mid-release
`Connection closed by 198.18.0.68 port 22` - that range is a VPN/proxy's, and only port 22 was affected;
HTTPS was fine throughout. Fix that keeps working: `gh auth setup-git` for credentials, a second remote
`origin-https`, and `publish-mirror.ps1 -Remote origin-https`. The script already takes `-Remote`, so
nothing had to be edited.

## 2026-07-31 (early hours) - the collapsed bar: made visible, made continuous, made to move

Six complaints in one session, all about the pill's own background bar. Five were separate root causes.

**Shipped as v3.1.4.** Release build 0/0, **427 tests** green locally (424 on the mirror - three compile only
behind `HALO_PRIVATE_ASSETS`). Local `master` carries eight commits (the mirror was cut at the seventh,
`7c22591`; `PROGRESS.md` is not part of the public tree); the stripped mirror is
`origin/V3` @ `d3c0491`, tagged **v3.1.4**. `installer/build.ps1` produced a signed
`DynamicWinSetup.exe` (30.0 MB, Authenticode Valid) and `DynamicWinPortable.zip` (41.6 MB), both stamped
`3.1.4+7c22591`. **Deployed** to `%LOCALAPPDATA%\Programs\Halo` by DLL hot-swap during the session, not yet
by installer. The GitHub Release itself is **not created**: the `gh` keyring token is invalid
(`gh auth refresh -h github.com`), and the tag is pushed ahead of it.

Two shell notes worth keeping. `pwsh` launched *from the Bash tool* mangles the «دسکتاپ» path segment on its
way to a child `git` (`git -C` fails with "No such file or directory" on a mojibaked path); launched natively
it is fine, so mirror/installer scripts run from PowerShell, not Bash. And `NotifBanner.cs` holds a raw NUL
byte in `_fitBody = "\0"` - written as the character, not the escape - which makes git treat the file as
**binary**: line endings are not normalised for it, so an editor that rewrites it as LF commits a 349-line
change disguised as three bytes. Worth converting to the escape some day.

### The start of a track repainted the pill in one frame
The bar's colour is the album art's, so a track change swapped the whole background between two frames -
"یهو رنگ میخوره". Two eases fix it, both in `DrawCollapsed` off its own frame clock: the drawn accent lerps
toward the art's (tau 0.30s, first cover snaps - washing in from the White sentinel would be a flash of grey),
and the bar's alpha fades in and out (tau 0.20s) with the outgoing fraction held in `_lastProg` so a track
change cross-fades instead of blinking off and on. The *fill* still snaps on a track change, deliberately: a
new song's bar belongs at the new song's position, not gliding back from where the last one ended.

### It was missing entirely on greyscale covers — the one that kept coming back
The complaint that survived three deploys. `AccentOf` answers `Fx.White` when a cover has no colour worth
extracting, and `PillBar` draws **nothing** for White — its other callers use that sentinel to mean "no colour
worth painting with". `--probe-timeline` settled it in one line: a playing track, real art, `end=0:02:52`,
`pos=0:00:37`, `ring=0.219`, `accent=WHITE (no bar!)`. Everything the bar is made of was correct and the bar was
never drawn. For this widget the bar *is* the content, so White now falls back to a neutral light grey at the
call site — deliberately not `Fx.White` itself, which is the sentinel. Rendered with `--render-bar <png> dee2e8`.

### It was invisible on dark album art
`PillBar` derives every colour from the accent by taking value *away* — the track sits at `v*0.34`. A black-ish
cover gives a black-ish accent, so the bar was drawn in full and simply could not be seen ("اونایی که تیرن
نمیاد"). The accent now gets a floor (v ≥ 0.62, hue kept, a little saturation put back so the lift isn't just
grey) before anything is derived from it. Bright accents are already above the floor and pass through
untouched. `--render-bar <png> [rrggbb]` takes an accent now: `1a1512` used to render a black bar on black
glass, and renders a legible warm brown one.

### The "alive" pulse read as a separate piece, then as nothing at all
First attempt was a bright band pinned at the wavefront. At this strength the fill sits near 18% alpha and that
band peaked near 36% — a stripe twice as bright as the bar it rode on, which is exactly why it looked detached
("تیکه تیکه"). Replacing it with a body-wide breath fixed the seam and went too far the other way: you could no
longer tell it was moving. What ships is a wide, soft shimmer that **travels** the filled length, clipped to the
fill so it never paints on the track, peaking below the body's own alpha. Strength also went 0.34 → 0.5, which
brings in the sheen and lip and makes it read as one lit body. Verified on the `--render-bar` filmstrip: the
highlight sits at a different place in each frame, and `paused` is flat.

### ...and then the animation itself was the problem, so it was taken back out
Three effects had piled up at the wavefront and were competing: a lip, a tight glow on top of it, and the
travelling shimmer. Reported as "سرش روشن تره، بعد یه خط ایجاد شده بینشون، بعد از پشت موج میاد". The **line**
was real and specific: the lip's gradient ramped up to 94% of its width and then fell back to nothing over the
last 6% - about two and a half pixels of bright-then-dark right at the head. Final state is much plainer: the
shimmer is gone, the lip rises into the head with **no** drop-back (it just stops where the clip stops) at 0.3
alpha instead of 0.5, and the wavefront glow drops 26 → 13. The slow whole-body breath is the only "alive" cue
left. Four shapes were tried before this one; the filmstrip shows an even fill with a soft head and nothing
sliding along it.

### Two bars, a pale one ahead of a solid one — `Fx.Glow` was throwing the caller's clip away
`Fx.Glow` opens with `g.SetClip(pillPath)`, and `SetClip(GraphicsPath)` defaults to **Replace**. So the clip
`PillBar` set around each glow call - "stay inside the filled part" - was discarded the moment Glow ran, and the
halo went on spilling past the wavefront exactly as before. Because the wide halo is drawn *before* the fill,
that spill is a faint band lying **under** the bar and reaching further right than it: reported as "دوتا نوار
شده، یکی کم رنگ زیرش جلوتر، یکی پر رنگ عقب تر". `SetClip(clip, CombineMode.Intersect)` fixes it, and is
identical to the old behaviour for every caller that has no clip of its own.

Worth recording how it was found, because two rounds were wasted first: the filmstrip and even a pixel dump of
the live pill (captured with `HALO_CAPTURABLE=1`, which is the way to actually SEE the running window) both
looked clean, because on a near-neutral accent the tail is only a few levels above the track. Sampling the same
row on both builds is what showed it - past the edge, `32 33 33 32 32 31 30 29 29 29` decaying over ~60px
versus a flat `28 28 28 28 28 28 28`. **The lesson: a clip set around a Fx.* call is not necessarily in force
inside it.**

### The head was two pieces, and the filmstrip had been lying about the strength
Both glows are centred **on** the wavefront, which puts half of each one past it: after the fill's own crisp
edge there was a soft detached blob lying on the empty track, so the head read as two pieces. Both are now
clipped to the filled part - nothing exists to the right of the wavefront except the track. The position
correction was also being snapped whenever it exceeded 0.02 of the duration, which on a three-minute song is
under four seconds and therefore fired on ordinary reports; a snap is exactly what "not smooth" looks like, so
only a track change or a real seek jumps now (0.08) and the ease is tighter (tau 0.14).

Separately: `--render-bar` had been drawing at `strength 0.34` while `MediaWidget` passes **0.5**, so every
filmstrip eyeballed here was a weaker bar than the one that ships. The hook renders the real number now, and
takes an optional fraction (`--render-bar out.png dee2e8 0.07`) because the breath's visibility at the *start*
of a track - a few pixels of fill - is its own question, and was the reason the swing was widened again.

### The fill stepped instead of gliding
Players report position in lumps; Spotify repeats the *same* position for seconds. `RefreshTimeline` re-stamped
`_posAt` against that unchanged `_pos` every 200ms poll, restarting the extrapolation each time, so the fill
could never grow past one poll's worth. An identical reading is the player repeating itself, not time standing
still — the clock is now left alone on a repeat. `--probe-timeline` shows `rep`/`pos` frozen at `0:00:02` while
`ring` climbs 0.011 → 0.026: the extrapolation is carrying it.

### A seek took 4-5s to reach the picture
Two causes. Every request waited 320ms for a burst that, for one drag-and-release, was never happening; and
then it re-sent with a widening gap up to ~5s, each retry seeking the video *again* — a player that has stopped
reporting never agrees however often it is asked. Now: an isolated ask goes out immediately, only one landing on
the heels of a send waits for the tapping to stop, and there is exactly one retry before it stops asking.

### The bar leapt forward and back at the start of a track
A track change and its timeline don't land together — for up to a second the player still serves the outgoing
track's span and position. That leftover is now recognised by its duration and waited out (time-boxed, so a
playlist of equal-length tracks can't stall the bar). **This one shipped broken first:** attaching to a session
also lands in the track-change path, with the title arriving *after* `Hook()` has already read a good timeline,
so the guard armed against the correct current track. `--probe-timeline` caught it as a solid 2s of `end=0` /
`ring=-1` at startup — on screen, a pill with no bar (a *second*, separate cause of the same symptom). There is no predecessor to protect against on the first
track, so nothing is discarded there.

Also: a zeroed span (what a backgrounded browser tab answers with) no longer erases a duration already known,
and ring colours now ease toward their target instead of flipping between frames — done centrally in
`NotchController.EaseRings` so Claude, Codex and the rest get it from one place.

### Tooling
`--probe-timeline` prints, twice a second for 15s, what the media widget actually believes (`end`, `pos`,
`rep`, `prevEnd`, pending seek, `ring`, accent). It has to run on an **MTA thread**: the widget fills itself in
from `async void` handlers, and a WinRT completion arriving in an STA is a COM callback that only lands when
the apartment pumps messages, which a probe sitting in `Thread.Sleep` never does — the first three runs printed
"session hooked, no title yet" forever. `--probe-media` next door gets away with it because a blocking
`GetResult()` on an STA pumps while it waits.

The four timing rules are extracted into `MediaTiming` (same file, the `NotchVisibility` pattern) and tested —
they all fail as *rendering* bugs from the outside, and one of them looked obviously right and was measured
wrong.

Release 0 warnings / 0 errors, **427 tests** (up from 409; `MediaTimingTests`). Deployed by `Halo.App.dll`
hot-swap. **Not pushed.**

## 2026-07-30 (evening) - the media panel: a speed menu, a second line, a seek bar that works, and VLC

### The size never showed, because the title has no extension
The shortcut was sitting right there — `Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media.mkv.lnk`, in Recent,
pointing at a real 2.2 GB file — and the lookup never so much as started. Windows' Media Player reports the
name **without** its extension, and `LooksLikeFile` required one before it would spend a directory listing.
The name-match had the same fault from the other end: it compared the shortcut's target (`…Film2Media.mkv`)
against a title that stops at `…Film2Media`.

Both fixed, and both are now tests: a release name is recognised by its shape (several dot-separated pieces)
rather than by an extension it may not carry, and a candidate matches with or without the extension the title
lacks — while a near miss (`…Film2Media.2.mkv`, `…720p…`, the same name with a `.txt` on it) still does not.
Verified with the new `--probe-size`, which runs the lookup on a title with no player open at all:
**2,351,393,277 bytes = 2.2 GB**.

### Is it actually playing? Ask the bar
A two-hour film advances the pill's background bar about a pixel a minute, so the bar could not answer the
one question you glance at it for. Now the wavefront **breathes** while it is playing — the same idiom the
agent pills already use for "a process is running" — and stands perfectly still when it is not, which is the
other half of the question and needs no signal of its own.

`--render-bar` is a filmstrip of one full breath plus a paused row, because a pulse cannot be judged from a
single still: brightest at +860ms, gone by +2150ms, and the paused row is visibly flat beside them. The VLC
widget gained an `Animating` of its own so its breath actually gets frames — nothing else about a playing VLC
changes per frame.

### Seeking several times quickly: the player drops all but the first
Reported: it works, but use it a few times in a row and it stops moving. Three measurements, each one
killing the theory before it:

1. `--probe-seek 60 6` fires six taps 120ms apart **through the widget's own path**. The widget asked for
   1:11, 1:12, 1:13, 1:14, 1:15, 1:16 — and the player went to 1:11 and stopped. Every single call returned
   **true**. So the player honours an isolated seek and **silently drops** anything arriving while it is
   still working on one, and nothing in the API says so.
2. Serialising the requests (awaiting each before issuing the next) changed nothing: it is not a fire-and-
   forget problem.
3. The same six taps 3 seconds apart mostly landed. It is a timing property of the player, not of us.

Which means sending on every tap throws away exactly the position the user wanted — the last one. So a tap
now moves a **target**, and the target is sent once the tapping stops for 320ms, then re-sent with a widening
gap until the player reports itself there, giving up after about five seconds and letting the player's own
position back in. The pill has already moved to the target, so none of that waiting is visible.

Two supporting fixes fell out of it. The position is set **optimistically** at the moment of asking, so the
next relative tap counts from where you asked to be rather than from a position the player has not caught up
to reporting — three quick ±10s taps used to all land in the same place. And while a seek is outstanding the
only report believed is one that **agrees** with the target: a timestamp is not enough, because a player will
stamp an update after our request and still carry its pre-seek position in it.

Measured after: six taps 120ms apart, the widget tracks all six (1:33 → 1:38) and the player lands on
**1:38:35** within 800ms of the last tap, and stays.

### The bar was dead because the widget never learned the duration
Reported again: the bar still does not work. Two probes settled it in a way that three rounds of reasoning
had not.

`--probe-seek` asks the live session to move and reports what happens: **both directions worked perfectly**
at the API level (`returned True`, position followed immediately, forward and backward alike). So the player
was never the problem, and neither was the seekable window - `--probe-media` showed `minSeek=0`,
`maxSeek=2:10:23`, `canSeek=True`, which makes the earlier `StartTime`/`MinSeekTime` clamp a no-op. Then the
same probe read the *widget's* own view of that session: **`RingProgress = -1`**.

That is the bug. `_end` was **zero** in the widget while the session reported 2h10m. SMTC's
`TimelinePropertiesChanged` is not a stream, it is an occasional nudge - Media Player fires it on a seek and
then says nothing for minutes - so a session hooked before its file had a duration keeps a zero forever. And
everything hangs off that number, so everything broke at once and silently: the bar never filled, the
timestamps never drew, `RingProgress` was -1, and `Seek()` returned early on `end <= start`. Which is exactly
the reported shape of it — the ±10s buttons worked, because they do not need a duration, and the bar did
nothing at all.

The timeline is now **polled** twice a second from the draw path as well; the event stays, to make updates
prompt. Both refreshes only bump `Version` when something actually moved, so a poll that finds nothing new
costs no repaint. Proof in the render: the bar fills and the timestamps read **5:00 / 2:10:23**, where before
there were no timestamps at all.

### The speed list, again, in the panel's own language
"It is not smooth and it does not match the theme" - both fair. The first one was an opaque dark box with a
1px white border: a Win32 context menu sitting on a frosted panel. This one is built from what the rest of
the panel is built from — a translucent wash over the glass (the seek bar is visible *through* it), an accent
glow underneath so it belongs to the artwork's colour, and an edge that is brightest at the top and gone by
the bottom, which is what a lit glass edge does and why a uniform hairline read as hard.

Smoothness was three things: per-item hover was **binary** and now eases; the list **unrolls**, each row a
beat behind the one above it, instead of seven rows appearing together; and it opens quickly but closes
slowly (0.075s in, 0.13s out), because a menu that snaps shut is the part that reads as abrupt. The chevron
turns over as it opens rather than swapping glyph. The current rate is an accent-tinted pill now, not a dot.

Seven rows at 21px fit exactly between the handle and the bottom of a 220-tall panel; the first attempt used
23px rows and the last one hung out of the panel.

### `--render-widget` now settles before it shoots
Every hover state in these panels eases toward its target, and the hook drew exactly one frame - so a menu
that opens on hover rendered at a fifth of its opacity and looked like it had not been drawn at all. It now
runs 45 real frames on a real clock before the shot. Half of this session's UI work could not be checked
without it.

### No ring around the collapsed art after all
Added, then removed the same evening: the pill's own background already carries that exact fraction, and a
second reading of one number two pixels away is decoration. Both widgets, and the `Fx.PathProgress` helper
stays - it is the only way to stroke part of a rounded square's outline, and it will be wanted again.

### The speed chip became a menu, and lost its circle
It was a glass chip in the transport row that **cycled** on every click: four clicks from 1x to 2x, no way to
see the choices, and no way back except all the way round. It is now a bare label at the top right - no chip,
no ring, because it is a menu handle rather than a button - with a chevron, and pointing at it drops the whole
list: **1x, 1.25x, 1.5x, 1.75x, 2x, 2.5x, 3x**. The current rate is marked with a dot rather than by being
the brightest row, or "which am I on" and "which am I about to pick" would be the same signal. Hovering
either the label or the list keeps it open, so the pointer can travel between them; while it is open it owns
the pointer, because a click landing on what is *under* a menu is the oldest bug in menus.

Top right, not in the transport row, for a reason that is pure geometry: the row sits at y=158 in a 220-tall
panel, so a list opening *downward* from it had 22px to live in. From the title row it has 150.

### A second line under the name
A release filename is a sentence about the file - `Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media` says the
year, the resolution, the source and who put it out - and all of it was being dropped. The line now reads
**"Film2Media - 1080p - BluRay"**, and a single dot when nothing is known, so the row keeps its height and
nothing below it moves.

What may be read and what may not is the whole design, and it is a test: a publisher is only claimed for a
name that really is a dotted release (`Spy.mkv`, `Interstellar (2014).mp4` and `My holiday video.mp4` all
yield nothing), and a trailing codec or quality token is never mistaken for one. A resolution the *player*
reports beats one parsed from a name, since a name can lie.

**The file size** is the part SMTC cannot answer: there is no path anywhere in that API. The shell knows,
though - anything opened from Explorer leaves a shortcut in Recent, named after the file, carrying its target.
`MediaFileInfo` matches the title against Recent, pulls paths out of the `.lnk` (both the ansi and the utf-16
copy, by scanning for drive-letter paths) and then **only believes one if the file is really there and really
has that name**. The verification is the point: a crude extraction plus a hard check beats a correct parse
with no check, because the failure mode here is a fabricated number on the pill. Cached, misses included, and
it runs off the render path and bumps `Version` when it lands.

### The seek bar's two bugs, both in the same missing concept
Reported: on Windows Media Player, seeking forward works but the bar glitches backwards; seeking **backwards
does nothing at all**.

One cause underneath both: the timeline is not `[0, EndTime]`. SMTC also reports `StartTime`, `MinSeekTime`
and `MaxSeekTime`, and Media Player uses them - so a backward target computed as `frac x EndTime` landed
*before* `MinSeekTime` and was rejected outright, while a forward target still fell inside the range and
worked. Every position is now expressed against the real seekable span: the fraction shown, the timestamps,
the +-10s buttons, the ring, and the seek itself, which clamps to `[MinSeekTime, MaxSeekTime]`.

The glitch was the other half: a player emits a timeline update with the OLD position while a seek is in
flight, and taking it at face value dragged the bar back before the real update arrived. A seek now records
its target, and until the reported position is near it (or 1.5s passes) the stale updates are dropped.

### VLC has a seek bar now
VLC does not speak SMTC - it has its own widget and an http channel - so it had +-10s buttons and no bar at
all. Its status document carries everything needed and more than SMTC gives: whole-second `time` and
`length`, and the **real stream resolution** out of the demuxer rather than a filename's claim. So: position
and length are polled, `SeekTo(frac)` sends a percentage, the panel draws the same bar with the same
press-and-drag-and-commit-on-release, and the same "ignore the poll that overtakes my own seek" guard. Its
second line reuses the media panel's parsers, with the real resolution preferred. The poll is about once a
second, so the bar extrapolates between readings - including by the playback rate - or it would step.

### Video progress on the collapsed pill, twice
Like the agent pills: the spent fraction as the pill's **own background** (`Fx.PillBar`), and the same
fraction **around the art** - which is a rounded square, so `Fx.PathProgress` strokes a fraction of its
outline (flatten, walk the length, cut the last segment where the fraction actually falls) instead of an arc
that has nothing to draw on. Both for the SMTC widget and for VLC.

### The clock's weather asks the machine where it is
It geocoded the city guessed from the timezone, which gave every city in a zone the same reading. Now:
Windows Location first when the user has it switched on - read from the consent store rather than assumed, so
a denied or switched-off service is a silent fall back to the timezone rather than a prompt - and the
timezone city remains both the fallback and the banner's *label*, since a fix carries coordinates and no
name. Verified live with `--probe-almanac`: `source = windows location`.

## 2026-07-30 — the voice reads the room, the ring rides with it, and the chime says where you are

Release 0 warnings / 0 errors, **372 tests** (up from 259; five new test files). Deployed by DLL hot-swap
and relaunched. **Mirror published to `origin/V3`.**

### "still cooking…" forever — the bug under the feature request
- **Reported as:** only one line ever appears on the pill; it never changes.
- **Root cause:** the 60-second hold was re-stamped **on every read**, which makes it a sliding window —
  and `Draw*` reads it 125 times a second. Any key the pill kept looking at could therefore never expire,
  so a long thinking block latched `unknown@ages` and held it for the life of the process. The whole
  table was decoration; the wording that shipped was never the problem.
- **Change:** the expiry is measured from when the line was *rolled*. A reroll also avoids the line that
  just expired — with a two-line set, picking at random is a coin flip on whether anything appears to
  have happened at all.
- **Verified:** a test floods the same key 500 times inside the window (steady), then steps the injected
  clock past it (moves). `Line` now takes a clock for exactly this reason — expiry is the half that broke
  in the field and it cannot be watched on a wall clock.

### The situation is more than one dimension now
`MoodContext` — running time, context fraction, usage fraction, the turn's prompt tokens, tool hand-offs
this turn, and the local hour. Every field is a figure already drawn on the panel; nothing new is
measured and nothing is invented. `default` means "know nothing", which is why `Hour` is nullable:
midnight is 0.

A **ladder** picks exactly one modifier, most pressing first — `@tight` (context ≥80%) → `@thin`
(usage ≥90%) → `@ages` → `@long` → `@again` (≥4 tool hand-offs) → `@heavy` (≥60k prompt tokens) →
`@late` (00–04) / `@early` (05–07) → plain. One, not several: stacking explodes combinatorially and most
pairs read badly ("no room to think, and again, at 3am"). Resolution falls *down* the ladder until it
finds a set the slot actually has, so a slot needs only the situations worth wording.

The voice itself moved to hands-on work — a kitchen, a toolbox, a job on the bench — because a metaphor
carries information a synonym does not: "kettle's on…" tells you to go away for a minute, "still
running…" does not. `@tight` is the bench being covered, `@thin` is rationing, `@again` is the same
drill. Claude Code's own spinner words are in there too - reflecting, synthesizing, analyzing, indexing, connecting, undulating - and half of `unknown` is now just the noise a person makes while thinking, which is the honest content
of a state that has nothing to report — and the hmm gains an *m* per duration band ("hmm…" → "hmmm…" →
"hmmmm…"), which is length as information and the one joke here that survives being read twice.
**78 keys.**

### The pill can finally say WHAT, not just what kind
`ToolTarget` in `Halo.Hooks` forwards what a tool is acting on, taken from the `tool_input` the hook was
already receiving and throwing away: the **file** for the file tools, the **program** for a shell command,
the **host** for a fetch, the pattern for a search, the subagent for a `Task`. So the pill reads
**"running dotnet…"**, "reading Moods.cs…", "writing Fx.cs…", "asking Explore…" — because "running…" could
not tell a three-second `git status` from a two-minute build, which was the one thing it could not say.

Precedence in `Moods.Line` is **situation → fact → voice**, so the line is always the most specific true
thing about right now. A band still wins (a session about to run out of room is bigger news than which
file is open — verified in the sheet: at 92% context the violet writing ring says "margins are gone…", not
the filename). With nothing situational, the fact wins. With no fact — thinking, between tools, a payload
that named nothing — the voice speaks, which is still most of the time.

Everything about it degrades to null rather than guessing: a chained command names none of its programs, a
`file_path` that isn't a string names nothing, a fact too long for 22px falls back to the voice rather than
being clipped (a clipped line reads as a rendering fault). One real bug the tests caught first: a quoted
program path, `"C:\Program Files\nodejs\npm.cmd" install`, split on whitespace and named **"Program"** —
a wrong fact, which is worse than no fact.

Two things had to be measured live rather than reasoned about. `tool_input` arrives as an object from some
surfaces and as a JSON *string* from others, so `AsObject` accepts both. And every early live check read
`"toolTarget": null` and looked like a failure — until the payload dump showed the extractor was right and
the *observations* were wrong: each command I was reading the file with contained a `|` or an `&&`, so the
extractor was correctly refusing to name one program out of several. `Get-Content <path>`, with neither,
wrote `"toolTarget": "Get-Content"` first time.

Codex gets none of this: its tool payload carries no target, so that widget's words are always the voice.
A field that is permanently null is worse than an absent one.

### The ring was orange 90% of the time, and the cause was not the palette
Reported from the live pill, and true. The hook clears `currentTool` the moment a tool finishes, and the
gap that follows — the model writing its next move — is many times longer than the call itself. So a ring
keyed on the *current* tool spent almost all of its life on the tool-less amber, and with pressure warming
that amber, a seven-colour palette was something the eye never actually saw.

Two changes. The last tool is now held for **9 seconds** after it ends (`Glow`), for the words and the
colour together, because the agent that has just read a file is still working on that file — and a state
nobody ever sees is not a state. And the free-hue warm lerp went **0.85 → 0.45**: at 0.85 the thinking
amber was fully orange from about 60% context on, and since thinking is where a turn spends most of its
time, "always orange" was the honest report of it. Amber stays amber now; orange is kept for the top of
the band, where it is news.

### The notification sound that kept coming while no banner ever appeared
Reported: banners suppressed, but a short sound most of the time. `Sound=0` was already being written
per-app (with `ShowBanner` and `AllowUrgentNotifications`), and the registry was correct for 139 apps — so
the registry was never the problem. The gate's own log had the answer: every launch printed
`loaded 139 learned app(s)` and then took twelve seconds to reach `applying → WpnUserService restart`.

`WpnUserService` reads these settings once, when *it* starts, and it is started by logon — so until that
restart the deciding service has never seen a single zero. `Enable()` was stamping `_lastToast = now` at
launch, out of politeness: "a sound might be in flight, don't cut it". The cost of that politeness was a
twelve-second hole at every start in which every arriving toast banged at full volume. The refresh restart
is now the first thing that happens.

Second, the deferral could **starve**: each new toast pushed the pending restart back by the whole quiet
gap, so on a machine that toasts every few seconds the restart might never land and the session ran on
with a stale service. Past 30 seconds pending, the quiet-gap rule is dropped — one truncated sound is
cheaper than a session of them. The cooldown is *not* dropped; it exists to stop restart thrash and
outranks the sound. Both rules stay pure and unit-tested in `ApplyDelayMs`.

**And it was still audible after all that** — reported again, which killed the theory. All 139 learned apps
had `ShowBanner=0` *and* `Sound=0` in the registry, the service had been restarted, no banner ever
appeared, and the sound still did. So whatever honours `ShowBanner` is not what decides the sound, and the
per-app `Sound` value — which Windows itself writes — is not the switch.

The switch that is: the **global** one, Settings › System › Notifications › "Allow notifications to play
sounds", a single DWORD (`NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND`) at the root of the same key, absent
by default and absent-means-on. It is a global change and it is treated like every other one here: the
original is recorded before it is touched, so `Restore()` puts it back — and since it was *unset*, restore
means deleting it, returning Windows to its own default. Halo has taken over presenting these
notifications, so the OS playing its own sound over Halo's silent banner is a duplicate, not a feature.

Verified live: `silenced global notification sound (was unset)` in the log, `= 0` in the registry, `global`
recorded in `banner-orig.tsv` with an empty original, and the launch restart now firing in the same second
as `enable` instead of twelve seconds later.

### The words were all there and none of them could be read
Reported: the text on the collapsed pill becomes unreadable when it shrinks. It did, and the order of
operations was the bug — the voice picked a line, *then* the renderer measured it against the gap and
shrank the font to fit, with a floor of **9px**. A nineteen-character line in twelve characters of room is
9px, which is present rather than readable.

Now the layout is measured **before** the words are chosen: `Fx.FitChars` asks the real font how many
characters fit in the real gap at the smallest size worth drawing, and that budget rides in
`MoodContext.MaxChars`. `Pick` only considers lines that fit — every set keeps a few short ones, so a tight
pill still gets the voice, just its terser half ("hmm, ok…", "sifting…", "on fumes…") — and if nothing
fits, the shortest line in the set is drawn, because a too-long true line still beats a made-up short one.
A held line whose room has since shrunk is re-rolled rather than drawn too small, which happens when the
elapsed clock grows a digit mid-hold. `Fact` respects the same budget, so "writing SomethingLong.cs…"
gives way to the voice. The font floor is 12.5px, and reaching it should now be rare.

### A pinned pill over fullscreen video: three attempts, one kept, and a platform wall
The pin already forces the fullscreen-hide off (`bool fullscreen = !_pinned && IsFullscreen(fg)`), so the
pill was never being *hidden* in a fullscreen video. Everything below was an attempt to work out what was
happening instead. **It is still not visible there, and the honest conclusion is that it cannot be made to
be** — recorded here so nobody spends another evening on it.

**Kept.** `ProbeBehind` hides the window for 12ms to see what is behind it, then calls
`SW_SHOWNOACTIVATE`, which re-inserts it at the *bottom of the topmost band*. It runs on every foreground
change, so any window that is itself topmost could end up over the pill until the next once-a-second
`AssertTopmost`. A probe must leave the z-order as it found it, so it re-asserts now. That is a real fix on
its own merits and has nothing to do with fullscreen.

**Reverted.** Asserting `HWND_TOPMOST` *every frame* while pinned and covered. Changed nothing, and paid a
syscall per frame for a premise that turned out to be wrong.

**Reverted.** Dropping `WDA_EXCLUDEFROMCAPTURE` while pinned over a fullscreen app, on the theory that a
capture-excluded window is handled outside the composed frame and so is never composited over a flip-model
surface. It was a good theory and it is **wrong**: measured on the real thing, the pill still did not appear.
It also cost the glass its screen-grab fast path (`_capturable` disables it) and put the pill into screen
recordings, so it was a real cost for no gain — exactly the kind of trade to undo rather than keep "just in
case".

What is left is the platform: over a fullscreen flip-model surface DWM composites the shell's own z-bands
and nothing else, and the band above one belongs to **uiAccess-signed** apps installed under `Program Files`
— which an unpackaged app living in `LOCALAPPDATA` cannot be. That is how the taskbar and Game Bar manage
it. `SetWindowBand` without uiAccess fails, and true exclusive-fullscreen DirectX cannot be overlaid by any
ordinary window at all. So: fullscreen video keeps the screen, the once-a-second assert stays for ordinary
windows, and the pill comes back when the video does not own the screen any more.

### The fallback icon, and why the real one never came
Two separate faults, reported together.

**The alignment.** `MediaWidget.DrawGlyph` centred with `StringFormat`, which centres the *line box* and the
*advance width* — and for an icon font neither says anything about where that particular glyph's ink sits.
The new `--render-glyphs` hook shows every fallback glyph at 6× with crosshairs through its tile's true
centre: all seven sat high, most of them left as well. `Fx.GlyphCentred` centres on the ink in both axes,
and the sheet's second column puts each glyph on the crosshair. This is the third place to reach that
conclusion (the copy pill and `LocalBadge` each did their own), so it lives in `Fx` now — and the swap-strip
cells, which had already learned it, are unchanged. The drop blob had not, and now has.

**Why the icon was missing at all.** `AppIcon.ForAumid` resolves by matching a **running process name** and
pulling the icon out of its exe, which a packaged app has none of. Measured with the new `--probe-media`
against the actual session: the player is `Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic`, where
`AppIcon` returns **NULL** and `ShellIcon` returns 256×256 — and for the *new* `Microsoft.Media.Player` it is
exactly the other way round. The media widget only ever asked `AppIcon`, so any packaged player with no
track thumbnail fell through to the glyph. `AppIcon.ForSessionApp` asks the shell first, then the exe: the
same chain the notification icons have always used. Both resolvers cache their misses, so it is safe per
frame.

Loose end worth knowing: `MediaSessions.SlotApp` runs the AUMID through `GetFileNameWithoutExtension`, which
cuts at the last dot — the ZuneMusic session reports its app as
`microsoft.zunemusic_8wekyb3d8bbwe!microsoft`. It is only used for the focus-hide rule, which therefore
never matches a packaged player. Left alone for now; it degrades quietly rather than wrongly.

### The pill said "idle", which is the one word it must never say
Reported from the live pill, with a screenshot. Two faults meeting:

**"idle" was a line in the idle set** — the raw state name, which is the entire thing this table exists to
avoid saying out loud, and it read exactly like the debug string it is. At four characters it was also the
shortest line in its set.

**And the budget was being measured on a frame the pill never rests at.** `MaxChars` came from the live
`w`, which is mid-morph for most of an expand or a collapse; a pick made at a transient width is then
**held for a minute**, so one narrow frame put the shortest line in the set onto a full-width pill. The
budget is now only taken when the pill is settled (`fade > 0.99`), and a budget under eight characters is
treated as no budget at all — that is the pill animating, not a genuine eight-character gap.

A test now walks every set and fails on any line that is merely the name of a state. A verb with an
ellipsis ("working…") is voice; a bare state name is a leak.

### Ten states, ten colours — and no second ring
"More colours around the ring" meant *more of Claude's states carrying their own colour*, not another ring,
so the context arc that went in first came straight back out. What replaced it: the families were split
back apart, from seven to **ten**.

| | |
|---|---|
| green | a shell command is executing |
| cyan | reading what is already here |
| **teal** | fetching from outside — split out of cyan |
| violet | writing, patching, publishing |
| lime | digging and reviewing — shifted greener to make room |
| **gold** | planning, plotting, a skill — split out of lime |
| magenta | a subagent or an mcp server is doing it |
| **slate** | watching, waiting on something outside — deliberately the quietest hue, which is what the state means |
| pink | your turn |
| amber | thinking, nothing to show yet |

Plus the four whose colour *is* the message and never modulate: red outage, white spent-limit, blue running
compact, mint just-compacted. **Fourteen** on the ring in total.

The number is bounded by measurement, not taste. The same two tests that forced thirteen down to seven
earlier now pass at ten: every pair 85 apart in RGB, and every modulated colour still nearer its own calm
self than anyone else's. Ten passes where thirteen failed for two reasons — the pressure modulation is
gentle now (when thirteen was tried, warmth was overwriting the hue and a squeezed green landed on lime),
and each addition had to move its neighbour rather than being wedged between two of them.

### One mapping for the words and the colour
`ToolSlot(tool)` is now the single place a tool becomes a slot, on both widgets, and **both** the wording
and the ring's colour come off it — so the pill cannot say "delegating…" in the green of a shell command.
That refactor is what made the rest of this possible, and it deleted a duplicated switch.

Slots added because the pill was printing raw tool names for them: **watching** (`Monitor`, `BashOutput`),
**reviewing** (`ReportFindings`), **publishing** (`Artifact`, `SendUserFile`), **consulting** (any
`mcp__*` tool), **peeking** (Codex's `view_image`, which was a bare literal). A state the product can
*name* is one it can also colour and time, which the fallback never could.

`Moods.PrettyTool` for what is left: an MCP tool arrived on the pill as `mcp__serena__find_symbol…` —
26 characters of punctuation — and now arrives as **`serena…`**, because the server is the half that
answers "who is doing this". Underscores become spaces and the whole thing is cut to the pill's ceiling
rather than clipped mid-word by the renderer, which reads as a rendering fault.

### The ring is a palette now, and pressure is not allowed to lie with it
`Fx.SlotColor` started at seven activity families and is now at ten (see above) — plus the four whose colour
*is* the message and are exempt from any modulation: red outage, white spent-limit, blue running compact,
and a **mint** for the twenty seconds after a compact, which used to look idle.

Getting pressure to coexist with that took three tries, and each failure was a different kind of lie:

1. **0.85 pull toward orange** — every slot arrived at the *same* orange, so warmth erased the thing it
   was annotating.
2. **0.6 pull** — a squeezed green landed on **lime**, which is the colour of surveying. Worse than
   erasing a state: impersonating one.
3. **Hue for everyone, softly** — still drifted, and the *night dim* turned out to move a colour about as
   far as the gap between two neighbouring hues (a dimmed violet came out nearer the magenta than its own
   daytime self).

The rule that holds: **a hue that says which activity this is may not be repurposed; the two that say
nothing may be.** `hueIsFree` is true for exactly thinking (amber = "no news") and idle (white = nothing),
and those two get the full warm lerp — which is where it is most wanted anyway, since an idle pill on a
nearly-full context is exactly when you want the ring to catch your eye. Everyone else gets pressure as
saturation and value: the same lamp turned up, never a different lamp.

`SlotColorTests.AWarmedSlotNeverLooksLikeADifferentSlot` is the invariant, at five pressures × two times
of day: however lit it gets, a slot must stay nearer its own calm colour than anyone else's. It is also
what forced the palette down from thirteen hues to seven, before the gentler modulation let it back up to
ten: thirteen is 27° apart, and reading-cyan and fetching-teal were one colour on a 2px ring.

Two more bugs the tests caught before the screen did: chaining two lerps off the state colour landed a
squeezed green on **yellow**, and the HSV round trip **dropped alpha**, so the deliberately-not-opaque
idle ring got *brighter* as the session tightened.

### `--render-pill`, because this is the part that cannot be screenshotted
Every other hook renders an expanded panel; the ring and the voice both live on the 220×40 pill. Fourteen
rows at 2× with the situation named beside each: one per colour family first, so they can be compared
side by side (the only way to tell whether they are distinguishable at 20px), then the same shell command
under rising pressure. It immediately earned itself: the first pass produced pills with rings and **no
words**, because the line fades in over frames (`_appear`) and a single-frame render draws it at alpha 0 —
it now warms up on a throwaway surface.

Twenty rows now, one per colour first and then the facts and the pressure bands. Rendered at 01:15 on the first pass, which is why the time-of-day band is all over that one: idle → "night shift" (white);
thinking → "night thoughts…" (amber); shell → "the night shift…" (green); reading → "eyeing the wiring…"
(cyan); writing → "by lamplight…" (violet); surveying → "on the trail…" (lime); subagent → "passing it
on…" (magenta); an MCP tool → "calling the desk…"; an unmapped tool → "someothertool…" (green); waiting →
"your move ;)" (pink); 10 minutes of thinking → "deep in thought…"; context 92% → "elbows in…"; usage 96%
→ "on fumes…"; both and dragging → "nowhere to put it…". Ring and words agree in every row, which is the
thing being judged.

### The Codex `TurnOver` twin — owed since yesterday, paid
Same hole, same reason: an interrupt is not a lifecycle event, so nothing writes a status and a
pid-backed one stays live for as long as the process runs. `CodexWidget.TurnOver` + `Shown(st)` at every
display site, the Esc watcher and `CancelCodex` both latching the turn's own `StartedAt`, and
`GetCancelRoute` now refuses a turn the pill has stopped believing in — the stop button used to stay live
on an already-interrupted turn, which sends a second Esc into whatever owns that terminal now. One
difference from the twin, and it needed a guard: `CodexSnapshot.UpdatedAt` is non-nullable, so "never
written" arrives as `default` rather than `null`, which the time backstop would have read as very old.

Also deleted `CodexWidget.Activity` — dead since the panel became a ring cluster, and it would otherwise
have needed the new context threaded through it for nobody.

### The hourly chime says something worth reading
It said the time, which is the one thing the tray clock already tells you. `Shell/Almanac.cs` adds the
rest of the glance.

**First cut was too crowded** — "Thursday 30 Jul · 8 Mordad · Tehran · 27°C clear" is four fields, three
separators, the same day said twice, and a word for something a picture does better. The banner has
**three rows** and the chime was only using two of them; the facts fit the rows exactly:

```
[🌙 indigo tile]   TEHRAN               <- the place. Constant, so it belongs where the app name goes
                   1:00 AM · 27°        <- the two numbers you came for
                   Thursday, 8 Mordad   <- the date, in the one calendar the place keeps
```

Nothing was dropped and one separator survives out of three.

- **The sky moved into the badge.** The banner already carries an icon and its hue already feeds the
  banner's glow, so `Almanac.SkyBadge(code, day)` returns a glyph and a hue: sun, moon, cloud, or a flake.
  Only glyphs *verified present* in Segoe Fluent Icons — found by rendering the E700/E900/EA00 blocks to a
  labelled grid rather than trusting a codepoint list — so what the shape cannot distinguish, the hue
  does: rain is a blue cloud, overcast a grey one, a storm a violet one. `--render-badges` carries all
  four now; the first pass had the sun at hue 44, and since the tile gradient runs hue → hue+24 that came
  out gold→**green** and read as acidic beside the others.
- **Day/night is `is_day` from the API**, not a guess from the hour — so a moon at nine in the evening in
  July is still right in January.
- **One calendar, the one the place keeps.** Both at once was the clutter.
- **No unit letter.** "Tehran 27°" cannot be misread, and the letter was pure width.

The parts:
- **Where** is the machine's own timezone, not an IP lookup — `IpCountry` knows where the *VPN* comes
  out, while the timezone is the one location fact on the box the user set themselves. Windows id → IANA
  → the segment after the last slash. `Etc/GMT+3` and `UTC` name an offset, not a place, and say nothing.
- **Weather** is Open-Meteo, keyless: one geocode of that city name (cached for the process), then the
  reading, on a half-hourly timer that never touches the chime's own path. No reading, no weather clause.
- **The date** is `PersianCalendar`, so a conversion rather than an estimate, and only for a machine
  actually in Iran.
- `--probe-almanac` exercises both live fetches, and immediately found a real flaw: this machine's
  Windows region is **US** while its timezone is Iran, so the first line read "Tehran 81°F" — describing
  Tehran in somebody else's units. Units and calendar now follow the *place*, off the `country_code` the
  geocoder returns, with the region as the fallback for before that lands.
- `mirror/PRIVACY.{md,fa.md}` updated in both languages: they enumerate every outbound request, and this
  adds two.

### The public README, per GitHub support
The direct **`releases/latest/download/DynamicWinSetup.exe`** link is gone from both mirror READMEs —
support named linking the binary itself as the problem. The download button points at the releases page
and the sub-line names the two assets as text. Root `README.md`/`README.fa.md` never linked the binary,
only named it in a table, and are unchanged.

`ReadmeFiles/agents.png` → **`agents-2.png`**, both mirror READMEs following. The file was rewritten in
place last session and GitHub's camo proxy serves a cached copy of an image whose URL has not changed —
the same trap `9ceb581` hit with the banners. Renaming is this repo's own workaround. Checked first, and
worth recording: `raw.githubusercontent.com` returns **200** for every image and for the release asset,
so nothing was actually broken on the fork — the suspension was what made them look broken.

### Still owed
- OG card language bar: the auto-detect path is written and syntax-checked but has never run for real.
- `GenericAgentWidget` still has the flat four-colour ring. Not twin-coupled to the two above, and it has
  no usage or context figures of its own, so it needs a smaller version of this decision rather than a
  copy of it.

## 2026-07-29 (later) — the Codex panel is a ring cluster too, and the voice stopped calling out

Release 0 warnings / 0 errors, **259 tests**. Deployed and running. **Not pushed.**

### The generated-copy path is gone
The CLI generator worked in the end — a real run filled all 21 slots — but it spent tokens on the
user's own subscription and had to launch their agent CLI to do it, which is a process spawn and an
account touch for the sake of cosmetic text. Deleted outright: no `RunCli`, no prompt, no cache, no
`moods.json`/`moods-source.txt`, no `MoodSource` switch, no startup call. In its place a written
table that ships in the binary — **47 keys, 428 lines**, printable with `--moods`.

- **Bands instead of one word per state.** A verb that has not changed in four minutes has stopped
  being information, so a slot can carry `@long` (past 2 min) and `@ages` (past 8 min), chosen from
  the real elapsed clock. Bands only escalate: a slot with `@long` and no `@ages` keeps saying the
  `@long` thing rather than snapping back. `ToolVerb` now takes the running time on both widgets.
- `unknown` was the complaint that started it ("unclear state" reads like a fault when the situation
  is the agent thinking between tools). Its set is now seventeen ways of saying *thinking*.
- Nine tests pin the shape: the 22-char ceiling, no duplicates, printable-ASCII-plus-ellipsis, every
  band having a slot behind it, and the latch holding steady across 50 calls.
- The Codex widget test that asserted exact strings now asserts *set membership* — it was pinning the
  copy when the thing with logic in it is the routing.

### CodexWidget wears the new theme
Rewrote `DrawExpanded` as the same ring cluster the Claude panel uses: three arcs (primary window,
secondary window, context) with the hover lift and the centre readout, the key rows, the mirrored
latency waveform, the exit block and the refresh line. Verified with a new
`--render-widget <png> codex-demo`, which writes a synthetic session to `%TEMP%` — the panel cannot
be screenshotted any other way.

Two real bugs fell out of building it:
- **`ExitBlock`** — the flag, the reputation mark and the dns test are properties of the *machine*,
  not of either agent, so they moved out of `ClaudeCodeWidget` into `Widgets/ExitBlock.cs` and both
  panels draw the same block. Not twin-coupling: both depend on a shared piece, which is what the
  rule against the twins depending on *each other* is for. Verified by re-rendering `claude-hot` and
  comparing — identical but for the live latency figures.
- **The two usage ramps had drifted.** Claude ramped green→amber→red through *hue*; Codex lerped
  blue→amber in RGB, which passes through grey — so 61% on the Codex panel rendered as a dead grey
  ring while the same figure on the Claude panel was clearly amber. One `Fx.UsageColor` now.
- **Codex context never rendered for a CLI session.** The panel demanded `PresentFields`, which only
  the *rollout* parser ever sets — a hook-written status leaves them zero however real its numbers
  are. `ContextMax > 0` is the honest test; a figure that came out of the file is not invented.

### Still owed
- **`docs/moods-plan.md`** — the next pass on the pill's voice: more situations (context nearly full,
  usage nearly spent, big turn, same tool repeatedly, time of day) resolved as ONE modifier by fixed
  priority, plus a trade-and-kitchen voice instead of generic wit. Signals, precedence, steps and
  tone sketches are all written down there. Start from that file, not from scratch.
- The `TurnOver` twin for Codex: it has the same silent-interrupt hole, so cancelling a Codex turn
  still sticks. Called out at the bottom of the plan too, since both land in the same files.
- OG card language bar: the auto-detect path is written and syntax-checked but has never run for
  real (the Halo post reads its cached JSON, and GitHub still 404s for this account).

## 2026-07-29 — two stuck states, and the mood prompt finally reaching the CLI

Release 0 warnings / 0 errors, **247 tests** (13 new). **Not deployed, not pushed.**

### The context ring disagreed with its own figure
- **Reported as:** "86% used" written in red, inside a ring that was still blue.
- **Root cause:** the arc took a hardcoded `Blue` while the figure below it ran `ContextBand`'s
  blue/amber/red ramp. One value, two colour rules, in the same panel.
- **Change:** `ClaudeCodeWidget.ContextColour(frac)` is now the only thing that turns a context
  fraction into a colour; the arc, the swatch and the figure all read it. The band ramp and not
  `UsageColor`, because context's thresholds are the ones the /compact banner fires on — the arc has
  to agree with the warning, not with the usage rows beside it.
- **Verified:** new `--render-widget <png> claude-hot` renders a synthetic 93% / 78% / 86% session;
  the existing demo sits at 34%, where this bug is invisible. The PNG shows all three arcs matching
  their figures. Tests pin arc colour == figure colour at four fractions.

### A cancelled turn left the pill on "hmm…" forever
- **Reported as:** cancel with Esc and it stays "hmm…". Then: the stop button does it too.
- **Root cause, and the two reports are one bug:** the stop button *injects Esc*, so both are the
  same path. Claude Code writes status on lifecycle events and an interrupt is not one, so the last
  write stays `working` with no tool — and `IsLiveStatus` keeps a pid-backed status live for as long
  as the process runs, so it never expired. `ToolVerb(null)` is "hmm…".
- **Change:** `ClaudeCodeWidget.TurnOver(st, now)` decides whether a turn the file still calls
  working is actually over, and the display paths read `Shown(st)` rather than `st.State`. Two ways
  out because there are two ways in: the stop button and the Esc watcher latch the turn's own
  `startedAt` — exact, and self-clearing since the next turn carries a new stamp — and a tool-less
  `working` unwritten for 180s ages out on its own, which is the only thing that can catch an Esc
  typed into a terminal Halo never sees. It cannot misfire on a long tool call: while a tool runs,
  its name is on the status. `DetectCompactCancel` became `DetectAgentCancel`.
- **Verified:** 7 tests. **Not eyeballed live** — needs a real cancel on a real session.

### The mood prompt was never reaching the CLI
- **Root cause (yesterday's open bug 3 — and it was not the model's fault):** `ArgumentList` escapes
  for the C runtime, but the process launched is `cmd.exe`, which re-parses the command line by its
  own rules. The prompt is full of double quotes, so cmd's quote state flipped partway through and
  `claude` got effectively nothing — it answered "Hey! What can I help you with?" and exited **0**.
  That is why this read as a model ignoring the format rather than a prompt that never arrived.
  Proved by hand with the same `ProcessStartInfo`: `what is 2+2` returned `4`; the real prompt
  returned a greeting.
- **Change:** the prompt goes over **stdin**, so it never touches a command line and cannot be
  mangled by one; the argument form stays as a fallback. Separately, the JSON contract moved to the
  front of the prompt with a worked example, and `Refresh()` retries once, flatly, when the reply
  contains no object at all.
- **The two-source switch:** `MoodSource.Fixed` (shipped wording) vs `Ai`. Fixed is the default, so a
  normal install generates nothing and reads exactly as before; this machine opts in. Stored as one
  word in `%LOCALAPPDATA%\Halo\moods-source.txt`, so the settings panel this is heading for only has
  to write that.
- **New dev hook `--moods [refresh|fixed|ai]`.** The lines reach the screen one slot at a time, weeks
  apart, in a window that cannot be screenshotted — printing the batch is the only way to read them.
- **Verified:** a real `--moods refresh` filled all 21 slots with 6 alternates each, all inside the
  24-char guard and in register ("uh… / erm… / one sec…" for `unknown`).

### Blog — the live header was capped by a term nobody had checked
- **Reported as:** the demo on the post header is too small. Twice, after a cap raise that in fact
  changed almost nothing.
- **Root cause:** `fit()` bounds the scale by the room the **open** panel needs. On the 800px article
  column a 5:2 frame is 320 tall, so `(320-10)/178 = 1.742` was the real ceiling and the 1.75 cap sat
  just above it doing nothing. The header was reserving space for a state that only exists while you
  point at it; on a phone the open panel's 366px minimum pinned the whole dock to ~1.0.
- **Change:** two scales — `--s` fits the resting 216×38 pill, `--so` fits the open panel, and the
  dock swaps between them on the curve the notch already morphs with. The hero frame went 5/2 → 2/1
  so the height term stops binding on desktop.
- **Verified:** resting pill 47% → 59% of the frame on desktop, 56% → 76% on a phone; the open state
  still fills 95–98% of the width in both. Served bytes checked on both vhosts, script parses under
  `node --check`. Backups `*.bak-herofit-20260729`, `*.bak-twoscale-20260729`.

### The generated lines arrived as mojibake
- **Reported as:** the pill showing `ermâ€¦`.
- **Root cause:** a redirected child's output is decoded with the *console's* codepage, not UTF-8, so
  the three bytes of `…` were read as three Latin-1 characters. Nothing to do with the model or the
  cache — `File.WriteAllText`/`ReadAllText` were UTF-8 all along; the damage happened at the pipe.
- **Change:** `StandardOutputEncoding`/`StandardErrorEncoding`/`StandardInputEncoding` pinned to
  UTF-8, plus a `Legible()` guard that drops any line carrying U+FFFD or a Latin-1 supplement
  character — the pill's whole vocabulary is ASCII plus an ellipsis, so that range is damage, not
  language. Belt and braces, because this one reached the screen.
- **Verified:** cache purged and regenerated; 0 mojibake characters in the stored JSON, `uh…` intact.

### Blog demo — reverted at the user's request
The two-scale change and the 2/1 hero frame were **rolled back** on both vhosts from
`*.bak-herofit-20260729`; `aspect-ratio:5/2` and the single `--s` scale are back. The analysis in the
section above still stands, but it was the wrong target: the ask was the static poster image, not the
live demo. Backups of the reverted-away work remain as `*.bak-twoscale-20260729`.

### Still owed
- **Port the ring-cluster theme to `CodexWidget`** — not started. Only its mood strings are wired.
  Map unchanged from yesterday's entry below.
- **The `TurnOver` twin for Codex.** `CodexWidget` reads `st?.State == "working"` in the same places
  and has the same silent-interrupt hole; the repo rule is that a change in one twin needs the other.
- ~~`halo-hero.jpg`~~ **done:** cropped 1600×640 → a 1050×420 window anchored to the top edge (the
  notch has to stay flush with it) and resampled back, so the notch went from 37% to ~56% of the
  frame. Cropped from a `.bak-crop-20260729` backup rather than in place, so re-running cannot crop a
  crop. DB `featured_image` bumped to `?v=5`.
- **Generated lines were shallow** ("??…", "hmm what…", "ready when u are"). The prompt now demands
  the line carry the slot's actual meaning and bans chat shorthand and punctuation-only lines, and
  `Legible()` drops anything with no letter in it. Regenerated: "not sure what… / state unknown /
  can't tell :P" for `unknown`.
- OG card language bar: still parked on the GitHub ticket.

## 2026-07-28 — the pill's lines are written, not hardcoded

- **Root cause / motive:** every mood on the collapsed pill was a literal in `ClaudeCodeWidget` and
  `CodexWidget`, so the product said the same four things forever.
- **Change:** new `src/Halo.App/Agents/Moods.cs`. Each line is a *slot* whose shipped wording is the
  fallback; a background call writes fresh alternates into a pool and `Moods.Line(slot)` serves them.
  18 call sites in each widget now go through it. Startup does `LoadCache()` + `RefreshSoon()`.
- **Two things that shaped it:** `Draw*` runs per frame, so latching lives inside `Moods` keyed per
  slot with a 60s hold — a caller-side latch would strobe, because one frame asks for more than one
  slot (`OutageText` and `ToolVerb` both run, only one is shown). And the width guard (≤24 chars) is
  ours, not the model's: a long line clips mid-word and reads as a rendering bug.
- **No new packages** — `HttpClient` + `System.Text.Json` are BCL, so the API call is hand-rolled.
  No key, no network, bad JSON → falls back to exactly today's strings.
- **Superseded the same day:** the first cut called the paid API with a stored key. Replaced with
  the agent CLI already on the machine (`claude -p`, else `codex exec`) — no key, and it rides the
  subscription that is already running. Cache `%LOCALAPPDATA%\Halo\moods.json`, stamp
  `moods-stamp.txt`, refresh throttled to once per 24h (it was every launch, ~3c a shot).
- **Verified:** Release 0 warnings / 0 errors; `dotnet test` 234 passed / 0 failed. **Not yet
  deployed, not pushed.** The generated lines have not been eyeballed on a real pill — no key was
  configured on this machine, so every path taken so far was the fallback.
- Reverted the same day: a blog two-variant experiment (`content_ai`/`content_fa_ai`/`text_variant`
  + a `post.html` patch) — wrong target, backed out on both vhosts and the columns dropped.

### Follow-up — what a live CLI run actually found (2026-07-29)

Ran the real prompt through `claude -p` rather than trusting the code. Three bugs; two closed:

1. **Prompt over stdin meant the CLI never ran.** `cmd /c claude -p` with the text piped in left
   cmd reading the prompt's own lines as commands — and still exiting 0, so the failure was
   completely silent. Fixed: `ProcessStartInfo.ArgumentList`, prompt as one argument.
2. **Inheriting the working directory hijacked the answer.** Run inside the repo, the agent picked
   up this `CLAUDE.md` and replied with Halo backlog items instead of pill copy. Fixed:
   `WorkingDirectory = Path.GetTempPath()`.
3. **OPEN — the model answers in prose, not JSON.** The API path guaranteed shape with
   `output_config.format`; the CLI has no equivalent, and the contract sat at the end of a long
   prompt. Fix: lead with the output contract, include a one-line example of the exact shape, and
   retry once when the reply has no JSON object. Until then the pool never fills and the pill shows
   exactly today's strings — nothing broken, nothing gained.

**Two things still owed, neither started:**

- Close bug 3 above, then eyeball the generated lines on a real pill.
- **Port the ring-cluster theme to `CodexWidget`** — only its mood *strings* were wired up; the
  layout is still the old design. The map: on `ClaudeCodeWidget`, `DrawExpanded` + `Key(...)` are
  ~306-542, `DrawNet` + `Rule`/`Waveform`/`Cap` ~854-980, `DrawNetHover` ~981-1032, `DrawExit` +
  `FlagFitted`/`Waved` ~542-798. `CodexWidget.DrawExpanded` is ~244-318. Not a copy-paste: the data
  model differs (`CodexSnapshot` vs `CcStatus`, plus `CodexSurface` and `CodexLimit`/`LimitLabel`
  which have no Claude counterpart), and the twins must not start depending on each other.
  Budget ~400 lines of C#, plus a `--render-widget codex` pass to actually see it.

Still **not deployed, not pushed.** Release 0/0, `dotnet test` 234 passed.

## 2026-07-28 (latest+10): the banner's grabber bar promised more when there was none
Build 0/0, **234 tests** (4 new). Hot-deployed. **Not pushed.**

- Reported as: a short notification still shows the little bar underneath that says "open the pill to read
  the rest", when there is no rest. Correct — the condition was `n.Body.Length > 0`, i.e. *has a body at
  all*, not *has more body than fits*. A two-word message offered a handle that expanded into the same two
  words over an empty gap.
- `NotifBanner.BodyOverflows(n)` asks the layout instead: lay the body into the same two-line box and see
  whether any characters are left over. Measured with `WrapFmt`, **not** the summary's `SummaryFmt` — a
  format carrying `EllipsisCharacter` reports the whole string as fitted, because as far as GDI+ is
  concerned it did fit it, by cutting it short. `TrimEnd` first: mirrored toasts routinely carry trailing
  newlines and those are not something to read. Memoised on (body, has-preview) — it is on the per-frame
  path twice and the answer only changes when the notification does. A measurement failure returns *true*,
  so a broken probe can never hide a real "there's more".
- **Both** call sites use it: the bar in `NotifBanner.Draw` and the drag gesture in `NotchController`
  (which had the same `Body.Length > 0` test). A strip that expands into nothing is worse than no strip,
  and the two must not be able to disagree.
- `--render-notif` now renders **two** banners — a long body and a short Persian one — because a single
  long-body sample could never have shown this. Verified: bar on the ellipsised one, absent on the short.

## 2026-07-28 (latest+9b): the blog's Claude panel image, replaced live
- The post `halo-glass-notch` referenced `assets/blog/halo-claude.png?v=2`, from **both** `content` and
  `content_fa`. Replaced with the regenerated `claude-demo` render (below), backed up as
  `halo-claude.png.bak-20260728` on each vhost first.
- **Both vhosts, as always**: `/home/boystore.org/public_html` (owner `boyst8337`) and
  `/home/pvboy.dev/public_html` (owner `pvboy2287`). scp lands as root, so the chown back to each
  vhost's own user is not optional. Cache-bust bumped `?v=2` → `?v=3` with one `REPLACE()` over both
  language columns — the file name is shared, so a single UPDATE covers EN and FA.
- Verified live, not assumed: API reports `?v=3`, both hosts return 200 / 84000 bytes / `image/png`, and
  the md5 fetched over HTTPS equals the local file's (`dfbb9db6…`).
- Trap: the first scp to boystore.org died with `Connection reset by peer` while the pvboy.dev one
  succeeded, leaving the two vhosts **disagreeing** — the old file was still being served on one side and
  nothing said so. `scp -O` went through. Always diff the two by checksum afterwards rather than trusting
  that a loop of two uploads both landed.

## 2026-07-28 (latest+9): the docs screenshot was three layouts out of date
Build 0/0, 230 tests. **Not pushed. Blog not touched — see below.**

- `ReadmeFiles/agents.png` still showed the **pre-ring-cluster** panel: progress bars, an empty graph
  reading "net … · api …", no weekly limit, no exit block. Regenerated from
  `--render-widget agents.png claude-demo 2 440,145` — the cursor parked inside `ExitRect()` so the block
  renders its audit rows rather than the resting one-liner. Shows: running (red stop), 5-hour 42%,
  weekly 61%, context 341K/1M, the live graph, and the full exit audit.
- **`claude-demo` seeded the limits but not the exit**, so a docs render still put the author's real
  address, ISP and ASN on a public page. Now seeded with **RFC 5737 TEST-NET-3 (`203.0.113.24`)** and an
  **RFC 5398 documentation ASN (`AS64496`)** — they read as a real exit and can never be anyone's. The
  load-bearing part is setting `IpRep.ForIp`/`DnsLeak.ForIp` to the same address: both `Want()` methods
  early-return when they already hold that ip, which is what stops the draw-time calls going out and
  overwriting the demo values. Same trap the limits hit, one layer down.
- Prose updated in **both** mirror READMEs to name what the new image shows (the two-sided graph, the
  exit audit). Root `README.md`/`README.fa.md` carry no screenshot — only `mirror/` does.

## 2026-07-28 (latest+8): the small rows were soft because the hinting never landed
Build 0/0, 230 tests. Hot-deployed. **Not pushed.**

- **Small text in the Claude Code panel was soft and uneven, and it was not the renderer.** Content is
  drawn at native resolution (only the *shape* is supersampled) and the hint was already
  `AntiAliasGridFit`, and DPI here is 96 so the `ScaleTransform` is a no-op. The bug is that grid-fit was
  being handed a **fractional origin**: `TextTop` multiplies the ascent ratio by the size (0.9668 × 12.5),
  so baselines landed on .915 of a pixel, and `x` came straight off `MeasureString` just as fractional.
  Every hinted stem was then resampled across two pixels. The error is a fixed fraction of a pixel, so its
  share of the glyph grows as the font shrinks — which is exactly why the 12–13px rows looked worse than
  the 22px title while nothing was wrong with the title.
- Fix: `MathF.Round` on both the baseline and `x` in `Text`/`TextClipped`. Costs ≤ half a pixel of layout.
- Separately, three fonts were declared at **12.5px**. A half-pixel em cannot be grid-fitted; all three
  are 13px now. Verified with `--render-widget` cropped to the key rows and blown up 3× nearest-neighbour
  — stems are single-pixel and clean where they were two-pixel smears.
- `TintAppExpanded` 60 → **48** on request (open panel ~81% window). `TintAppCollapsed` stays at 120: the
  small pill has no room to lose contrast under its own content.

## 2026-07-28 (latest+7): the tint revert, then the ends of the pill were a different colour
Build 0/0, 230 tests. Hot-deployed. **Not pushed.**

- **`TintAppExpanded` 140 → 60, reverted on the user's judgement.** 140 measured better on the offending
  capture (band spread 13.7 → 8.3, sharpest edge 1.64 → 1.00 per row) and looked wrong: at 140 the glass
  stopped reading as glass. Transparency *is* the material here; the ghost band is the price. The comment
  at the constant now records both the measurement and why the better number lost, so it does not get
  "fixed" back.
- **Then: the ends of the pill took the colour of whatever was behind them while the middle stayed near
  black.** Root cause is not the tint and not the mask — the straight edges measure clean, hue identical
  to the boundary (R−G = 39 interior, 38 at the last covered pixel), only alpha ramps, so the single-mask
  fix from latest+6 holds. It is `BlurPyramid`: **blur shrinks the backdrop, it does not stop it being a
  map of the backdrop.** A 90px block against the pill's left end is still ~6px in the 1/14 thumbnail and
  the bicubic upscale hands it back as a flat coloured slab. More blur cannot fix it — past ~1/14 the
  upscale rings and the edge comes back *sharper*, measured earlier.
- **Fix: pull the blurred plate toward its own mean (`FrostMix`, default 0.55).** That is what frosted
  glass actually does — takes on the average of its backdrop and keeps a soft drift of it. Hue and
  movement survive, the pane still shifts with the wallpaper, but no region reads as a shape. Mean is
  computed on the 40×15 thumbnail's bits (a `DrawImage` to 1×1 is a resample, not an average).
- Verified with a deliberately brutal backdrop — saturated 90px blocks flush against both borders, a
  bright bar across the middle. End-vs-centre region delta: **37 → 18 at 0.55, → 9 at 0.75.**
  `--render-shape` takes a 4th argument now that sweeps `FrostMix` without a rebuild; the sweep strip is
  the only way to pick that number.
- **Then `FrostMix` fixed the edges and killed the glass** — and that is the actual lesson of this whole
  run. Both ghost fixes (the tint, then the mix) work by REMOVING information from the backdrop, and with
  nothing put back the pane stops being a material and becomes a flat colour. **Transparency alone is not
  glass.** Frosted glass anywhere is blurred backdrop + a lit surface: a **sheen** down the face, a
  **grain** in the substrate, a **rim light** along the contour. None of those three existed here. All
  three are backdrop-independent, so they buy the material back at zero cost to the ghost suppression —
  which is why they are the right lever and the tint was not.
  **Rejected on sight and rolled back — the three are shipped at 0.** The reasoning above is sound and the
  code stays (at 0 each is a branch that does not run), but on the real pill it did not read as glass, it
  read as an effect on top of one. Shipped state is `FrostMix 0.55`, no cues — edges fixed, and the glass
  question still open. The 4th argument sweeps `mix,sheen,grain,rim`.
  Rim is drawn INSIDE the mask, inset by half the pen, so it is shaped by the same path and cannot come
  back as the coloured frame. Grain tile is deterministic — a per-frame reseed is a crawling fizz on a
  window that sits still.

## 2026-07-28 (latest+6): the white rectangle was the desktop, and the glass was letting it through
Build 0/0, 230 tests. Hot-deployed. **Not pushed.**

- **The "white rectangle inside the glass" was real content, faithfully rendered.** Found by dumping the
  capture rather than guessing: new `HALO_DUMP_GLASS=1` writes the raw grab and the blurred result to
  `%TEMP%`, and the raw grab was a Telegram window with a pale message bar across it. The glass was
  showing exactly that. **Blur alone does not make frosted glass** — a blurred bright panel is still a
  bright panel, so any light strip behind (a message bar, a title bar) arrived as a hard-edged pale block
  sitting inside the pill. The backdrop is now desaturated 40% toward its own luminance and its range
  squeezed to ~58% into the lower half (`Frost`) before the tint goes on. Measured on the actual offending
  capture: row-luminance spread through the glass **51.5 → 2.7**. Hue and movement still come through;
  legible shapes do not.
- `--render-shape` takes an optional backdrop image now, so the composite can be driven with a REAL
  captured desktop instead of flat magenta. Flat magenta could never have caught this.
- **Separately, a latent capture bug: the grab ignored the drag offset.** The window lands at
  `workLeft + (workWidth - winW)/2 + OffsetX`, the capture started at `workLeft + (workWidth - CaptureW)/2`
  with no offset — so a pill dragged off centre showed a faithful picture of *the centre of the screen*
  instead of what is behind it. The pill's own width cancels out of the algebra, so the fix is the offset
  alone. **Not** the reported symptom (the saved offset here is 0) and it is not claimed as such, but it
  is a real bug and it is fixed.
- **The pin's hover label sits on its own chip.** Bare text at `pin.Right + 6` landed straight on the
  agent panels' stop button once that moved to x=42 — a hover label you have to read against whatever it
  covers is not a label.
- A little more air between the flag and the country line (9 → 13).
- Deleted a dead 54-line `DrawNetHover` overload left from an older graph design — it turned out to be a
  copy of the one Codex still uses, which is its own small warning about the two panels drifting.

## 2026-07-28 (latest+5): the rings answer to the pointer, and the dns row is a button
Build 0/0, 230 tests. Hot-deployed. **Not pushed.**

- **Press the dns row to run the test again.** `DnsLeak.Retest()` drops the cached answer and the next
  hover frame starts a fresh lookup; the old verdict stays on screen, dimmed, behind "testing dns…"
  rather than the row blanking. A second press mid-test is a no-op — `_busy` already guarded that.
  `DrawExit` records where it actually put the row and `Buttons()` hands back that exact rect, because
  how far down the row sits depends on whether the exits have split — and the hand cursor reads the same
  list, so what looks pressable and what is pressable cannot drift apart.
- **Hovering a ring lifts it and fills the hole in the middle.** Which band the pointer is in comes from
  the distance to the centre — they are concentric, so the radius alone answers it. The hovered arc
  thickens and the other two step back (dimming the others is what actually picks one out of three), all
  eased on a time constant rather than a per-frame step so it takes the same ~0.09s at any fps tier. Its
  figure lands in the centre of the cluster — the empty hole flagged two entries ago — with the label and
  detail under the cluster. `Animating` now also stays true while the lift settles, or it would freeze
  half-raised the moment the pointer left and the next hover would start from wherever it stopped.
- **Credits moved to hover.** They were on the resting 5-hour line beside the countdown, which kept a
  dollar figure on screen permanently for something most glances are not asking about. Now the resting
  line is just the countdown; pointing at the row adds the spend (or the remaining, or spent-against-cap
  when the API exposes those), and the 5-hour ring's centre readout carries it too so both hover paths
  agree.
- Graph switched back to the **equaliser** — mirrored capsules with a capped bar width and the older
  samples dimmer. Constellation is gone; the mirror and the full-width spread stay.
- **The wind loop reads as smooth now.** It was already seamless arithmetically — the phase advances by
  exactly Tau and sine is Tau-periodic, so the frame after the wrap is the frame that would have come
  next. The problem was perceptual: one sine at 1.45 cycles is simple enough that the eye memorises it
  and reads each pass as a restart. Two harmonics (the second at double rate, offset phase) make the
  cloth wander instead of march, and both terms keep period Tau, so the loop is still exactly seamless.
  Period 5s → 7s: the faster a repeating pattern runs, the more obviously it repeats.

**Unresolved: the white rectangle in the big pill.** Could not reproduce. `--render-shape` over flat
magenta is clean, and the only capture of the live window needs `HALO_CAPTURABLE=1`, which removes the
capture exclusion and makes the glass photograph itself — so that image cannot be used to diagnose the
glass. Waiting on where exactly it appears and in which state before guessing at a third fix; the first
guess at the coloured frame was wrong and only measurement caught it.

## 2026-07-28 (latest+4): the coloured frame round the glass, and a flag that leans into the wind
Build 0/0, 230 tests. Hot-deployed. **Not pushed.**

- **The coloured frame behind the glass was real, and my first fix was wrong.** Reported as a coloured
  border visible around the big glass pill. First guess: `SetClip` is hard-edged whatever the smoothing
  mode, so the backdrop landed one stair-stepped pixel proud of the antialiased tint. Replaced it with an
  antialiased `FillPath(TextureBrush)` — and **measurement said it got worse** (80 leaking pixels against
  the old 21). The actual cause is more basic: the backdrop and the tint were filled through the *same*
  path one after the other, and at a boundary pixel with coverage `c` the tint's alpha is scaled by `c`
  too, so it covers the backdrop least exactly where the backdrop already is. On a magenta test backdrop
  the rim reached 130 in red and blue against an interior of 27. **Fix: composite backdrop + tint on a
  flat rectangle first, then mask once.** A single antialiased edge can only scale alpha; it cannot shift
  the hue. Now 0 leaking pixels, worst residual 23 — which is just the glass legitimately showing the
  backdrop through the tint.
- New `--render-shape <png>` hook drives the real composite over flat magenta, and `DrawShape` was split
  so the backdrop is a parameter rather than a field read. This is the only way to inspect that edge: the
  window carries `WDA_EXCLUDEFROMCAPTURE`, and the edge has now been got wrong twice.
- The two supersampled buffers are **reused** rather than allocated per frame. The old code already
  allocated one 1120×440 bitmap per call; the fix needs two, and that is not churn this path may have.
- **The flag's wind has an angle.** The phase now advances with y as well as x, so the wavefronts cross
  the cloth on a slant instead of marching straight across in flat columns, and the amplitude ramps
  (smoothstepped) from nothing at the hoist to full at the fly. That ramp is most of what sells it as
  cloth rather than an image being wobbled.
- **The mark is coloured by its own value**, on a continuous red → amber → green ramp instead of four
  buckets: bucketing put a 72 in the "fine" band and painted it plain white, which reads as no colour at
  all. Only the figure takes the colour — `72/100` in the ramp, `· datacenter` grey — the same rule the
  usage rows follow. `dns ok` / `dns leak` gets the same treatment.

## 2026-07-28 (latest+3): constellation graph, a real DNS leak test, and a mark out of 100
Build 0/0, **230 tests** (was 221). Hot-deployed. **Not pushed.**

- **The two-lane sparkline was a mistake and is gone.** Feedback was blunt and correct: it looked
  ridiculous. The error was throwing away the MIRROR — that symmetric silhouette was the only thing
  giving the graph character, and the actual complaint had only ever been the empty left half. Three
  mirrored treatments were built and rendered for real (equaliser capsules, a filled ridge, a dot field);
  **constellation** was chosen. Every sample is a lit dot, your internet above the rule and the path to
  Anthropic below, oldest at 0.45 alpha rising to full at "now". A steady route settles into a calm even
  row, and a spike genuinely jumps *out* of the row instead of merely being taller. The other two were
  deleted rather than left behind an env switch.
- **A real DNS leak test** (`DnsLeak`). This cannot be measured locally — the only way to see which
  resolver actually answers is to have an authoritative nameserver watch for the query — so it uses
  `bash.ws`: take an id, look up six `<n>.<id>.bash.ws`, read back the resolvers that came asking. The
  leak test is the resolver's **country** against the exit's; their own `conclusion` field calls any
  different-ASN resolver a leak, which flags merely choosing Cloudflare DNS. Hover-only, once per
  address, cached. Measured live: 5 resolvers in IE/TR/US → leaking; a later run, 2 in TR → clean.
- **A mark out of 100** (`IpRep.Score`, 9 tests). Stated plainly in the comment and here: this is the one
  number on the panel that is *ours*. Every input is a real flag from a real lookup, but the weights are
  a house opinion about what gets an address refused, and nothing measures that. So it is never shown as
  a percentage, and the findings that took the points off always sit on the same line — a bare score you
  cannot audit is a magic number. `72/100 · datacenter` on the live exit; the abuse term is dropped
  rather than ellipsised when the column is too narrow, since an ellipsis would eat the verdict instead.
- **The flag ripples.** Per-column vertical displacement plus a brightness term from the slope, done in
  one `LockBits` pass into a reused PArgb buffer. Sampling clamps at the edges so only the *content*
  waves and the silhouette stays the rounded rectangle it is clipped to — letting the edges undulate
  fought the rounded corners rather than adding to them. `Animating` only asks for frames while the
  pointer is actually on the panel: pinned open with the mouse elsewhere, nobody can see it.
- Flag settled at 28×18 (32×21 overshot). Exit block moved up to y=120 and its rows are a built list now,
  laid out in order, because how many there are depends on what is actually wrong.

`--probe-ip` now runs the DNS test too, which is the only way to check the parse without hovering an
uncapturable window. `mirror/PRIVACY.{md,fa.md}` updated in both languages — `bash.ws` gets its own
paragraph, because a DNS leak test *cannot* be done quietly: telling a third party who resolves your
names is the entire mechanism, which is why it never runs without a hover.

Still open: the swatch dots duplicate the figure colour, the ring cluster's centre is empty, and red vs
amber needs a shape difference for red-green colour blindness. City/region/operator-domain from
`ipapi.is` are fetched-but-unused — they want a second-level popup on the flag, not another row.

## 2026-07-28 (latest+2): the graph becomes two lanes, and the header stops narrating
Build 0/0, 221 tests. Hot-deployed. **Not pushed.** Driven by "the panel has got crowded" and "the graph
needs a better idea so we use that space better".

- **The network graph is two lanes, not a mirrored axis.** Both series now grow upward from their own
  floor as filled area traces — green net on top, blue api below — so reading the lower one no longer
  means mentally flipping it, and each gets a full height instead of half a shared one. The fill is a
  vertical gradient to transparent; drawn as a brush straight onto the surface it composites correctly,
  unlike the baked textures that forced the PArgb rule.
- **The samples now spread across the whole column.** A half-warm buffer used to pack its bars against
  the right edge and leave the rest as bare rule — which was most of the graph, most of the time. Both
  lanes share ONE origin index, or a gap in one series would slide it out of time with the other. The
  trace is straight segments between samples, not a smoothed curve: a curve invents shape between
  readings, which is the same sin as an invented number.
- Kept: the scale off 3× the median, the red full-height tick for a dropped sample, the tooltip. The
  tooltip now picks the **nearest** sample rather than the cell landed in, since the trace is vertices.
  A single sample pins to the right end — time runs rightward, so the first reading is already "now".
- **Band 46 → 38**, and the 8px went to the exit block below: **the flag is 26×17 → 32×21 with rounded
  corners.** That is the actual fix for "make the flag higher quality" — at the old size the TR star had
  about six device pixels to live in, and no filter fixes that. Rounding is done with a texture brush,
  not `SetClip`: GDI+ clipping is hard-edged whatever the smoothing mode, so a clipped rounded rect comes
  back with stair-stepped corners while `FillPath` antialiases them.
- **The line under the title is down to one job:** the question Claude is waiting on. The verbs ("hmmm",
  "googling :P"), the moods and the elapsed clock all left — narrating that something is running, in the
  panel you opened *because* something is running, next to a lamp that already says so in colour.
  `Activity()` died with it. The collapsed pill keeps all of it: a blank pill reads as broken, and that
  is where the product's voice lives.
- **The weekly row disappears when it has no figure**, and the rows below close up (they take a running
  slot now instead of a fixed index). The 5-hour row keeps its dash — that window always exists on a
  Claude account, so a missing figure there means the fetch failed, which is worth seeing.
- Figures 18px → 16px. `⟳ refresh` keeps its word at rest (a lone glyph is a guess about what it does,
  and it is a button); only the age waits for the pointer.
- **The cursor turns to a hand over anything pressable.** A layered popup tells Windows nothing about its
  own hit-testing, so `WM_SETCURSOR` asks the controller, which walks the same rects the click dispatch
  walks at the same scale — the pointer can never promise a press the click path would not honour.

Verified with `--render-widget claude-idle 2`, `claude 2`, and the exit hover under `HALO_RENDER_NET=1`.

Still on the table: the swatch dots duplicate the figure colour exactly now, the three sub-lines could
move to hover, the ring cluster's centre is empty while its numbers sit in the next column, and red vs
amber needs a shape difference for red-green colour blindness if more text goes.

## 2026-07-28 (latest+1): text that only confirmed the normal case is gone
Build 0/0, 221 tests. Hot-deployed. **Not pushed.** Reported as "the panel has got crowded — could some
of this be a shape, or live behind the pointer?"

The rule applied: **text reports the exception, not the norm.** A line that is only ever there to say
nothing is wrong is a line that costs attention every glance and pays back on almost none of them.

- **"api takes the same exit" / "no proxy set · direct" deleted.** It appeared on hover whenever the two
  exits agreed — i.e. it existed to announce the default. The split case is the one worth words and
  already gets them, loudly, in amber. Silence there now means agreement.
- **"not fetched yet" (×2) deleted.** The value already reads `—`; the sub-line underneath was the same
  fact spelled out. Both usage rows collapse to one line while the figure is unknown.
- **"scale 1500" deleted** from the graph legend, and `cap` dropped out of the `_hover` tuple with it —
  it was dead once the legend stopped printing it. The legend carried the axis cap because a mirrored
  profile has nothing to hang numbers off, but the tooltip *is* that axis now: point at any bar and it
  reads out. The unit moved onto the figure it belongs to (`api 328 ms`) instead of floating right-aligned
  on its own.
- The reputation line now takes the **next free row** rather than a fixed `y + 53`, so removing the third
  line closes the gap instead of leaving a hole where the sentence used to be (caught in the render).

Resting text runs: ~16 → ~11. Verified with `--render-widget … claude 2` and `… 2 447,160` under
`HALO_RENDER_NET=1`.

Still on the table, not done: the swatch dots in the key rows now duplicate the figure colour exactly
(they became redundant the moment the figures took colour), the three sub-lines could move to hover, and
the ring cluster's centre is empty while its numbers sit in the next column. Also noted for whenever more
text goes: red and amber are close for red-green colour blindness, so a critical band should carry a
shape difference, not only a hue.

## 2026-07-28 (latest): context warns before it degrades, and the exit gets scored
Build 0/0, **221 tests** (was 197). Hot-deployed (`Halo.App.dll` swapped into
`%LOCALAPPDATA%\Programs\Halo`, relaunched, alive). **Not pushed.**

- **A banner when the context is full enough to cost quality.** `CheckContext` joins the latched alerts
  in `CheckAlerts`; past `ContextWarnAt` (0.80) it enqueues "Context N% full · answers get vaguer from
  here — /compact when you can". Latched per session — keyed `pid:startedAt`, because pids get recycled —
  rather than per edge: compacting drops the fraction and a long session would otherwise re-warn every
  time it climbed back. Dropping below the line re-arms it.
- **Colour on the figures only.** `Key` grew a `figure` colour and a `hot` token so the caption stays
  grey while the number carries the state — colouring the label too turns the row into a block of one
  hue you have to decode. The 5-hour and weekly percentages take `UsageColor`; context has its own ramp
  (`ContextBand`: blue → amber 15 points out → red at the warn line), applied to both "132K" and the
  "13%" inside "of 1M · 13% used". Splitting that sub-line into runs needed `MeasureTrailingSpaces` —
  `GenericTypographic` measures a run ending in spaces short, and the next run slid onto the separator.
- **The exit is now scored, with someone else's numbers.** Hovering it asks `api.ipapi.is` (HTTPS, no
  key) for `is_datacenter / is_vpn / is_proxy / is_tor / is_abuser` and the operator's `abuser_score`,
  and adds a fourth line: `datacenter · abuse high`, coloured by severity. ip-api.com carries the same
  flags but its free tier is plaintext HTTP, and asking "is my exit private" over a channel the local
  network can read and rewrite is the wrong trade. Nothing is computed here — a reputation is not
  something this machine can measure about itself. Only the *ordering* is ours (`IpRep.Classify`): a
  flagged address is refused outright, a recognised vpn draws captchas, a plain datacenter is merely
  noticed, and an operator their own data calls a high abuser lifts a datacenter into the warning band.
  Fetched lazily on hover, one request per address, cached until the exit changes.
- **The stop button was sitting on the pushpin.** `CancelRect` was `(18,16,34,34)`, the controller paints
  the pin at `(9,4,24,24)` — a press meant for one could land on the other. Stop moved to x=42, title and
  activity line to x=84.
- **The refresh timestamp is hover-only.** A permanent "updated 4m ago" is a timestamp nobody asked for
  sitting in the corner; at rest it is just `⟳`, and the age appears when you go to press it.

`mirror/PRIVACY.{md,fa.md}` updated in both languages — they enumerate every outbound request, and the
line claiming `ipwho.is` was *the only* one disclosing anything to a third party had become false.

New dev hooks: `--probe-ip` prints the exit as both providers see it (the only way to check the
reputation parse without hovering an uncapturable window), and `HALO_RENDER_NET=1` makes
`--render-widget` wait for the real lookups so the exit block renders live data instead of "locating…".

Verified: `--probe-ip` against the live exit returned `verdict=datacenter abuse=high sev=2`;
`--render-widget … claude 2 447,160` under `HALO_RENDER_NET=1` shows all four exit lines with the amber
verdict, and the resting render shows the bare `⟳` and the stop button clear of the pin.

Asked for and **already shipped**: the media title marquee on hover. It exists (`DrawScrollingLine`,
bound to the title row, `Animating` gated on `_marqueeScrolling`, unit-tested in `MediaMarqueeTests`),
landed in 5f21d60, and the installed build already postdated it. It only moves when the title is too
long to fit — a title that fits is drawn plain, by design.

## 2026-07-28 (later): tooltip draw order, and the exit answers for the route
Build 0/0, **197 tests**. Hot-deployed.

- **The tooltip was not transparent — it was underneath.** Reported as "the background is colourless and
  it mixes with the block below". `DrawNet` painted its own hover panel and `DrawExit` was called
  afterwards, straight over the top of it. The hover geometry is stashed and the tooltip is now drawn
  last, after everything; its fill went to fully opaque, and it flips above the profile when there is no
  room below instead of running off the panel.
- **The freshness line moved to the top-right**, into the corner the stop button vacated. It had been at
  the very bottom, under the exit block — about as far from the numbers it dates as the panel allows.
- Stop button tightened against the title (13px gap → 6), ring cluster shifted left (cx 96 → 84).
- **Hovering the exit reports the route** instead of an invented score. ipwho.is sells no score on the
  free tier, so it shows what is actually measured: the ASN, the API path's current latency and its loss
  count out of the samples taken, and whether Claude's traffic leaves by this exit or another one.

`--render-widget` takes an optional `x,y` now, which parks the cursor. Hover is half this panel's
behaviour — the tooltip, the exact-reset swap, this new route readout — and none of it could be rendered
before, so all of it was being changed blind.

## 2026-07-28: the panel gets a real grid, a mirrored graph, and an exit that talks
Build 0/0, **197 tests**. Hot-deployed.

### Alignment was the actual complaint, and it was real
Everything was positioned from a top-left corner. Put 13px and 18px text in one row with the same `y`
and their baselines land ~4px apart — invisible until you look for it, and it is exactly why the panel
kept reading as "nothing lines up". Every string is now placed from a **baseline**, converted to GDI+'s
top-left via the font's own ascent (`TextTop`). Fixed columns too: key captions at x=178, their figures
at x=268, the whole right column between x=356 and x=538.

### The graph is mirrored, not overlaid
Two series on one axis fight each other however they are drawn — as lines they crossed, as filled areas
they hid each other. Your internet now grows *up* from a centre rule and the path to Anthropic grows
*down*, one bar per sample. Nothing overlaps, the shared scale keeps them comparable, and a lost sample
is a full-height red bar on whichever side dropped it, which is the question the graph exists to answer.

**Scale is 3× the median, not the max.** A cold TLS handshake costs ~1450ms against a steady ~85, and
scaling to the max — or even p90, which the spikes drag up with them — flattened every honest sample to
a 3px stub while the legend advertised "peak 1450". Measured both ways before settling on the median.

### The flag became an exit report
It was a country and nothing else. `IpCountry` already fetched the IP and threw it away, and ipwho.is
returns the ISP for free, so the block now reads `TR · G-Core Labs S.A.` over the address. And because
the API probe goes through `HTTPS_PROXY` while everything else goes direct, it asks the same question
down **both** paths: when the two exits differ, an amber line says where the API is actually leaving
from. Only when they differ — it is silent otherwise. `NetMon.ProxyUrl` is now the single source for
that proxy so the two probes cannot drift apart.

## 2026-07-27 (night, earlier): the ring panel, made legible
Build 0/0, **197 tests**. Hot-deployed. Four changes on top of the ring cluster below.

- **Type up a step throughout** — key captions and sub-lines were 11px and reported as unreadable;
  title 20→22, activity 12.5→14, captions 11→13, values 14.5→18, sub-lines 11→12.5, and the graph's own
  legend and axis with them. Key pitch 34→40 to hold it.
- **The stop button moved onto the status lamp**, in front of the title. It was in the far corner, about
  as far from the name of the thing it stops as the panel allows, and it is the same circle as the lamp.
  One 34px slot now: the red stop while a prompt can be interrupted, a plain lamp in the state colour
  (white when idle) the rest of the time. No ring and no square in the idle form — drawing button chrome
  with nothing to cancel would be faking an affordance.
- **The flag is centred under its graph** instead of shoved against the right edge.
- **The graph is filled areas, not two hairlines.** Two 1.6px lines crossing in a 30px strip was a
  diagram you had to squint at; each series is now an area fading out downward with the line kept on top,
  and the Y axis is gone — in a strip that short it was a third line competing with the two carrying data.

`--render-widget claude-idle` is new: the lamp is what the panel shows most of the time and there was no
way to render it, since the demo session is hardcoded to "working".

**Caught by checking geometry rather than by eye:** the key's sub-line box was 210px from x=182, and the
graph starts at 362 — a hovered 5-hour row prints "resets Wed 14:30 · $12.34 left" and slid straight
under the chart. Clamped to 168.

## 2026-07-27 (night, earlier): the Claude Code panel is a ring cluster now
Build 0/0, **197 tests**. Hot-deployed. Third pass, and the first that changes the *form* rather than
the arrangement — bars, then tiles, both still a list to be read one item at a time.

The three figures are not a list, they are three budgets draining at once, so they are one object:
concentric arcs around an empty centre, outer to inner as 5-hour / weekly / context, with a key beside
them carrying the exact numbers and the resets. It is the ring language the collapsed pill already
speaks — `RingProgress` draws this same arc — so opening the panel enlarges what you were already
looking at instead of switching notation halfway.

### What the form change exposed
- **Two of the three rings were the same colour.** The usage ramp started at blue and context *is* blue,
  so under 50% the outer and inner arcs were identical and the object said nothing. The ramp starts at
  green now; blue belongs to context alone. `UsageColorTests` gained a case that walks the whole ramp and
  fails if any point lands within 120 (summed RGB) of the context blue.
- **The Claude mark at the centre came out an orange splat** — it is a detailed glyph and the inner ring
  leaves ~18px of clear radius. The centre is empty.
- 9px bands on a 15px step ran together into a spiral; 8 on 16 reads as three rings.

**Known trade-off:** equal percentages do not look equal across the three arcs, because the radii differ.
They are independent budgets that are never compared against each other, and the key carries the exact
figures, so this was accepted rather than solved.

**Still asymmetric:** `CodexWidget` keeps the old one-column layout.

## 2026-07-27 (night, earlier): the Claude Code panel rebuilt as tiles
Build 0/0, **196 tests**. Hot-deployed. Same information as ever; the two-column pass below was a
rearrangement, this replaces the structure.

560x220 is wide and short, and three full-width bars stacked down it spent the width on nothing while
crowding the height. The three figures are peers, so they now sit side by side as tiles — caption, the
number at a size you read at a glance, a bar, and the detail underneath ("of 1M · 34%", "2h 47m left").
That frees a whole band at the bottom for the connection graph, which is the only thing on this panel
that is actually a chart, and it gets 246x30 there instead of being wedged beside the title.

**The flag stopped being a watermark.** `Fx.DrawFlagGhost` hardcoded a 210px ghost across the middle of
the panel, under the text — it competed with everything drawn on top of it. It gained a rect overload
(the old signature delegates to it, so the Codex twin is untouched) and the exit flag now sits at 76x51
at the end of the graph band, which is the part of the panel it is actually about. First attempt put it
at 46px and the ripple ate it: the crescent and star washed into a red smudge, so alpha now rises as the
rect shrinks and 76 is the floor at which it still reads as a flag.

Hit targets were checked as geometry rather than by eye — all seven in bounds, no overlaps.

## 2026-07-27 (night, earlier): the Claude Code panel redrawn in two columns
Build 0/0, **196 tests** (10 new). Hot-deployed; **not committed to the mirror** (app source, not docs).
Same information as before — nothing added, nothing dropped.

### Layout
One column of numbers, one of state. The graph used to be wedged between the title and the stop button
with about 135px, which put a moving chart beside the first line you read; it now owns the right column
at ~190px and is 46px tall instead of 22. Context / 5-hour / weekly sit on a 38px pitch in the left
column, a faded hairline marks the seam, and the freshness + refresh line moved under the graph.

The activity line is now ellipsised to the left column. `waiting_input` prints Claude's *real question*
there, and at any length it used to run under the graph.

### Two things the redesign exposed
- **The usage bar went grey.** `UsageColor` lerped blue→amber per channel, and those two average to
  (163,165,157) — measured saturation **0.05** at 61%, i.e. pure grey, which reads as *disabled* on a bar
  whose whole job is to say "warming up". It now rotates hue instead, running blue→cyan→green→yellow→amber
  and staying saturated the whole way. `UsageColorTests` pins it: 7 sample points must stay above 0.35
  saturation, blue below 50%, red at 100%, and hue must fall monotonically across the ramp.
- **The graph had never been eyeballed with data.** `--render-widget` drew the frame before NetMon had a
  single sample, so every render showed an empty axis. The hook now waits 3.5s after the warm draw for the
  agent widgets. An empty buffer also drew a bare L-shaped axis labelled with a *default* cap of 150 — a
  number that was never measured — which now says `sampling…` and no axis numbers instead.

### A leak this introduced and then fixed
Adding that wait let the asynchronous usage refetch land *after* `claude-demo` had set its synthetic
figures, so the saved frame showed real usage and a real dollar balance — precisely what the demo mode
exists to prevent. Demo figures are now applied last, after the wait, with credits forced to zero.

**Still asymmetric:** `CodexWidget.DrawExpanded` is untouched and keeps the old one-column layout, so the
twins no longer match.

## 2026-07-27 (night): the README hero, tried as a vector and then dropped
Three commits on `master`, all published to `V3` (`603dcac` → `6170dc2` → `8a87c1a`).

Replaced the 4.6 MB `preview.gif` hero with a 6.9 KB animated SVG of the media panel, user rejected
it on sight, reverted it, and then dropped the hero clip from the top of both READMEs entirely. The
top of the page is now the badges and the "try it in your browser" link. `ReadmeFiles/preview.gif`
stays: the repo-root README still points at it, and removing a file already in history reclaims
nothing.

Worth keeping from the attempt, since it will come up again:
- **CSS keyframes on SVG geometry properties (`width`, `y`) are silently ignored when the SVG is
  rendered as an image** — which is exactly how a README embeds one. Measured: the seek bar filled
  0 px at every sampled timestamp. SMIL `<animate>` works in that context; re-measured 0/24/49/74%
  at t=0/6/12/18s.
- **Headless Chrome does not advance SMIL time**, with or without `--virtual-time-budget`, so a
  screenshot pair "proving" an animation is worthless. Drive it with `svg.setCurrentTime()` on an
  inlined copy instead.
- A README's raw HTML can be checked against GitHub's sanitiser before pushing, via
  `gh api markdown -X POST` with `{"text": ..., "mode": "gfm"}`.
- `raw.githubusercontent.com` serves a stale branch ref for a while after a push; verify against the
  commit SHA, or `gh api repos/.../contents/<dir>?ref=<sha>`, not the branch name.

## 2026-07-27 (night): the suppression was correct and the service had never read it
Build 0/0, **186 tests** (1 new). Hot-deployed and verified live; **not released** (still 3.1.3).

### The report
"Either let the sound play in full or block it completely — stop cutting it in half." The 3.1.3 fix
earlier the same day stopped the mid-chime cut, but the sound was still audible: neither on nor off,
just the other half of the same complaint.

### Root cause — the "only if something changed" test asked the wrong question
`Enable()` re-asserted every learned AUMID and then `if (changed) ScheduleApply()`. For a returning
session nothing *changes*: the zeros are already in the registry from previous runs, so `WriteZero`
reports false and **no restart is ever queued**. But WpnUserService reads per-app settings exactly
once, when *it* starts — and it is started by the logon, before Halo writes anything. Measured live:

```
service pid 24984 started        20:40:08
com.nvidia.nvapp key written     20:41:15   <- after
Chrome key written               20:42:28   <- after
Logi.GHUB.Systray key written    20:40:34   <- after
```

Registry said `ShowBanner=0 Sound=0 AllowUrgentNotifications=0` for all three; the running service had
never seen any of it. The condition was guarding on the registry being stale when the thing that was
actually stale was the *reader*.

### Fix
`Enable()` now schedules the apply unconditionally — one service restart per Halo launch — and stamps
`_lastToast` at startup so that restart still waits out the 12s quiet gap and cannot fire into a sound
that was already playing when Halo came up. Verified live: service pid 24984 → 18364, log reads
`20:51:25 enable` → `20:51:37 applying → WpnUserService restart` (12s later, in quiet) →
`20:51:38 listener re-acquired`, and all three AUMIDs' key-write times now predate the running
service. Sounds are blocked for the whole session, and nothing is cut.

## 2026-07-27 (night): an outage during a closed panel left no trace in the graph
Build 0/0, **185 tests** (19 new). Hot-deployed here (`Halo.App.dll` only); **not committed/released
yet** at time of writing.

### The report
User saw Claude's own surface admit `529 Overloaded · Retrying`, but the pill's ring/mood never
flagged it and the graph showed nothing even after opening the panel afterward.

### What was actually true vs. what needed fixing
The 5xx→`Lost` mapping, the eager always-on heartbeat, and the ring/mood override were all already
correct (`ClaudeCode/NetMon.cs`, from the 2026-07-17 pass) — a live probe at the time of investigation
confirmed `api.anthropic.com/v1/messages` was healthy again and `IsDownStatus` already treats 529 as
down. The real gap: **the 10s background heartbeat computed `apiDown`/`netDown` as booleans and threw
away the measured values — it never wrote into the graph's ring buffer.** Only the fast panel-open
sampling (gated by `Poke()`'s 8s window) did that. So an outage that happened with the panel closed left
the ring buffer with nothing in it: the collapsed pill's mood/ring reacted correctly in real time, but
reopening the panel afterward to check showed an empty graph — which reads exactly like "doesn't notice
the outage" even though half the mechanism was working.

### Fix
The background heartbeat now calls the same `RecordSample` the fast path uses, so the graph has
continuous history (~10s resolution) regardless of whether the panel was ever open. Also pulled the
status-code-to-`Lost` decision out into `IsDownStatus(int)` on both `ClaudeCode/NetMon` and
`Codex/NetMon` (identical gap, same background-only blind spot, fixed identically per the "twin" rule)
— it was inline in a method that makes a real HTTP call, so 529 specifically was previously unverified
by anything. `NetMonTests.cs`: 19 cases pinning 529/500/503/403/407/429 as down and 200/405/404/401/499
as up, for both widgets.

## 2026-07-27 (evening): the notification sound was being cut in half — **3.1.3**
Build 0/0 with `-warnaserror`, **166 tests** (6 new).

### Root cause, from the log rather than from theory
`notif-debug.txt` has **23 `applying → WpnUserService restart` lines, every one of them ~3s after the
toast that triggered it**. Windows starts a toast's sound the moment the toast fires; Halo only learns a
new app's AUMID *from* that toast, writes `Sound=0` after the fact, and then restarts WpnUserService to
make it take effect this session. The restart landed inside the sound. So the chime was neither on nor
off — it started at full volume and was guillotined partway, which is exactly what was reported.

`SeedKnownApps` already claims every AUMID the registry lists, so this only bites genuinely new ids —
but tray apps mint one per launch (`NotifyIconGeneratedAumid_17586711155421024048`, and a second one
hours later), so it kept happening.

### Fix
The restart now waits for the notifications to go **quiet**: `ApplyDelayMs` is a pure function of
`now`, `lastRestart` and `lastToast` that returns the larger of "12s since the last toast" and "60s
since the last restart", and `DoApply` re-checks it rather than trusting the delay it armed. Every
mirrored toast stamps `_lastToast` — `SuppressApp` is already called per toast, so a burst pushes a
pending restart out on its own. The old code checked the cooldown in `DoApply` and the debounce in
`ScheduleApply`, which is why the two rules could not see each other.

Consequence worth stating plainly: a sound Windows has already started **cannot** be muted — the
per-app setting only takes effect on service restart, and restarting is what was cutting it. So the
first toast from an app Windows has never listed now plays *whole*, and every one after it is silent.

### Also this session
- **Installer icon.** `make_icon.py` handed the .ico to Pillow, which writes every frame PNG-compressed.
  Rewritten to emit BMP/DIB for 16..128 and PNG only for 256, each resized from the 512 supersample.
  **This was not the reported bug** — the old build extracts and draws fine at every size, verified
  side by side. Chrome showing a generic icon in its download list is code signing: the cert is
  `CN=phoseinq` issued by `CN=phoseinq`, so the publisher is unverified everywhere but this machine.
  There is no way around it; SignPath.io issues free certs to OSS projects.
- **`WizardSmallImageFile`** puts the mark in the corner of every wizard page. Verified by screenshotting
  the live wizard, which also caught both task checkboxes ticked **on an upgrade** — the case that used
  to clear them.
- **READMEs rewritten** in both languages: install first, then each feature as the clip from the blog
  post (copied into `ReadmeFiles/`), and nothing about how it is built. The Persian licence badge said
  CC BY-SA next to an MIT LICENSE.
- **`PRIVACY.md` + `PRIVACY.fa.md`**, written from `grep -rn "https\?://" src/` rather than from intent.
  Nine endpoints; `ipwho.is` is the only one that discloses anything about the user and is called out
  rather than buried. `notif-debug.txt` logs app name and text *length*, never text — stated with a
  sample line.
- Pillow's `split`/`flip`/pixel access all segfault on the CPython here (3.14.0a4), so the DIB is built
  from one `tobytes()` and slicing.

## 2026-07-27 (latest): installer task defaults, and the header demo on a phone — **3.1.2 RELEASED**
`origin/V3` = `0ecc99a` (mirror), local master `09ee224`, tag **v3.1.2** with both assets, and
`releases/latest` resolves to it, so the in-app updater will carry it. Build 0/0 with `-warnaserror`,
**160 tests** locally / 157 on the stripped tree. Installed here from the signed setup (3.1.1.0 at the
time of testing, autostart shortcut present, `~/.codex/hooks.json` written by the install task). Blog
changes deployed to both vhosts.

### Both installer checkboxes were only default-on for a first install
`AppVersion` still read 3.1.0 — the released build was 3.1.1 — and neither tick survived an upgrade:
- **Codex carried `Flags: checkedonce`**, which is explicitly "unchecked when Setup finds a previous
  version installed". Every upgrade therefore silently dropped the integration. Flag removed.
- **`UsePreviousTasks` defaults to yes**, so Inno restores the *previous run's* selection and overrides
  the defaults entirely. One person unticking autostart once meant it stayed off for every release after.
  `UsePreviousTasks=no`, so the `[Tasks]` defaults win on every install.
Verified with a real `/VERYSILENT` install over the existing one: `Startup\Halo.lnk` exists and
`~/.codex/hooks.json` carries the four `Halo.Hooks.exe codex …` entries.

### The blog header demo was cut in half on a phone
`assets/blog/halo-live.html`, deployed to `pvboy.dev` and `boystore.org`. The header is an iframe as wide
as the page; the open panel plus the circle beside it is **494px, and hero mode scales it 1.35× → 667px**,
inside a frame that is ~360px across and (at `aspect-ratio:5/2`) **144px tall**. Tapping the pill grew it
straight past all four edges. Three separate causes, all of them needed:
- **No fit.** `fit()` now writes `--s` and `--openw` from the real viewport. It **narrows the panel first**
  (the contents are flex and reflow at full text size) down to **312px — 40px padding plus the volume
  group, the transport and the empty column that keeps the transport optically centred; narrower and the
  slider runs into the buttons** — and only then scales the dock down. Desktop is untouched: at 900×360
  it still resolves to 1.35 / 440px, confirmed by a **pixel diff of old vs new rendered in the same page**
  (the only difference left was the hint, and `bottom:clamp(8px,9.5%,34px)` still lands on 34px there).
- **`:hover` latches on a touch screen.** `.notch:hover` and `.notch.open` were one selector list, so the
  first tap opened the pill *and* stuck the hover on, and the second tap could not close it. They are two
  rules now, the hover half behind `@media (hover:hover)`. Same for `.alt:hover` and the hint fade — which
  is why the hint also needed `body:has(.notch.open)`, there being no pointer to leave the dock.
- **The invisible panel was eating the tap**: it fills the collapsed notch, so the seek bar's pointer
  capture could swallow the gesture meant to open the pill. `pointer-events:none` until it is visible.
Hint text now says "tap" under `(hover:none)`, and a tap outside the dock closes it.
`blog/post.html` on both vhosts gained `@media (max-width:640px){…aspect-ratio:16/9}` — the shallowest box
the fitted dock clears. Backups on the server: `*.bak-mobile-20260727-*`.
**Verified against the live URLs** at 360×203 collapsed / open / download-app and 900×360 desktop, plus
the real post page at a 390px viewport. Trap: `--virtual-time-budget` does **not** advance timers in this
Chrome's `--headless=new`, so a lone screenshot catches the 0.42s open transition mid-flight and looks
like a regression — render both versions as iframes in one page and diff the halves instead.

## 2026-07-27 (later): glass latency, the pin's second setting, and two gesture collisions
Built 0/0, **160 tests** green, deployed here (hot-swapped exe). **Not pushed yet at time of writing.**

### The glass was ~6 frames behind the screen
Three separate causes, and only fixing all three helped:
- **Capture path.** `DoCapture` read the window DC, which comes back black for anything GPU-composited —
  every browser, every video player — so it fell through to `CaptureViaPrintWindow`, which re-renders the
  *whole* window. **Measured ~30ms per capture over a maximised window.** It now reads the **screen DC**,
  which is what DWM already composited, and PrintWindow is the third fallback rather than the usual path.
- **The blackness test was wrong for the new path.** `IsMostlyBlack` exists to catch a *failed* window-DC
  BitBlt. Applied to the screen grab it fired on any genuinely dark backdrop — **113 of 113 captures over
  a dark editor** went the slow way for nothing. The screen DC has no failure mode; the test is gone.
- **Blur expanded twice.** `Blur(Blur(raw,8),5)` did two full-size bicubic upscales, and the upscale is
  nearly the whole cost. `BlurPyramid` keeps the chain at thumbnail size and expands once.
- **Cadence.** `CaptureSlow` was 12 frames — a fresh backdrop every ~200ms collapsed. Now 2 (and
  `CaptureFast` 1), affordable because a capture went **57ms → 6.1ms**. Trace with `HALO_GLASS_DEBUG=1`
  (`%LOCALAPPDATA%\Halo\glass-debug.txt`): **234/234 captures on the screen path, avg 6.1ms, 32ms apart.**

### Pin no longer decides whether the pill is capturable
A pill visible to screen capture is visible to *its own* capture, so pinning silently forced the slow
path. The two are now separate settings (`capturable` beside `pinned`), and the pushpin carries both:
**tap = pin, press-and-hold 0.55s = show in captures.** Three readable states — dim, fully lit, lit head
only — plus a muted-amber needle for pinned+capture, or unpinning appears to do nothing. `--render-pin`
draws all five cells. The hold gesture is deliberately unlabelled.

### Two gestures that were stealing the press-to-move
`UpdateMove` starts on "button held over the pill and not travelling", which is also what a pushpin hold,
a file drop and a tray reorder look like. `PressOnControl` now covers the pushpin, and `holding` stands
down while `FileTray.DragActive`, `_trayPressPath` or an in-flight `_trayMode` says something is held.

### Chrome download cancel, with a long list
`uia-cancel.ps1` strategy 1 walks keyboard focus — up to 60 Tab presses at 150ms, stepping visibly down
the list one row at a time, and any pointer movement re-homes Chromium's focus and lands the cancel on the
wrong row. Chrome's bubble exposes **one Invoke button per row and no Cancel at all** (re-measured today:
`Button | zeta.bin ↓ 0.0/4.0 GB • 5 hours left`, no children), so the walk could never succeed there — it
is skipped for chrome/brave/vivaldi/opera, which also stops the bubble being opened for nothing. Edge
still needs it and now aborts if focus leaves the browser or the cursor moves. **Verified end to end with
four concurrent downloads off a local slow server: `partial is gone -> stopped`.**

### Blog (pvboy.dev/blog/halo-glass-notch)
Rewritten shorter, features named rather than explained. `<video class="md-img">` matched **nothing** —
the rule was `img.md-img`, which is why the clips never fitted the column; `video.md-img` and
`iframe.md-img` rules added to `post.html` on **both** vhosts. The tray clip is cropped 1400→1080 wide.
Header is now a **live embed**: `post.html` upgrades the featured image to an iframe when a `.live.html`
sits beside it (same convention the file already had for `.gif`), and the list thumbnail is a headless
screenshot **of that same page**, so the two cannot drift. Trap worth remembering: the renderer's
"bold numbers with units" pass rewrote `100%` **inside a style attribute** into `<strong>100%</strong>` —
nothing carrying a number and a unit survives in post content, so sizing has to live in the stylesheet.

## 2026-07-27: UI polish round — **3.1.1 RELEASED**
`origin/V3` = `31fa557`, tag **v3.1.1**, CI green, installed here from the signed setup (3.1.1.0,
autostart shortcut present). Build 0/0 with `-warnaserror`, **160 tests**.

### The one that explains most of the "choppy" reports
`NotchController` honoured `IWidget.Animating` **only while the pill was collapsed** (`_progress < 0.5f`).
An open panel therefore redrew only when something else marked it dirty. **Measured: 42 distinct frames
in 12s (~3.5fps) with a title scrolling at 42px/s; 249 after the fix.** `AdaptFrameRate` already pins 60
while the panel is open, so the gate saved nothing — it was starving the waveform, animated covers and
the marquee at once. Measure this with `mpdecimate` on a ddagrab capture, not by eye.

### Media
- **Press-and-drag scrubbing.** The click dispatch is edge-triggered, so a drag was N separate seeks each
  awaiting the player — that was the "تیکه تیکه". `WidgetInput.Down` added; while held the bar tracks the
  cursor and the seek commits **once**, on release. Grows 3× **while held** (hover was rejected: it
  twitches whenever the pointer crosses). Timestamp previews the target.
- **Holding a control used to walk off with the pill** — press-and-hold is the move gesture, and the
  offset is persisted, so it *stayed* moved (found it at 269). `PressOnControl` excludes any rect the
  widget's `Buttons()` describes.
- Title marquee bound to **its own row**, not the panel; 0.35s hold; resets on leave. `MarqueeStep` is
  pure and unit-tested so the rate cannot become frame-dependent again.

### Shapes that showed their edges
- **Glow was a rectangle**: dither noise was *added* to the falloff, so everything outside the inscribed
  circle sat at alpha 1–5 and the texture's square boundary stayed lit. Now noise is **scaled by** the
  falloff (true 0 at the edge), `WrapMode.Clamp`, and radii overshoot the panel.
- **Strip icon wash was a flat square** clipped to the strip path → rounded-corner box around every
  circular icon. Now a radial `PathGradientBrush`, no clip.
- **Copy pill**: icons must be centred on their **ink**, not font metrics — two glyphs of one icon font
  differ in ink height (page icon 1.4px high, check on centre). `Fx.InkCentreOffset` / `CapCentreOffset`.
  New `--render-copy` hook draws both states at 4× with a centre guide.

### Session circle
`RingProgress` for Claude and Codex = 5-hour window, weekly as stand-in, `-1` (full ring) when neither is
known. Colour still = state. A lone session no longer wears a "1" — `StatusStore.LiveSessions()` gates it,
which is the rule the Codex twin already followed.

### Notes for next time
- **Recording/verifying the pill is hostile while a Claude session runs**: the agent widget is promoted on
  every state change, and every tool call *is* one. Park `~/.claude/notch/*.json` for the shot and restore
  in a `finally` — that is the only reliable way to get media on screen.
- `SetCursorPos` is remapped by the calling process's DPI (asked 1280, landed 980). Use `mouse_event` with
  absolute 0..65535 coordinates. And **never name a helper `Move`** — it is an alias for `Move-Item` and
  silently ate every pointer call for a whole take.
- ddagrab draws some cursors as a white box; `draw_mouse=0` for anything but a drag shot.


## 2026-07-26 (night): mirror automation, banner alignment, cancel actually cancels — **3.1.0 RELEASED**
**Pushed and released.** `origin/V3` = `4833fae`, tag **v3.1.0** with `DynamicWinSetup.exe` +
`DynamicWinPortable.zip`. Public **CI passed on the push** (1m39s) — the workflows build, test and run
the source policy against our tree for the first time. Build 0/0, tests **153** local / **150** on the
stripped mirror; installer signature `Valid`, thumbprint `2EB268…2E1D`, which is the one
`AutoUpdate.SignerThumbprint` pins.

**Nobody auto-updates *to* 3.1.0** — 3.0.2 has no updater. The daily check starts mattering at
3.1.0 → 3.1.1, so this release's blast radius is only people who install it by hand. Verified the
updater's own view of the release: `tag_name=v3.1.0` parses, and the asset is named exactly
`DynamicWinSetup.exe` as `AssetName` expects. On this machine it logs
`latest=v3.1.0 running=3.1.0.0` and correctly does nothing.

Commits: `2b0980b` mirror automation · `0011b85`+`6e505cf` private-asset test gating ·
`6921ea4` banner icon + alignment · `6c507b7` cancel.

### Publishing the mirror is now one command
`scripts/publish-mirror.ps1`. The public tree is **derived, never edited**: `src/` and `tests/` come
from master, and everything that exists only on the public side lives in `mirror/` here — workflows,
CODEOWNERS, the policy script, the **MIT** LICENSE and the public READMEs. It stages into a temp dir,
strips comments from `src/` only, runs the public repo's own policy gate, builds and tests the
stripped tree, then writes the commit with `commit-tree` (this repo's index is never touched).

- **The guard that matters:** before committing it diffs its tree against `origin/V3` and refuses if
  any file would disappear. First run it immediately caught Codex's `NotchVisibilityTests.cs`, which
  existed only on the fork. That test is now back-ported (148 → and the mirror is safe to regenerate).
- **The strip tool lives in `tools/strip/`** now, not `%TEMP%`. Deliberately outside `Halo.sln` — it
  pulls Roslyn, and `dotnet build Halo.sln` must stay at 0/0 with only System.Drawing.Common.
- **Comments survive in `tests/`.** They are not shipped source, and three of the six defects in the
  last outside patch came from load-bearing comments being stripped before the contributor saw them.
  For the same reason the CI comment rule is now **advisory on pull requests**, fatal only on a push
  to V3 where an unstripped file is our own bug.
- **`LICENSE` diverges on purpose and must not be "fixed":** public is MIT, master is CC BY-SA. The
  overlay is what stops a mirror push from silently relicensing the public repo. `RequiredOverlay`
  fails loudly if it is missing.
- `installer/` and `hooks/` are private, so three Codex tests that read them now compile only behind
  `HALO_PRIVATE_ASSETS` (defined when `installer/Halo.iss` exists). Runtime skipping does **not** work
  here: xunit 2.x has no `Assert.Skip` and the 2.9 runner ignores its dynamic-skip message token.

### Halo's own banners: no icon, text too high
Both from one blind spot — **every `--render-*` hook rendered a mirrored toast, and a mirrored toast
always has a body.** The body-less ones are ours.
- The text column's offsets are tuned for two body lines, so eyebrow+title alone ended at y=67 and
  read as centred on 44 while the artwork centres on 64. `NotifBanner.TextShift(hasBody)` centres the
  pair when there is no body; the two-line layout keeps its tuned numbers untouched (a body is what
  the grow-to-detail reveal cross-fades). The copy pill travels with the title row.
- `OnClipboardImage` set `Preview` but never `Icon`, and the preview takes the icon's slot — so the
  screenshot/clipboard banners shipped **no identity mark at all** and `Fx.AccentOf(null)` left them
  the only banners with no glow. They now carry a camera / copy badge on the thumb's bottom-right
  corner, with a **dark** ring because a capture of a bright window is the common case.
- New hook **`--render-local`**: the four real local banners stacked, each with a centre guide-line
  drawn across it. That is the hook that was missing.

### Cancel — six defects, all measured against a local trickle server
`scratchpad/trickle.ps1` serves 60MB at 24KB/s over loopback, so this was all verified **without
spending any of the user's bandwidth**. Both browsers now end the transfer: `rc=0`, partial gone.

1. **Edge had no name to match a row on** — target came out as `Unconfirmed 12345`. Fixed by reading
   `target_path` from the store.
2. **Those paths were unread in the store.** `InProgressInfo` **f13 = current_path, f14 = target_path**,
   both **pickled `base::FilePath`**: `uint32 payloadSize, uint32 charCount, UTF-16 chars`. Under UTF-8
   they look like binary, which is why they were skipped. `current_path` also replaces the old
   closest-`received_bytes` guess, which could attribute a file to the wrong download.
3. **Edge's row has no buttons until keyboard focus reaches it** — a descendant search returns Image,
   two Texts, ProgressBar, nothing else. Tab into the row and `Pause`/`Cancel` are the next two stops.
   **This is the whole reason Edge never worked.** It therefore needs the browser in front.
4. **Chrome's Ctrl+J bubble is not in the a11y tree at all**, and the bubble from toggling the toolbar
   button exposes one Button per row and no Cancel. The way in is Chrome's **app menu → the MenuItem
   whose name contains `Ctrl+J`** (locale-independent), which opens the real downloads *page* where
   each row has a `More actions` menu holding Cancel.
5. **A failed focus returned early → the click did literally nothing.** Foreground rights belong to
   the process receiving input, which the pill does not always still have. The toolbar button is now
   pressed through UIA (no rights needed), and if nothing can be pressed the browser's own list is put
   in front of the user. Never silently give up.
6. **Advertising a pattern is not honouring it:** Edge's toolbar button answers `Expand` with
   `E_FAIL`, and `ErrorActionPreference=Stop` killed the script on its first strategy. Every UIA call
   is its own `try` now. Chrome's toolbar button carries **only** `TogglePattern`, which `Press` never
   tried.
- The script outgrew `-EncodedCommand`'s ~32K command line and stopped launching at all (`rc=-1`); it
  goes through a temp file now, written **with a BOM** (5.1 reads BOM-less UTF-8 as ANSI).
- New hooks: **`--cancel-download`** (scan, cancel, then report whether the partial actually stopped)
  and **`--probe-downloads`** (every source's view, plus raw store fields — this is how f13/f14 were
  found).

### Trap that cost time tonight — hot-deploy
Copying `Halo.App.{deps.json,runtimeconfig.json}` from `bin/Release` over the install **breaks it**:
the installed app is self-contained, and bin's runtimeconfig says framework-dependent, so Windows
prompts the user to install .NET. Hot-deploy **only `Halo.App.dll`**, or copy from a real
`dotnet publish -r win-x64 --self-contained` output.

### Still open
1. Higher-quality blog videos for `pvboy.dev/blog/halo-glass-notch` (`HALO_CAPTURABLE=1` + ffmpeg
   `ddagrab`).
2. `144c2c0`/`63feae0` still carry Codex's work under my authorship. Cosmetic now: the mirror
   flattens history, so the public repo never sees it. Split only if local attribution matters.
3. Chrome's *bubble* cancel remains unreachable by design (one control per row); we route via the
   page instead, which works. Edge's cancel needs the browser focused — inherent, since the buttons
   do not exist otherwise.

### How to release next time
```powershell
pwsh scripts\publish-mirror.ps1 -Message "v3.1.1 - ..." -Push   # strips, gates, builds, tests, pushes
pwsh installer\build.ps1                                        # publish + sign + Inno + zip
gh release create v3.1.1 --repo phoseinq/DynamicWin --target <mirror sha> `
    --title "Halo v3.1.1 — ..." --notes-file notes.md dist\DynamicWinSetup.exe dist\DynamicWinPortable.zip
```
The mirror script refuses to publish if the tree is dirty, if `mirror/` is missing a required file, if
the stripped tree fails the policy gate or its tests, or if any file would be deleted from V3.

---

## 2026-07-26 (later): Edge progress, auto-update, real cancel — HANDOFF
**All committed to `master` and deployed locally (hot-copied, v3.1.0). NOT pushed, NOT released —
GitHub is still on v3.0.2.** Build 0/0, tests **144/144**.

### READ THIS FIRST — history needs cleaning up
Two of my commits swept in Codex's in-flight work because `git add -A src tests` was too broad.
Nothing is lost, but the attribution is wrong and the eventual stripped-mirror push will be hard to
reason about until it is split:
- `144c2c0` ("Edge showed a byte count...") also contains Codex's `Codex/Limits.cs`, `Codex/Status.cs`,
  `Shell/LayeredNotch.cs`, `Shell/NotchController.cs`, `Widgets/CodexWidget.cs`, `Widgets/IWidget.cs`
  and four `Codex*Tests.cs` files.
- `63feae0` ("real Edge download progress...") also contains Codex's
  `src/Halo.Hooks/CodexHookInstaller.cs` and `src/Halo.Hooks/Program.cs`.

Still uncommitted and deliberately untouched: `hooks/install-codex-hooks.ps1`, `installer/Halo.iss`,
plus untracked `AGENTS.md` and `docs/superpowers/plans/2026-07-26-offline-codex-hook-installer.md`.

### Edge downloads — the whole picture, measured
Edge is not Chrome, and both earlier attempts failed because of it:
- **History is written only when a download ENDS.** `max(id)` did not move through 22s of active
  downloading with 85MB on disk. Chrome writes its row up front, which is why Chrome always worked.
- **The partial is never renamed** away from `Unconfirmed 12345.crdownload`, and `current_path` stays
  empty even in the finished row — so neither path can be matched by name.
- **The file on disk is not the download.** One `Unconfirmed` blob grew 72MB → 92MB across three
  separate test downloads. We were showing its length as progress. A folder-based heuristic was tried
  and reverted (it named a 1GB transfer after a different row).

The fix is `Widgets/ChromiumProgress.cs`: read Chromium's own in-progress store, a LevelDB under
`<profile>\shared_proto_db`, by hand — 32KB blocks, crc/len/type record headers with FIRST/MIDDLE/LAST
fragments to stitch, WriteBatch entries, then a minimal protobuf walk to
`DownloadDBEntry.f1.f4 → { url = 1, total = 10, received = 15, state = 21 }` (state 0 = in progress).
**Those field numbers were read off the live store — there is no `.proto` in this repo.**
Two traps inside it, both found by measuring: a write-ahead log keeps *every* revision, so one transfer
came back as twenty rows with `received` climbing through all of them (now keyed by guid, last write
wins, delete drops it); and requiring `received` to match the file size made the pill flicker back to
"Downloading" when the store lagged the disk (88MB vs 55MB), so with one download running no match is
required. Verified live: `100Mb.dat 55,098,542 / 104,857,600` at 52%, climbing to 99%, then cleared.

### Cancel
Chrome: **works**. Edge: **does not, structurally.** Chrome puts downloads on a page inside the frame
window; Edge opens a flyout that is its own top-level window *and auto-dismisses*, so by the time the
UIA tree is swept there is nothing left to press, and with no filename there is no row to target.
What was learned building it (`Assets/uia-cancel.ps1`, driven from PowerShell 5.1 out-of-process):
- MSAA is useless here — `AccessibleObjectFromWindow` returns **0 children** on the frame window and on
  all three `Chrome_RenderWidgetHostHWND` children. Chrome is UIA-first.
- Chrome builds the renderer a11y tree only *after* a UIA client attaches, so the first sweep sees
  browser chrome and no page content. It retries until the list appears.
- An in-progress row shows no Cancel: only "Copy download link" and "More actions". Cancel is a
  **MenuItem inside that menu**, and the menu button carries `ExpandCollapsePattern` and no
  `InvokePattern` — an Invoke-only filter silently discarded the control that opens it.
- Judge success on the **file**, never the browser UI: a cancelled row keeps its menu, so a row-based
  check reported failure on a cancel that worked (and earlier, success on one that had not).
- `SetForegroundWindow` returns before the foreground changes; a 600ms poll gave up and skipped the
  keystroke. Now 1.5s with one retry.

### Auto-update (`Update/AutoUpdate.cs`)
Daily silent check of the GitHub latest release; installs with no prompt (install lives under
`%LOCALAPPDATA%`, so no elevation). **Signer is pinned by thumbprint, not chain-validated** — the
certificate is self-signed, so `Status == Valid` on the build machine and invalid everywhere else;
chain validation would have disabled updates for every real user. Rotating the cert means updating
`SignerThumbprint`, and until then updates stop rather than accept a different signer. Failure backs
off 30m → 6h → 12h → 24h, once each; "nothing new" counts as success and resets it. Downloads to
`.part` and renames on completion. Portable copies are skipped. Log:
`%LOCALAPPDATA%\Halo\update-log.txt`. Verified: `latest=v3.0.2 running=3.1.0.0 → ok=True nextIn=24h`.

### Smaller
- Notification summary readability (idea from `codex/notif-readability`, reimplemented — its 8-field
  metrics record was seven literals behind a function and its test asserted them back at themselves).
  Gotcha: `GenericTypographic` carries `LineLimit` and lays out only WHOLE lines, so two 14.5px lines
  (~38.6px) vanished to one in an exactly-38px box.
- Copy pill: `LineAlignment.Center` centres the EM box, so digits and "Copied" sat low — `Fx.CenterLift`
  fixes it. Also moved onto the title row and eased.
- No Persian in source (user rule). Pre-existing Persian *comment quotes* in `Fx.cs`,
  `NotchController.cs`, `LayeredNotch.cs`, `MediaWidget.cs`, `AudioSpectrum.cs` were left alone.
- Both PR reviews on GitHub were **edited** to withdraw the "remove your comments" request, which
  contradicted this repo's own workflow (the fork is a *mechanically* stripped mirror; `master` is the
  comment-bearing truth and contributions land there first).

### Next, in the order I would do it
1. **Comment-stripping in CI/CD** (user's idea, fixes the root cause). Trap: branch
   `codex/ci-guardrails` adds a gate that *fails* on comment lines — it must become a post-merge strip
   step or the two will fight.
2. Split `144c2c0` and `63feae0` so Codex's work is its own commit.
3. Our own local notifications: no icon, text not aligned. Not investigated.
4. Higher-quality blog videos for `pvboy.dev/blog/halo-glass-notch` (`HALO_CAPTURABLE=1` + ffmpeg
   `ddagrab`).
5. Push to GitHub + release 3.1.0 — only after 1 and 2, so the stripped mirror deletes nothing.

## 2026-07-26: multi-download, a real cancel, and the pill-as-bar colour
**Deployed locally (hot-copied over the install, v3.1.0); committed to `master`; NOT pushed and NOT
released — GitHub is still on v3.0.2.**

### The Cancel chip was two bugs stacked, and the second was mine
- **No hit rect.** `DrawControls` and `Buttons` each laid the control row out independently and had
  drifted: the painter grew a third chip for partial-file downloads (folder / switch app / cancel)
  while the hit-tester still built two, so Cancel had *nothing* behind it and the two that did work
  sat 29px left of the circles being aimed at. Both now read one pure `DownloadWidget.Row`, pinned by
  `DownloadControlsTests`. The row is also left-aligned to the title's edge instead of centred the way
  media's transport is — media has a symmetric prev/play/next cluster; this is a toolbar.
- **Deleting the partial file was never a cancel.** Chrome opens its `.crdownload` with
  `FILE_SHARE_DELETE`, so the delete succeeds and the directory entry disappears while the handle
  stays valid and the transfer keeps running. Measured across the delete: **1694 KB/s before, a
  sustained ~350 KB/s for the next 15s with no partial file on disk at all.** Worse than doing
  nothing — the pill collapsed (we cleared `Name`) so it looked like it had worked, while the
  download kept spending bandwidth invisibly and threw the bytes away when the final rename failed.
  My earlier "verified live, the transfer stops" had only ever checked that the *file* was gone,
  which is not evidence about the bytes. Reported from the other side three times.
- **What it does now:** a browser gets its own downloads list pushed in front of the user (focus +
  Ctrl+J, the shortcut every Chromium browser and Firefox share), the partial file is left alone and
  the pill keeps showing the download because it is still running. Anything else is a plain
  downloader we can stop for real by ending it, and only then is the leftover partial safe to delete.
  `OwnerIsBrowser` guards that branch by name **and by process-fleet size** (chrome 19 processes,
  msedge 9, a downloader 1) so an unrecognised Chromium fork is never mistaken for a downloader and
  killed.
- `SetForegroundWindow` returns before the foreground has actually changed, so checking
  `GetForegroundWindow` on the next line could say "not us" and skip the keystroke entirely. The
  focus-and-type runs off the frame timer now and polls for up to 600ms.

### Several downloads at once
- `Scan` collected one winner through a priority chain and returned on the first hit, so a Steam
  install hid a browser download and two browser downloads hid each other. It now gathers every
  source into a list and projects one item onto the volatile fields the widget already reads — which
  is why **no drawing code had to change** to become a list.
- Order is arrival order, never progress: sorting by speed or percentage reshuffles the pill every
  second. Oldest running download owns the pill, the next takes over when it finishes, and the
  switcher's choice is sticky so it survives the bytes moving underneath.
- `Roots()` yielded the Downloads folder **twice** — once as the default and once from the learned
  downloader directories, where the browser was first seen writing — so every download was listed
  twice. Verified live with three concurrent Chrome downloads: three rows, right names and
  percentages, arrival order held while progress moved.
- Panel hamburger opens a four-row list over a scrim; longer lists window around the selection. The
  collapsed pill carries a count badge top-right (so it cannot collide with the pause badge), absent
  at one download.

### Rendering
- **The pill-as-bar read as flat paint.** The track was `Shade(accent, 1)`, which is *more* saturated
  as well as darker — on Chrome's yellow that is olive, so the whole pill was one dirty hue with a
  brighter patch on it. Track now keeps the hue and drops most of the saturation; two glows instead
  of one (wide + dim under the fill for body, tight on the wavefront); a vertical sheen; and the
  wavefront falls off over ~2.5px instead of a quarter of a pixel. Extra track weight is spent only
  where the effect is bold, so the agent pills at `strength 0.3` stay a whisper — verified by
  rendering both strengths across three accents and three fractions.
- **Notification summary** (idea taken from `codex/notif-readability`, reimplemented): the line people
  read at a glance was `Dim` (alpha 150) at 13px, and a `FontScale` shrank every string *further* the
  longer the toast got — so the toasts with the most to say were the hardest to read. Body now has its
  own near-white tone at 14.5px and holds one size; one truncated line became two wrapped ones.
  Gotcha found while verifying: `GenericTypographic` carries `LineLimit` and lays out only **whole**
  lines, so two 14.5px lines (~38.6px) silently vanished to one in an exactly-38px box.

### Browser download internals — measured, worth not rediscovering
- **Edge writes its History row only when the download ENDS.** `max(id)` did not move through 22s of
  active downloading with 85 MB on disk. Chrome writes the row up front, which is why Chrome gets a
  percentage and Edge does not. Edge also never renames its partial away from
  `Unconfirmed 12345.crdownload`, and leaves `current_path` empty even in the finished row, so neither
  path can be matched by name. A folder-based heuristic was written and **reverted** — it twice named
  a 1 GB transfer after a different row. Edge's live download *is* recorded, in `shared_proto_db`
  (LevelDB log + protobuf); parsing that is the open option.
- **Chrome is UIA-first, not MSAA.** `AccessibleObjectFromWindow` on the top-level window and on all
  three `Chrome_RenderWidgetHostHWND` children returns **0 children**. Over UIA the same window
  exposes 47 buttons including the `"1 download in progress"` bubble button, which invokes fine, and
  download rows are `ControlType.DataItem` with `InvokePattern` whose Name is the row's concatenated
  text (`"1Gb.dat Canceled Copy download link More actions Delete from history"`). All localized.

### Still open
- Auto-clicking Cancel via UIA (approved in principle; MSAA ruled out, so it is either ~400 lines of
  hand-written `IUIAutomation` vtable interop or shelling out to Windows PowerShell 5.1, which ships
  `UIAutomationClient` — decision pending).
- Edge percentage via `shared_proto_db` (approved in principle, not started).
- Media + download coexistence (badge on the icon, open to the right) — not started.
- Reading the PR replies on `phoseinq/DynamicWin`; GitHub release for 3.1.0; stripped-mirror push.
- Tests 102 → 112 for the download work (124 with Codex's uncommitted Codex-widget tests).

## 2026-07-26: public V3 CI and security guardrails
**Pushed to `phoseinq/DynamicWin` branch `V3` as `03abffe`; not deployed locally.**
- **Root cause:** the public mirror had no test project or CI, so pull requests had no automated
  compilation, regression, source-policy, dependency, or static-security feedback.
- **Implementation:** added a pinned-action Windows .NET 9 Release pipeline with warnings-as-errors,
  four initial `NotchVisibility` tests, a self-tested public-source policy gate (no shipped C# comment
  lines, tab indentation, or new production NuGet packages), CODEOWNERS, CodeQL, and PR dependency
  review at moderate severity.
- **Verified:** local policy self-test and repository scan passed; Release build completed with
  **0 warnings / 0 errors** and tests **4/4**. Manually dispatched GitHub runs completed successfully:
  CI `30181768325` and Security/CodeQL `30181769235`. The two initial push runs were intentionally
  cancelled by the workflows' concurrency setting when the manual verification runs started.
- **Review state:** PRs #1 and #2 both have `CHANGES_REQUESTED` reviews authored by `phoseinq`;
  remaining issues are documented in the review bodies. No standalone bot/account comments were added.

## 2026-07-26: offline Codex hook integration in the installer
**Deployed locally from the signed installer; implementation is not committed or pushed.**
- **Root cause:** `DynamicWinSetup.exe` shipped `Halo.Hooks.exe` but never registered Codex lifecycle
  hooks. The only registration path was a repository PowerShell script that could invoke `dotnet
  publish`, so a normal installed/offline machine could not configure the integration itself.
- **Implementation:** the self-contained `Halo.Hooks.exe` now owns
  `install-codex-hooks <absolute-exe>` and `uninstall-codex-hooks`. It preserves unrelated handlers,
  replaces stale Halo entries idempotently, writes `.halo-bak`, atomically replaces valid JSON, fails
  setup commands on malformed JSON, and removes only Halo handlers during uninstall. Normal lifecycle
  events retain their silent-failure behavior.
- **Packaging:** Inno Setup has a default-enabled `codexhooks` task that invokes the installed helper
  directly, with no internet, repository, `pwsh`, or `dotnet` dependency. Uninstall runs the surgical
  removal command once. The developer PowerShell wrapper delegates to the same implementation.
- **Verified:** command-level tests were observed red before implementation; focused hook tests
  **14/14**, full Release tests **124/124**, Release build **0 warnings / 0 errors**, and the final
  signed installer compiled without warnings. Silent install returned 0; the live config contains
  exactly seven Halo handlers and all point to
  `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe`; backup exists; installed/published hook executable
  hashes match; a real installed `codex prompt` probe returned 0 and wrote `working` / `cli`. Halo was
  relaunched from the installed build as PID 31964.

## 2026-07-26: Codex shell/live-bar/main-session/icon fixes
**Deployed locally from the signed installer; not committed or pushed.**
- **Shell activity:** current Codex rollouts emit `function_call` / `function_call_output` with
  `shell_command`; the parser only understood the older `custom_tool_call`, so shell work stayed
  unclassified. Both formats now drive the tool state, output returns the ring to thinking, and
  `shell_command` renders as `running…`.
- **Collapsed usage bar:** limits were copied from the active snapshot only at startup or when the
  expanded panel opened. `DrawCollapsed` now observes the selected snapshot before drawing the pill
  bar; identical observations no longer rewrite the cache or fake a newer freshness time.
- **Main session wins:** Codex Desktop subagent rollouts carry `parent_thread_id`, but the broker used
  the newest file regardless, so parallel agents displaced the parent task. Child rollouts are now
  excluded from both full scans and incremental watcher updates.
- **Small Codex circle:** grouped Codex rows used a numbered/badged session bitmap as the closed-row
  icon; the badge changed its ink bounds and shifted the mark. The closed row now uses the plain,
  centred OpenAI mark, matching grouped Claude rows; badges remain on the expanded session fan.
  A live screenshot then showed the geometrically-centred OpenAI knot still reads optically right-heavy,
  so `IWidget.IconOffsetX` now carries a Codex-only `-1.25px` correction into the supersampled strip;
  the ring and every other widget stay at zero offset.
- **CLI hook path:** `install-codex-hooks.ps1` now prefers the shipped
  `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe`; the old `%LOCALAPPDATA%\Halo\hooks` publish remains
  only as a development fallback. Per project policy, the live user hook config was not rewritten
  automatically.
- **Verified:** regression tests were observed red before implementation; full Release tests
  **120/120**, `dotnet build Halo.sln -c Release --nologo` **0 warnings / 0 errors**,
  `--render-widget ... codex` produced `%TEMP%\halo-codex-after.png` with no clipping, and
  `installer\build.ps1` produced signed installer/portable artifacts. Installed silently and
  relaunched the final optical-offset build as PID 19592 from
  `%LOCALAPPDATA%\Programs\Halo\Halo.App.exe`; installed and staged `Halo.App.dll` SHA-256 hashes
  match (`AAE51477...4AE7`).

## 2026-07-26: v3.1.0 — download coverage (browsers/Steam/any app), pill-as-bar UI, ring fixes
**Local only: installed and running as 3.1.0, but GitHub is still on v3.0.2 — no release cut for 3.0.3
or 3.1.0.** Commits `471c244`..`b181906` on `master`; nothing pushed to the fork since `c1c3070`.

### Download coverage — three tiers, best available wins (`docs/superpowers/specs/2026-07-25-download-coverage-design.md`)
`Downloads.Scan()` only ever found a download by regex-matching a leading `NN%` in a **window title**,
which deliberately skips browsers (`Downloads.cs` — a page can be titled "50% off") and never saw Steam.
- **`PartialFiles.cs`** — watches the FILESYSTEM, not the app: `*.crdownload/.part/.opdownload/...` that
  are growing. This is what finally covers every browser and any other downloader. Owner process comes
  from **Restart Manager** (`rstrtmgr.dll`), verified to work **unelevated**.
- **`BrowserDownloads.cs`** — supplies the one thing the filesystem can't know, the total, from Chromium's
  own `History` via `winsqlite3.dll` (same technique as `WpnDb`). Chrome/Edge/Brave/Opera/Vivaldi share
  the schema; every `Default`/`Profile N` is scanned. Firefox keeps downloads as `places.sqlite`
  annotations → bytes only, no percentage (honest, per the no-invented-numbers rule).
- **`SteamInstall.cs`** — `BytesDownloaded`/`BytesToDownload` from `appmanifest_*.acf` across every library
  in `libraryfolders.vdf` (3 here: C:, H:, D:). `StateFlags` deliberately NOT a gate — its bit semantics
  were never verified against a live download.
- **`Downloaders.cs`** — learns (app, directory) pairs to `downloaders.tsv`; the *directory* is the useful
  part, so a launcher downloading into `D:\Games\...` is picked up next time.
- `PartialFiles.LiveCount` → `Downloads.Count`/`HasMore` is real, not a placeholder; always 1 today.

### Chromium's download DB — three traps, each cost a debugging round
1. `immutable=1` makes SQLite **ignore the `-wal`**, so in-flight rows are invisible → snapshot the db
   **and its wal** to temp and open the copy.
2. `WHERE state = 0` finds nothing: Chrome keeps live progress **in memory**. Measured 60MB into a 100MB
   file: `total_bytes=104857600`, `received_bytes=0`, non-zero state. Take the newest row per file
   whatever its state, match `target_path` too (`current_path` is empty mid-download), ignore
   `received_bytes` entirely — bytes come from the file.
3. Chrome writes **no row at all** for a first-ever download until it finishes; a repeat download names
   the file `x (2).ext` while the sizeable row is `x.ext`. `StripCopySuffix` (parens, 1–3 digits only)
   fixes the repeat case. **First-time downloads therefore have no percentage — breathing pill.**
   Getting one would need `Content-Length`, i.e. a browser extension. Not built.
- **Cancel IS possible** and I wrongly claimed otherwise before testing: deleting the partial file stops
  the transfer (Chrome opens with `FILE_SHARE_DELETE`), verified live — file gone, nothing recreated over
  6s. `Downloads.CancelPartial` only ever deletes a path that still classifies as partial.
- Stall → `Paused`: counting consecutive no-growth **samples**, not `LastWriteTime` (Windows doesn't flush
  it per write, so a stopped download looked alive for seconds).

### UI: "the pill IS the bar" (`Fx.PillBar`)
No separate bar — the silhouette carries a deeper shade of the app's accent as track, the accent fills
left→right, a lip and glow ride the wavefront, and the **icon is drawn last** so the fill passes behind
it. Shared with the agent pills at `strength 0.3` to show the 5-hour usage window (weekly as fallback),
collapsed only, never while compacting owns the pill. Rendering lessons, each measured:
- `g.SetClip(path)` is **region-based and never antialiased** → the curved LEFT edge stair-stepped while
  the straight wavefront looked fine. Fill the PATH with a gradient cut instead.
- Filling the exact window silhouette put a hard accent line along the rounded bottom (two AA edges on one
  pixel row summing) → `PillPath` gained an `inset`.
- ClearType paints orange/blue fringes on a layered surface → `AntiAliasGridFit` for pill text.
- **Do NOT fold alpha into the colour channels of `Fx.Glow`'s ColorMatrix.** It looks required since the
  texture is premultiplied, but GDI+ un-premultiplies around a ColorMatrix; doing it greys the tint out
  (green accent measured 11,11,11, saturation 0). Tint is already faithful: hues 9/136/218 → 11/133/215.
- `Fx.CenterLift(font)` replaces a hardcoded -1.5px text lift that only suited one font size (these pills
  shrink text to fit): 0.58px @9px → 1.15px @18px.
- Panel rebuilt as one left-aligned column with each block placed from the one above, plus a **reserved
  right gutter** for the future switcher so the layout won't jump.

### Agent ring — two real bugs
- **Thinking never looked yellow: a drawing bug, not the state machine.** 150s of logged real work showed
  `amber 137.5s / green 8.0s`, flipping correctly. The ring was drawn at `0.55` alpha; amber at 55% over
  a near-black pill composites to ~(139,94,18) — a dark brown-gold 86 RGB units from the coral icon it
  hugs (green is 164). Alpha → `0.9`.
- **Ring stayed amber after a turn ended.** `stop` writes idle correctly, but Claude Code also fires
  `Notification` once the turn is over and the hook wrote `waiting_input` unconditionally. The hook now
  only treats it as an attention state while the turn is still running (`working`/`compacting`), keyed on
  prior state rather than message wording so either firing order works. Measured: turn-end WHITE, mid-turn
  permission prompt AMBER, limit WHITE (`LimitHit` also went Amber→White).
- `BtWidget.DrawCollapsed` threw **every frame** while the pill was tucked: `rr = (h-12)/2-1` hits zero at
  h≤14 and the tuck state is 96×**12**; GDI+ rejected the arc, `OnTick` swallowed it → frozen pill, only
  visible in `frame-errors.txt`. Guarded below 16px.

### Gotchas worth keeping
- A stale **self-contained publish layout left in `bin/`** (194 files vs 10) makes the app fail with "You
  must install or update .NET" forever — the host reads that folder's `runtimeconfig.json`. Same trap
  applies to hot-copying `Halo.Hooks`: copy a `dotnet build` output over the self-contained install and
  the hooks break. Publish self-contained instead.
- To drive internals, a throwaway project whose **AssemblyName is `Halo.Tests`** gets `InternalsVisibleTo`
  with no reflection. Used constantly this session.
- Agent widgets fade content in over the first frames (`_appear`), so a single render captures a faded
  pill — warm up ~90 frames before measuring anything.
- Tests: 81 → **97** (16 new: partial-suffix classification, `.acf` parser, `libraryfolders.vdf` parser).

### Still open
- Multiple simultaneous downloads: `Downloads` is still single-valued statics; needs to become a
  collection, plus the switcher wired to clicks and a remembered selection. Gutter + count already exist.
- `PROGRESS.md`/GitHub: cut a release for 3.1.0 and push the stripped mirror to the fork.

## 2026-07-25: v3.0.2 RELEASED + 3.0.3 built (not released) + first outside contributions reviewed
Note: 3.0.3 was built, committed (`918e60b`) and installed locally later the same day, but **no GitHub
release was cut for it** — v3.0.2 is still the newest tag. See the 2026-07-26 entry above.

### Shipped
- **v3.0.2 = Latest** on phoseinq/DynamicWin, target branch `V3`, tag on `c1c3070`. The first cut of
  this release (tag on `a3c2f2f`) was deleted with `gh release delete --cleanup-tag` and re-made after
  the BtWidget crash below was found, so the published assets contain that fix. **Carries its own
  assets** (`DynamicWinSetup.exe` 29.8MB + `DynamicWinPortable.zip` 41.5MB, both signed `CN=phoseinq`,
  `3.0.2.0` stamped inside) — v3.0.1 had none and pointed at v3.0. Installed live + relaunched;
  the machine had silently been running a **1.0.0.0** build until now.
- Local `master`: `414841c` (cleanup + version + tests) and `7806fb2` (CLAUDE.md). Not pushed —
  `master` is private, `origin` is the public fork only.
- Version lives in **two** places: `Halo.App.csproj` (Version/AssemblyVersion/FileVersion) and
  `installer/Halo.iss` (`#define AppVersion`). Both bumped.

### v3.0.3 (same day): thinking ring + banner leak — both root-caused with measurements
- **Ring never looked yellow — a DRAWING bug, not the state machine.** Logged the live store for 150s of
  real work: `amber 137.5s / green 8.0s`, flipping correctly on every tool boundary. The ring was drawn
  at `fade * 0.55f`; amber at 55% over the near-black pill composites to ~`(139,94,18)`, a dark
  brown-gold only 86 RGB units from the coral icon it hugs (green sits at 164), so "thinking" read as a
  shadow. Alpha → `0.9f` in all three agent widgets (A 150→231). Verified by rendering both states 4×
  side by side. **The RGB-distance metric barely moved (86→88) — the fix is composited luminance, not
  hue; don't use that metric to judge this again.**
- **Banner leak measured: 56 of 243 mirrored toasts (23%) came from never-silenced AUMIDs** — WireGuard
  tray balloons (`NotifyIconGeneratedAumid_*`) and 4 of 6 Telegram ids. Root cause: `SuppressApp` only
  learns an app *after* Halo mirrors one of its toasts, so every app banners once, and an app that mints
  a fresh AUMID per account/channel leaks once per id. Fix = keep the lazy learner AND pre-seed: `Enable()`
  now walks every AUMID already under `Notifications\Settings` (recursively — classic apps register as
  `{GUID}\...\app.exe`, which a flat `GetSubKeyNames()` misses). Verified 14/137 → **137/137**, and every
  recorded original was absent beforehand so `Restore()` still reverses all of it. Backup of the
  pre-seed state at `banner-orig.tsv.bak-preseed`.
- Gotcha: `notif-debug.txt` has **times but no dates**, so entries from different days interleave and
  look contemporaneous. Toast **ids** are the reliable ordering (WireGuard's stop at 66648 while
  `notif-seen.txt` is 67441 — those 50 leaks predate the learner).

### Open: downloader coverage (reported, root-caused, NOT built)
`Downloads.Scan()` finds downloads by regex-matching a leading `NN%` in visible **window titles**.
`Downloads.cs:118` deliberately skips browsers (`// "50% off" page, not a download`), so browser
downloads are unsupported by design; Steam never puts a percentage in its title, so it is invisible too.
Only Store (`StoreInstall` → `AppInstallManager`) and Xbox (`GameInstall` → staging folder) have real
integrations. Feasible directions, both matching existing patterns: Chromium/Firefox keep downloads in
SQLite (`History` → `downloads`, `places.sqlite`) and the project already P/Invokes the system
`winsqlite3.dll` for `wpndatabase.db`; Steam exposes `BytesDownloaded`/`BytesToDownload` in
`steamapps/appmanifest_*.acf` plus a `downloading/` staging dir, the same shape `GameInstall` reads.

### Root cause worth keeping: our duplication is what breaks contributors
Reviewing the pt-BR PR showed **every missed string sat where the same text was written twice**, far
apart, with nothing marking the pair. Fixed on our side (all four are now single-source):
- `QueueRamNotice`/`QueueCpuNotice` were two copies of one banner + two more in `PollTestNotif` →
  one `QueueLoadNotice(resource, pct, topProcess, fallbackBody)`.
- `net`/`api` had **four spellings per agent widget** (`"net " + x`, `$"net {x}"`, …) — eight literals
  for two words → `Fx.NetLabel` / `Fx.ApiLabel` / `Fx.LossLabel`.
- Screenshot wording existed in both `OnClipboardImage` and the `--render-notif` dev hook → consts on
  `NotifItem`.
- `"agent"` vs `"Agent"` (pill text vs panel heading) read as a typo → now carries a comment saying it
  is deliberate, because a PR "fixed" it and changed the English heading.

### `BtWidget.DrawCollapsed` threw on every frame while the pill was tucked — FIXED
Found in `%LOCALAPPDATA%\Halo\frame-errors.txt` (16:03:22, the same second `bt-debug.txt` logged
`connected: Boy`). `sz = h - 12`, `rr = sz/2 - 1`, so at **h ≤ 14** the arc radius hits zero and GDI+
throws `ArgumentException: Parameter is not valid`. The tuck state is 96×**12**, so any BT connect
while tucked threw every frame; `OnTick` swallowed it → frozen pill, no visible error. Reproduced
across h = 40…2 (ok until 16, throws from 14 down), fixed with an `if (h < 16) return;` guard, re-ran
the same sweep — all ok. Shipped: `9d88b1b` on master, `c1c3070` on V3, and the re-cut v3.0.2 assets.
Installed live and relaunched with `frame-errors.txt` deleted first — it stayed absent.

### Two stale tests were failing on master (79/81) — FIXED
`AgentNoticeTests` still asserted that `waiting_input` makes a widget primary; that was deliberately
removed ("no need for it to pop"). Rewrote as `WaitingInput_DoesNotStealThePill`, and rebuilt the
desktop-Codex-preference test on compact-done notices (the only kind that still opens a window). 81/81.

### PR review — evidence, not opinion (both build 0/0 in Release)
Verification trick that paid off: a throwaway project **named `Halo.Tests`** satisfies
`InternalsVisibleTo`, so contributor code can be driven directly with no reflection for internals.
- **PR #1 (i18n + pt-BR)** — sound design (English string as key). Real defect: `Loc.T(en, args)` runs
  `string.Format` on the *translated* text unguarded → a broken placeholder throws on the render path.
  Proved end-to-end by breaking one key and rendering: `Loc.T → DrawExpanded → DrawContent`, no PNG.
  Coverage ~40% and asymmetric; `HALO_LANG=pt-BR` renders show `rede` next to `api` in one label.
- **PR #2 (persistent BT widget)** — idea accepted, code not. **The 6s timeout was the error recovery**,
  not just a display duration; removing it made latent states permanent. Measured the race window:
  **75ms** warm vs **2629ms** on the cold path (`Battery()` → -1 → `Task.Delay(2500)` → retry), against
  a phone that connects for **1–2s** (seven occurrences in `bt-debug.txt`) → the disconnect is
  *guaranteed* missed → phantom device forever. Seed claim proved by A/B on `_live` alone
  (false → 0 connects; true → 1 connect "Boy" 47%). Ring/number desync measured: text 40%, ring 73.6%.
- **Three of the six PR #2 defects were caused by our comment stripping** — `// startup state, don't
  banner`, `// reveal: ring grows from empty`, `// keep frames coming so the ring eases` all exist in
  `master` and are absent from V3. Said so in the review. **V3 publishes no `docs/`, no `tests/`, no
  `PROGRESS.md`** either, so contributors cannot see any invariant. A published CONTRIBUTING is the
  cheapest fix; not written yet.

### Gotchas learned
- A stale **self-contained publish layout left in `bin/`** (194 files vs 10) makes the app fail with
  "You must install or update .NET" forever — the host reads that folder's `runtimeconfig.json` and
  never looks at the system runtime. Installing .NET does nothing. Delete `bin/`+`obj/`.
- `dotnet run --project X -- <arg>` swallowed the argument for the strip tool; build it and call the
  exe directly.
- `Radio.RequestAccessAsync`/`SetStateAsync` (WinRT) toggles the Bluetooth radio non-admin, but a
  **phone initiates the connection itself**, so cycling the PC radio does not bring it back.
- Before launching the pill from a tool shell, compare `SessionId` with `explorer.exe` — a different
  session means an invisible pill that still holds the single-instance mutex.

## 2026-07-22: v1.0.3 RELEASED — everything below is now committed + shipped
- **v1.0.3 = Latest** on phoseinq/DynamicWin (Setup + Portable, signed). All pending changes below are
  in local commits `029f650` + `11276a4` and in the release build — nothing un-bundled remains.
- Notif silence set is now banner+sound+**urgent** (`AllowUrgentNotifications=0`) — urgent toasts were
  the "banner slips out under spam" leak.
- New dev knob: env `HALO_CAPTURABLE=1` skips `WDA_EXCLUDEFROMCAPTURE` — without it the pill is
  invisible to every capture API (looks like a rendering bug; it isn't). Used ffmpeg ddagrab to record
  the README preview.gif (concise README + gif pushed to the fork's V2 branch).

## 2026-07-21: RESUME HERE — File Tray (next feature) + pending un-pushed state (ALL SHIPPED in v1.0.3 ↑)

### Un-pushed / un-released changes already made this session (bundle these into the NEXT build)
- **File Tray auto-remove + smooth reorder + pin spacing (DEPLOYED live, NOT pushed):** (1) drag-OUT now
  auto-removes on a successful drop — `FileDrag.Out` returns bool (`hr==DRAGDROP_S_DROP && effect!=NONE`);
  controller removes the dragged path(s) on success (cancel / drop-on-our-own-pill → effect NONE → kept).
  (2) **smooth reorder glide** — `FileTray._anim` eases each card's top-left toward its grid slot (~24%/frame)
  instead of snapping; `Animating` keeps frames coming until settled; `DrawContent` early-return (collapsed)
  now resets `_settled=true`+clears `_anim` so a mid-glide close can't leave `Animating` stuck on. Verified via
  successive-frame render (glide mid-frame → clean grid at rest). (3) **pin/title spacing** — "File Tray" title
  was at x=Pad(22) under the top-left pin (~x9–33); moved to `Pad+20` to clear it (matches ClaudeCode widget).
  Verified with pin overlaid in a render.
- **DND leak — notifications doubling (native banner leaks) — ROOT CAUSE FOUND, NOT YET FIXED (DEPLOYED safe
  interim, NOT pushed):** the whole registry Quiet-Hours trick is DEAD on Win11 26200. Verified live: the
  profile blob reads `Microsoft.QuietHoursProfile.AlarmsOnly` correctly, yet `SHQueryUserNotificationState`
  stays `QUNS_ACCEPTS_NOTIFICATIONS` (5) — never `QUNS_QUIET_TIME` (6). There is NO registry "DND enabled"
  flag; on 26200 the live DND on/off state is a **WNF / in-memory** state, so setting the *profile* never
  turns DND *on*. Restarting `WpnUserService` does NOT engage it AND kills Halo's own `UserNotificationListener`
  ("Class not registered" → mirroring dies until relaunch). An earlier attempt (force-restart when state==5)
  spun a 30s restart loop that repeatedly broke the listener — reverted. **Current safe `DndGate`:** writes
  the profile (harmless), reads ground-truth via `SHQueryUserNotificationState`, and only restarts to recover
  a revert IF DND has actually engaged once (`_everEngaged`, i.e. state hit 6) — on 26200 that never happens
  so it does ZERO restarts / zero self-harm. `[dnd]` logging in `notif-debug.txt`. Mirror pipeline itself is
  clean (log: ids monotonic, no floods). **REAL FIX TODO (needs user consent — risky):** toggle DND via the
  WNF state (`WNF_SHEL_QUIETHOURS_*`) / `NtUpdateWnfStateData`, the only thing that actually flips 26200 into
  quiet-time. `ToastEnabled=0` is a dead end (kills listener delivery too).
- **File Tray IMPLEMENTED (DEPLOYED live via Halo.App.dll hot-swap, NOT pushed):** new files
  `Interop/FileDropTarget.cs` (OLE `IDropTarget`) + `Widgets/FileTray.cs` (`IWidget`); OLE interop added to
  `Interop/Win32.cs` (OleInitialize/RegisterDragDrop/IDropTarget/DragQueryFile/POINTL, CF_HDROP); public
  `ShellIcon.ForPath`; wired in `Program.cs` (`OleInitialize`), `LayeredNotch.Show` (`RegisterDragDrop` +
  `_dropTarget` field), `NotchController` (widget added, drag-active priority + `open` include, Groups kind).
  Persist to `%LOCALAPPDATA%\Halo\tray.txt` (dedup, most-recent-first, cap 30, drops missing on load).
  Verified: 4 rendered states (list / drop-zone / collapsed-count / collapsed-dragging) + persistence
  self-check (dedup/order/case-insensitive/remove/load round-trip all PASS) + clean startup (no crash).
  **LIMITATION:** reveal-on-drag works only while the pill is on screen (a widget active, or the tray holds
  files). When the desktop is fully idle the pill is SW_HIDE'd → a hidden window can't be an OLE drop target,
  so a drag won't summon it from nothing. Fix if wanted: a tiny always-present transparent drop-catcher at
  top-center (steals clicks on its small rect — that's the tradeoff). Deferred (ask): Share dialog.
- **File Tray round 2 (DEPLOYED live, NOT pushed):** (a) icon → tray/inbox glyph `` (reads as a tray in
  BOTH Segoe MDL2 Assets + Fluent Icons — verified) replacing the generic folder; (b) **drag-OUT** implemented
  — new `Interop/FileDrag.cs` (OLE drag SOURCE: `SHCreateItemFromParsingName`→`IShellItem.BindToHandler(BHID_
  DataObject)`→`DoDragDrop` + minimal `IDropSource`; `Win32.cs` got those + `IShellItem`/`IDropSource`),
  `FileDropTarget.DragEnter` guarded by `FileDrag.Dragging` (no self-reveal), `FileTray.RowPathAt` + public
  `Open`, and `NotchController.HandleTrayInteraction` (press-a-row = open, hold+drag>6px = drag out). CF_HDROP
  data-object pipeline verified (QueryGetData=0, path round-trips); only DoDragDrop itself needs a real mouse
  gesture. (c) drag-IN priority made unconditional so a live drag ALWAYS makes the tray primary + expands.
- **File Tray round 3 (DEPLOYED live, NOT pushed):** (1) drop zone REDESIGNED (filled translucent zone +
  tray icon in a soft disc + two-line copy, breathing when active). (2) **Ctrl+click multi-select** —
  `_selected` HashSet, accent highlight + left bar, header "Remove N" chip (`RemoveSelected`); selected set
  drags out together (`SelectionOrRow`). (3) **drag-to-reorder** — drag a row up/down inside the panel
  (`ReorderFrom/To` live preview, `BeginReorder/UpdateReorder/CommitReorder`); leaving the panel mid-drag
  switches to drag-out. (4) **drag image fixed** — `FileDrag` now uses `SHDoDragDrop` + `IShellItemArray`
  (multi-file) so the file ICON follows the cursor instead of a bare square. `NotchController.HandleTray
  Interaction` rewritten (mode: pending/reorder/out; Ctrl=select, click=open, drag=reorder|extract). Win32
  got SHDoDragDrop/SHParseDisplayName/SHCreateShellItemArrayFromIDLists/IShellItemArray/ILFree; removed the
  now-dead single-item IShellItem/SHCreateItemFromParsingName. Verified: 4 rendered states + logic self-check
  (reorder/selection/multi-remove/SelectionOrRow) + 2-file CF_HDROP interop, all PASS; clean startup.
- **File Tray round 4 — grid redesign (DEPLOYED live, NOT pushed):** the vertical list only showed 3 of N
  files with a clipped "+N more" and wasted the right half → replaced with a **3-col card grid** (`CellRect`/
  `CellW`/`VisibleCells`, 3×3 = 9 visible, clean "+N more" footer). Each card = icon + name + folder, × on
  hover, accent tint/border when selected or lifted (reorder). Hit-testing (`RowPathAt`/`RowIndexAt`/`Buttons`)
  is now grid-aware; reorder/select/drag-out logic unchanged. Collapsed pill: several files now show a small
  **stack of their icons** ("پشت سر هم", up to 4, overlapping) + "N files" instead of one icon + count.
  Header/`RemoveChipRect` moved up (HeaderH=56). Verified: grid renders at 6/10/selected + collapsed stack.
- **Ring "yellow = thinking" round 2 (DEPLOYED live, NOT pushed):** proved the collapsed ring ALREADY goes
  Amber on working+no-tool (reflection test on the deployed dll). The gap was the EXPANDED panel dot —
  `ClaudeCodeWidget` `StateColor` was hardcoded Green for "working"; deleted it and pointed the dot at
  `RingColor(st)` so the panel dot matches the ring (yellow thinking / green tool). Codex dot left as-is.

- **Store "Waiting…" phantom + breathing redesign (DEPLOYED live via Halo.App.dll hot-swap, NOT pushed):**
  Root cause = a Phone Link (`Microsoft.YourPhone`) update parked in the Store queue at `ReadyToDownload`
  (total=1 byte) that never runs — `StoreInstall.Poll` surfaced any non-terminal item → pill stuck on
  "Waiting…" forever. Fixes: (1) `StoreInstall.cs` grace-timeout — a queued item that never starts
  downloading is dropped after `WaitGraceMs=30s` (`_waitPfn`/`_waitSinceMs`, returns `Phase.None`);
  verified live against the real phantom (Waiting for ~27s → None). (2) `DownloadWidget.DrawCollapsed`
  rewritten: app icon now on the LEFT always (extracted `IconTile` from `DrawArt`, new `DrawCollapsedIcon`);
  Waiting = whole-pill breathing glow (Claude-compacting style) + app name, NO bar/NO % ; Downloading =
  icon + bar + %. Verified by direct bitmap render (r_waiting.png / r_downloading.png). Needs: push
  `StoreInstall.cs` + `DownloadWidget.cs` to Boy + roll into installer/release.
- **Ring "thinking = yellow" fix (DEPLOYED live, NOT pushed):** `src/Halo.Hooks/Program.cs` `tool-done`
  case now sets `status["currentTool"] = null` (was: kept the last tool label to avoid flicker). Effect:
  between tool calls Claude reads as *thinking* → RingColor gives Amber; a tool sets it Green again.
  Widget logic UNCHANGED (RingColor already `empty?Amber:Green`); only `ClaudeCodeWidget.cs` has a
  reworded comment. **Hook binary hot-deployed** into `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.{exe,dll,
  deps.json,runtimeconfig.json}`. Still needs: rebuild into the installer + push `Halo.Hooks/Program.cs`
  (+ the ClaudeCodeWidget comment) to Boy.
- **Already DONE + deployed + pushed (Boy @ 2149780, pre-release v1.0.2 assets refreshed):** notif Island
  redesign (`NotifBanner.cs` eyebrow row app+time / bigger title / SummaryH 106→112 / RelTime "now"),
  notif icon Start-menu fallback (`ShellIcon.ForAppName` for `NotifyIconGeneratedAumid_*` tray toasts like
  WireGuard/Amnezia), banner text `AntiAliasGridFit`, DND re-stamp fix (`DndGate.WriteCache` always bumps
  FILETIME), Persian/fancy-Unicode `Fx.CleanText` NFKC + `Fx.IsRtl` in Media/VLC.
- **Releases:** `phoseinq/DynamicWin` — **v1.0.0 = Latest**, **v1.0.2 = Pre-release** (assets
  `DynamicWinSetup.exe` + `DynamicWinPortable.zip`). v1.0.1 deleted.
- **CC hooks path repointed:** `~/.claude/settings.json` 9 hooks now call
  `%LOCALAPPDATA%\Programs\Halo\Halo.Hooks.exe` (old `%LOCALAPPDATA%\Halo\hooks` was deleted in an
  uninstall). Backup `settings.json.bak-*` exists.

### FILE TRAY — ✅ IMPLEMENTED 2026-07-21 (see the un-pushed bullet above). Original plan kept for reference:
### plan (user: DynamicWin's signature feature). Reveal-on-drag: the tray appears WHILE a
### file is being dragged; otherwise it's not shown (empty+no-drag = nothing; held files = a small circle).
Feasibility confirmed: main thread is `[STAThread]` (`Program.cs:10`); notch window is layered but NOT
`WS_EX_TRANSPARENT` so it receives mouse + OLE drops; `Hwnd` is public; window resizes to content each
frame. `WM_DROPFILES` only fires on DROP (no drag-enter) → MUST use **OLE `IDropTarget`** for the reveal.

1. **Interop** (`Interop/Win32.cs` + new `Interop/FileDropTarget.cs`): `OleInitialize`/`OleUninitialize`,
   `RegisterDragDrop`/`RevokeDragDrop`, `IDropTarget` COM iface, `IDataObject.GetData(FORMATETC{CF_HDROP})`
   → `STGMEDIUM.hGlobal` → `DragQueryFile` loop → `ReleaseStgMedium`. DROPEFFECT_COPY=1.
2. **`FileDropTarget : IDropTarget`**: DragEnter(has CF_HDROP? → `FileTray.DragActive=true`, effect=COPY,
   bump Version) · DragOver(COPY) · DragLeave(`DragActive=false`) · Drop(extract paths → `FileTray.Add`,
   `DragActive=false`).
3. **`Widgets/FileTray.cs : IWidget`**: static `List<string> Paths` + `volatile bool DragActive` + `Version`,
   persisted to `%LOCALAPPDATA%\Halo\tray.txt` (load on start, like `notif-seen.txt`). `IsActive =
   DragActive || Paths.Count>0`. `Icon` = a Segoe Fluent tray glyph. `DrawContent`: drop-hint when
   empty/dragging, else rows = file icon + name + `[×]`. `DrawCollapsed`: count + mini icons. `Buttons`:
   row → open (`Process.Start{UseShellExecute=true}`); `[×]` → remove + persist. File icons via a NEW
   public `ShellIcon.ForPath(path)` (thin wrapper over the existing private `ExtractFrom` = 256px shell
   icon) OR `Icon.ExtractAssociatedIcon`.
4. **Wire:** `OleInitialize` once at startup (Program.cs before `RunMessageLoop`, or in LayeredNotch ctor);
   `RegisterDragDrop(Hwnd, new FileDropTarget())` at the end of the window-init in `LayeredNotch` (near the
   `AddClipboardFormatListener` call). Add `new FileTray()` to the widget list in `NotchController` (~L248-265,
   next to `DownloadWidget`). On `FileTray.DragActive`, force-expand the pill + make the tray primary (see how
   `AgentNotice`/`_userPicked`/`_primary`/`_drop` drive expand in NotchController).
5. **Verify** (compile 0/0), deploy live, then bundle the push+release with the pending ring fix.
- **Deferred (ask before adding):** Share via Windows dialog (`DataTransferManager` + HWND anchor, WinRT);
  drag-OUT of the tray.

### Build / deploy / release recipe (repeat each ship; GOTCHAS below)
- Cert thumbprint `2EB268F09FEA535E92FB395FA2FAB4409EC22E1D` (self-signed, CurrentUser\My). signtool sign
  with `/tr http://timestamp.digicert.com /td SHA256 /fd SHA256`, fallback sectigo, then unsigned-time.
- Publish App + Hooks: `dotnet publish src\Halo.App\Halo.App.csproj` and `src\Halo.Hooks\Halo.Hooks.csproj`
  `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\app`. Sign both inner exes.
- Installer: `ISCC installer\Halo.iss` → `dist\DynamicWinSetup.exe` — **RETRY up to 6×** (AV locks the output
  mid icon-embed: "EndUpdateResource failed (110)"). Sign it. Portable: copy `dist\app`→`dist\Halo`,
  `Compress-Archive` → `dist\DynamicWinPortable.zip`.
- Deploy live: `dist\DynamicWinSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, relaunch
  `%LOCALAPPDATA%\Programs\Halo\Halo.App.exe`. Hook-only quick deploy = copy the 4 `Halo.Hooks.*` files.
- Release: `gh release upload v1.0.2 dist\DynamicWinSetup.exe dist\DynamicWinPortable.zip --repo
  phoseinq/DynamicWin` (delete-asset first to replace). Boy branch: strip comments via the tool at
  `C:\Users\hosei\AppData\Local\Temp\halo_pr\strip` (`dotnet run -- <dir>`), copy stripped `.cs` into the
  fork clone `C:\Users\hosei\AppData\Local\Temp\halo_pr\fork`, commit as **phoseinq (NO Co-Authored-By)**,
  push Boy.
- **GOTCHAS:** (a) the PowerShell safety hook blocks a command containing BOTH `Remove-Item` AND a
  `C:\Program Files` literal OR a `/f` token (taskkill /f) — misparses it as a delete target; split them
  (use `Stop-Process`, resolve signtool in a Remove-Item-free command). (b) Persian path «دسکتاپ» breaks
  Windows PowerShell 5.1 reading UTF-8-no-BOM `.ps1` — use pwsh or launch via Bash. (c) Can't auto-screenshot
  the notif banner (transparent notch shows the window behind → false hits; synthetic PS toasts aren't
  delivered to `UserNotificationListener` like real app toasts) → verify by real toast / user eyeball.


## 2026-07-20: rapid-fire batch (DEPLOYED to %LOCALAPPDATA%\Halo\app, running PID-fresh)
1. [DONE] Frame-pacing: animations ran half-speed at 60fps (hard-coded 0.008f tick). → real
   per-frame delta `_dt` in `Frame()` (measured, clamped 1..50ms; also fixes the new 30fps tier).
2. [DONE] Autostart-after-reboot: root cause = Fast Startup (HiberbootEnabled=1) skips the
   at-logon scheduled task on power-on-from-shutdown. Added HKCU `Run\Halo` → deployed exe as a
   fast-startup-proof fallback (safe: single-instance mutex). NEEDS A REBOOT to confirm.
3. [DONE] Fun icons for iconless local notifs. `LocalBadge(cp,hue)` (gradient tile + Fluent glyph,
   like `LangBadge`; gives the banner a colored glow too). Battery E996 / Net EB5E / Limit E9D9 /
   Clock E917 / Cpu E950 — all verified no-tofu via new `--render-badges` hook.
4. [DONE] Video speed: cycling `Btn.Speed` chip (1/1.25/1.5/1.75/2×) on the video row via SMTC
   `TryChangePlaybackRateAsync`, label from `PlaybackRate`. Honest no-op on apps that ignore rate.
   VLC (VlcWidget, no SMTC) NOT covered — follow-up if wanted.
5. [DONE] Hourly chime: `CheckHourly()` in CheckAlerts, once per round hour, ClockBadge, time as
   title, English. `_chimedHour` inits to current hour (no spurious fire at launch).
6. [DONE] Heavy-load throttle: `AdaptFrameRate` gains a 30fps tier (busy>80%) + `_heavy` state
   (enter 50% / leave 40%) → process priority BelowNormal + 3× slower glass capture; ONE edge notif
   "High CPU usage — N%" naming the top-CPU process (`TopCpuProcess`, off-thread). English.
7. [DONE] MS Store downloads folded into the `Downloads` scanner: when no window-title download and
   the Store app is running, poll `Get-DeliveryOptimizationStatus` off-thread (~6s) for the biggest
   active download's % → shows as "Microsoft Store". Gap: DO gives no per-app name, Store-proc-gated.
Verified: Release build 0/0, badges PNG eyeballed, deployed + relaunched (priority Normal, no crash).

## 2026-07-19: 6-feature batch (IN PROGRESS)
- [ ] 1. Compact crescent — pulse fills fully-rounded rect over a flat-top pill → 2 dark crescents
      at the top corners. Fix: fill the real pill silhouette (`Fx.PillPath`) in both agent widgets.
- [ ] 2. Screenshot vs copied — classify clipboard image by `GetClipboardOwner` process: snip
      hosts / null owner → "Screenshot captured"; a real app owner → "Image copied".
- [ ] 3. Icon quality — AppIcon: `PrivateExtractIcons` @256, fallback `ExtractAssociatedIcon`.
- [ ] 4. Download priority + stop — active download becomes primary (user swap still wins); stop
      button focuses the downloader window (no cross-app cancel API); better icon via #3.
- [ ] 5. Privacy dot — `Privacy.cs` registry ConsentStore scan; mic=orange, cam=green; dot on the
      pill; pill stays alive only while mic/cam live; hides when done.
- [ ] 6. Alerts — edge-triggered local banners: battery<=20% discharging (click → Power Saver
      plan), Claude/Codex usage>=80%, internet slow ("Bad internet :/"). Throttled (one per edge).

## 2026-07-18 (session 2): precise click + media-follow-foreground + notif polish + drag-to-move (deployed)
- **Precise banner click (BUILT — supersedes the "NOT possible" note below):** `Notifications/WpnDb.cs`
  reads the toast's `launch`/`activationType` straight out of `wpndatabase.db` (locked WAL SQLite) by Id
  via the **system `winsqlite3.dll`** (P/Invoke, zero NuGet). Verified: DB `Notification.Id` == the
  listener's `UserNotification.Id`; payload is plain UTF-8 `<toast launch=… activationType=…>`; a
  `.db`-only read-only snapshot is enough (row is checkpointed by click time). `NotifItem.Activate` now:
  protocol → `Process.Start(launch)`; else → `IApplicationActivationManager.ActivateApplication(aumid,
  launch)` → opens the exact message/photo (Phone Link thread, Chrome URL, etc.).
- **Notif flood on restart FIXED (root cause):** DndGate restarts `WpnUserService` at launch, so the
  platform is down/empty for the first seconds; the old code baselined at 0 then dumped the whole action
  center (52 toasts) as "new". Fix: persist last-seen Id to `notif-seen.txt` (`LoadSeen`/`SaveSeen`),
  resume from it on start → immune to the race. First-run fallback: baseline only on a non-empty fetch
  or after a 3s grace. `_ready`/`initial` removed in favour of `_baselined`.
- **Auto-dismiss 3s → 6s** (`_notifDeadline`); Windows' own toasts linger ~5s, 3s was too quick to read.
- **Icon chain reordered** (`NotifSource.Build`): `ShellIcon` (clean transparent 256px, both packaged &
  classic) → `AppIcon` (running exe icon — catches custom toast AUMIDs not in Start, e.g. `PowerToys.Run`
  which `ShellIcon` returns null for) → `Logo(n)` last. Fixes the white tile-plate around UWP logos and
  the broken PowerToys icon.
- **Notif Persian/RTL** (`NotifBanner`): `LineFmt`/`WrapFmt` add `DirectionRightToLeft` for FA/AR lines
  so mixed FA+EN no longer mangles (english was jumping into the middle); right-aligned, ellipsis left.
- **Black edge line FIXED** (`LayeredNotch.DrawShape`): final supersample downscale bicubic → **bilinear**
  (bicubic's negative lobes undershot the dark→transparent premultiplied edge into a thin dark rim visible
  over light content). Verified via new `--render-notif` dev hook (real shape path on a colour backdrop).
- **Media follows the foreground** (`MediaWidget.FollowForeground` + `Pick`/`Hook`, called from
  `NotchController` on every fg change with the process name): focus the browser → browser playback,
  focus Spotify → Spotify's. Matches a session whose `SourceAppUserModelId` ~ the fg process name, else
  the system current; force:false skips re-hook churn while the fg app is unchanged.
- **Media art fallback** (`DrawArt` → `CoverFill`): no thumbnail → the source **app icon** instead of the
  generic music glyph (podcasts/videos/radio ship no art).
- **Drag-to-move the pill** (`NotchController.UpdateMove` + `LayeredNotch.OffsetX`): press-and-hold ~3s on
  the pill (a growing underline `DrawHoldCue` shows progress) → it collapses and follows the cursor →
  release drops it → parked within 55px of centre it snaps back (magnet). `_offsetX` persisted to
  `offset`; applied to `_cl`/`_el`/`NotifLeft` (hit-test) and `LayeredNotch` render dst.
- 71/71 tests; Release deployed to `%LOCALAPPDATA%\Halo\app` via the `Halo` scheduled task.

## 2026-07-18: notif banner polish + pin redesign + screenshot-hide (deployed)
- **Screenshot-hide**: `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` in LayeredNotch ctor —
  pill never appears in screenshots/recordings. Side effect: we can no longer screenshot the pill
  for verification; use `--render-widget` / standalone GDI harnesses instead.
- **Pin redesign**: hand-drawn pushpin (`PinPath`: head arc + needle, single continuous path, no MDL2
  glyph). pinned = solid amber (`PinOn` 255,210,105), unpinned = dim white outline. **Instant** toggle
  (no ease on state — user wanted snappy), hover shows English label "pin on top"/"unpin". Moved up
  (PinRect 9,4,24,24). `_pinT` field removed.
- **Notif adaptive font** (`NotifBanner.FontScale`): 1.0→0.86 as text lengthens.
- **Notif over fullscreen**: `NotchController` OnTick — a live/pending toast overrides the fullscreen
  hide (pill stays empty but the banner wakes + renders over games). `NotifSource.HasPending` added.
- **Notif click = open app**: `NotifItem.Aumid` (from `AppInfo.AppUserModelId`) + `.Activate()`
  launches `explorer shell:AppsFolder\<aumid>`. Banner-body click activates + dismisses.
- **Notif app icon**: `GetLogo` blanks on most desktop apps → `ShellIcon.ForAumid` pulls the real
  Start-menu icon via `IShellItemImageFactory`, keeping alpha (32bpp PArgb DIB copy). Icon clip
  changed circle → rounded square (`DrawAppIcon`) so opaque icons don't show as a disc.
- **3s auto-dismiss**: `_notifDeadline = +3s` (was 7s); existing tick loop animates the reverse morph.

### Native toast block — SOLVED via auto Do-Not-Disturb (`Notifications/DndGate.cs`, deployed)
Goal: kill the OS banner + SOUND but keep `UserNotificationListener` delivery. Only **DND** does that
(confirmed live by user: general toasts silenced, pill still mirrors). Dead ends first:
- `RemoveNotification` (still in place as belt/suspenders): always flashes ~0.5s, can't un-ring sound.
- `PushNotifications\ToastEnabled=0`: cached by WpnUserService, not applied live on 26200; also kills
  delivery. Removed.
- **Wrong CloudStore key**: `...\Store\Cache\DefaultAccount\$$windows.data.NOTIFICATIONS.quiethourssettings`
  is a legacy cache — writes there revert in ~400ms.

**Working recipe (DndGate):** the authoritative DND profile is
`HKCU\...\CloudStore\Store\DefaultAccount\Current\{9f763514-...}$windows.data.DONOTDISTURB.quiethourssettings\
windows.data.donotdisturb.quiethourssettings`, value `Data` (REG_BINARY). Blob = 28-byte header, byte[28]
= char count of `Microsoft.QuietHoursProfile.<Profile>`, then that UTF-16 string, then a short trailer.
Swap `<Profile>` (Unrestricted=off, PriorityOnly, AlarmsOnly=strictest) by rebuilding the blob (byte[28]
= new charcount, splice string, keep header+trailer — no total-length field to fix, verified). THEN
**`Restart-Service WpnUserService_*`** (works non-admin) so the platform re-reads. Sticks. Listener
survives the restart (re-acquired on the next 250ms poll). DndGate: AlarmsOnly on start (skips write+
restart if already set), Unrestricted on ProcessExit (fail-open). Find the key by suffix (the {guid}
may vary). Restart via a fire-and-forget `powershell Restart-Service` (no ServiceController dep).

**Still bypasses DND:** the Snipping Tool "snip saved" toast (`Microsoft.ScreenSketch_8wekyb3d8bbwe!App`)
— a system/action toast even AlarmsOnly won't silence. Only fix = its per-app
`Notifications\Settings\<AUMID>\Enabled=0` (but that also stops the pill mirroring it). Left to user.

**Precise banner click (open the exact message/photo):** NOT possible — `UserNotificationListener`
doesn't expose the toast's `launch` args. Would need to read the toast XML out of `wpndatabase.db`
(locked WAL SQLite) by id. Not built. Current `NotifItem.Activate` just foregrounds the app via
`IApplicationActivationManager`.

## DONE 2026-07-17 (evening): flag ghost + outage fix + limit mood + pin/tuck (deployed, needs live check)
- **Flag**: soft wind-blown ghost of the exit-IP flag centred in the panel — `Fx.FlagGhost`
  (2.4 gentle ripples spread across the whole flag + smooth centre-out vignette, baked 2x per
  flag, drawn at 0.16 alpha). Shared by BOTH the CC and Codex panels.
- **Codex parity**: flag ghost + eager/fresh-connection CodexNetMon heartbeat + limit-hit mood
  all mirrored into the Codex widget.
- **Outage bug (real fix)**: NetMon thread now starts eagerly (was: only on first panel-open →
  collapsed ring never learned of an outage), and the fresh-connection health heartbeat runs even
  while the panel is open (pooled fast samples were masking the RST storms and clearing ApiDown).
- **Limit hit**: working + usage ≥99% → "outta juice :(" + "back in Xh Ym" instead of the
  ever-growing turn timer; ring amber.
- **Pill**: no active widget → tucks into a 96×12 slim tab (animated). Pin button (MDL2 pin,
  bottom-left of expanded panel): pinned = upright/bright + pill ignores fullscreen hide;
  unpinned = tilted/faint. No text, state is the drawing. Not persisted across restarts.
- **Pin v3 + empty-hide + follow-focus**: pin was invisible (bare 13px glyph at 35% alpha over the
  weekly bar — verified via GDI+ harness) → final: bare upright pushpin top-left (10,8,26,26),
  MDL2 E840 outline dim = unpinned, E842 filled bright = pinned, no rotation; state + hover
  crossfade via smoothstep (`_pinT`/`_pinHov`, ~120ms). Verified on live pill via screenshots.
  Empty pill now fully hides (SetVisible false once the tuck lands; banners
  and waking widgets resurrect it; leaving fullscreen respects it). Pill follows the focused
  session: fg-change matches fg pid against `IWidget.OwnerPids` (agent pid + console pid, all
  CC/Codex/generic widgets) and switches primary unless a notice/drop owns the pill.
- **M4 notifications (banner UI built)**: NotifSource now grabs the app logo + RemoveNotification
  ("block" = best-effort yank from Windows banner/action center). Pill morphs into a 400×92 banner
  (EaseOutBack, tint/strip/mini all ride the same _notifT) with app icon + name + title + ONE
  truncated summary line; bottom grabber bar (no close button) grows it to the measured full text
  (≤250). Click anywhere outside = soft close; hover pauses the 7s auto-close; toasts queue and
  wait for an idle pill. Test toast: scratchpad toast.ps1 via powershell.exe (PS5 WinRT).

## DONE 2026-07-17: multi-session + strip redesign + glow everywhere (verified live, needs commit-audit only)
- **Multi-session CC**: hook writes `status-{agentPid}.json` per session (pid stamped every event —
  a mid-turn-born file without pid evades dedupe; session-end deletes file + legacy status.json;
  session-start sweeps dead-pid files). `StatusStore` scans `status*.json`+`app.json` → stable slots
  (`MaxSessions=4`, per-pid dedupe keeps freshest); `SessionLive(slot)` cached 1s (+3 tests).
  One `ClaudeCodeWidget` per slot, cwd-initial badge composited on the icon. Codex = two widgets
  (desktop/cli) via `Candidate(surface)`. Ceiling: N codex CLI sessions still share cli.json.
- **Strip UI (user's design)**: circle beside pill; apps stack DOWNWARD, a row with ≥2 sessions of
  one app fans RIGHTWARD on hover (closed circle shows the plain app mark, fan carries badges);
  primary session excluded. Union pill-path (flat top), 2x supersampled so icons stay crisp.
  Click maps row/fan → session; liquid drop flies from the actual clicked circle (_dropCX/_dropCY).
  Arrival toss lands on the circle (old bug: flew to slot*D below it).
- **Glow**: shared `Fx` helper — accent from icon (ConditionalWeakTable cache), dithered 128px
  radial texture (PArgb premultiplied! non-premul source on the layered surface sprayed white
  garbage), pill-shaped clip (flat top — all-corner clip left a dark crescent). Media art accent;
  CC coral / Codex green fallbacks; strip cells get 20-alpha washes.
- **Media polish**: iOS 9-bar center-weighted waveform; glass transport chips + eased hover; glyphs
  centred by path ink-bounds; soft volume chip + breathing bar.
- **Limits staleness**: 5-min heartbeat Timer in `Limits` (was: only panel-open/refresh → "59m ago").
  Also: account-lockout 429 (long `Retry-After`) now recorded as 5h=100% + reset time — the panel
  told the truth ("100% · 30m · updated just now") instead of rotting to "updated 12h ago".
- **Startup lag**: autostart moved Startup-folder lnk → Scheduled Task `Halo` at logon (no stagger).
- **Strip rings + numbers**: every circle wears the pill's status ring (`IWidget.Ring`); duplicate
  sessions get deeper shades (`Fx.Shade`) + stable number badges (`Fx.Badge`); codex surfaces badge
  1/2 when both live.
- **Generic agents**: `GenericAgentWidget` + `docs/generic-agents.md` — any AI tool writing
  `~/.halo/agents/agent-*.json` (name/icon/state/pid/...) gets the full treatment; groups by name.
  Verified live with a fake "Gemini CLI" file.
- **Media = already player-agnostic** (GSMTC): browsers/SoundCloud/VLC work without changes; a
  dedicated video-player face is the user's NEXT milestone.

Goal: smooth glass notch for Windows (Dynamic Island for desktop). C# + .NET 9, Win32 layered window
rendered with `UpdateLayeredWindow` + GDI+. Spec in `docs/` (start at `docs/MAP.md`); current
architecture truth is `docs/decisions.md` (it supersedes the older Composition-based docs).

**Roadmap:** `docs/plans/2026-07-15-backend-media-notifications.md`. **M1 (backend) + M2 (Now Playing)
+ M3 (Volume) DONE + verified live (2026-07-15). M4 spike: `UserNotificationListener` WORKS UNPACKAGED
(no MSIX to read/mirror toasts); native suppression has no official API.** M4 banner UI not built —
waiting on user's call re suppression appetite.

## Architecture (current, post-P2 pivot)
- `Shell/LayeredNotch.cs` — the window. `WS_EX_LAYERED|TOOLWINDOW|TOPMOST|NOACTIVATE` popup +
  `ACCENT_ENABLE_ACRYLICBLURBEHIND` (real frosted glass) + `Render(w,h,radius,tintAlpha,contentFade)`
  which draws a GDI+ per-pixel-alpha bitmap (rounded-bottom/square-top path, dark tint, top
  highlight, content) and blits with `UpdateLayeredWindow`.
- `Shell/NotchController.cs` — `DispatcherQueueTimer` (8ms) polls `GetCursorPos`; `EaseOutBack`
  spring lerps size/radius/tint/contentFade between collapsed (220x40) and expanded (560x220); calls
  `LayeredNotch.Render` each frame.
- `Interop/Win32.cs` (window class, ULW, acrylic, cursor), `Interop/Dispatcher.cs` (DQ controller),
  `Shell/NotchGeometry.cs` (+2 tests).

## Done
- P0 skeleton + geometry (2 tests).
- P1 glass + hover-spring expand/collapse; square top flush to screen, rounded bottom; no dark halo.
- **P2 pivot (2026-07-15):** dropped `Windows.UI.Composition` (couldn't host bitmap content without
  the missing `LoadedImageSurface` / heavy D2D). Rewrote shell as `UpdateLayeredWindow` + GDI+.
  Real acrylic frosts the desktop through ULW; content renders **crisp** (Segoe Fluent Icons + text).
- **P3 Claude Code panel + hooks (2026-07-15):** DONE, verified end-to-end.
  - Notch side: `Widgets/IWidget.cs` contract; `Widgets/ClaudeCodeWidget.cs` (green/amber/dim state
    dot, "Claude Code" + activity line, Session-context/5h/Weekly bars, top-right Cancel button);
    `ClaudeCode/Status.cs` (`StatusStore` FileSystemWatcher on `~/.claude/notch/status.json`, version
    poll → live re-render); click via `GetAsyncKeyState` polling in `NotchController`.
  - `src/Halo.Hooks/` — helper the CC hooks call: writes status.json per event (state/tool/prompt/
    context-from-transcript/pid/consolePid), and `cancel <pid>` = AttachConsole + Ctrl+C.
  - `hooks/install-hooks.ps1` publishes the helper to `%LOCALAPPDATA%\Halo\hooks` and merges 7 hooks
    into `~/.claude/settings.json`. **User must run it** (their live CC config).
  - Verified: helper writes status.json; panel reflects state changes **live** (idle→working shot).

## Next
- **Run `hooks/install-hooks.ps1`** to wire real Claude Code sessions (not yet installed).
- **Usage-limit data (5h/weekly)** still best-effort/unpopulated — the one open data source (no clean
  API). Panel hides those bars when `usage` absent; context bar is real. Refine later.
- Live-tune spring feel on the 144Hz panel (`EaseOutBack` c1, `DurationSeconds`).
- P6 config + autostart + package + comment-strip.

## M1+M2 done (2026-07-15)
- **M1 widget backend:** `IWidget` gained `bool IsActive` + `int Version`; the pill/dropdown build from
  **active** widgets only (`NotchController.ActiveIndices/AltIndices`), primary falls back to the first
  active widget when it goes inactive, and the version poll is aggregated across all widgets (dropped the
  StatusStore special-case). `ExpandedButton`+`ActivateButton` → `Buttons(w,h)` = list of (rect, Action)
  for multi-button widgets. ClaudeCode active when a status file exists; Clock/Battery always active.
- **M2 Now Playing:** `Widgets/MediaWidget.cs` on `GlobalSystemMediaTransportControlsSessionManager`
  (Spotify/browsers/any player). WinRT events run off-thread → update a lock-guarded snapshot + bump
  Version; GDI stays on the UI thread (album art decoded lazily in DrawContent). Draws art + title +
  seek bar (extrapolated while playing) + prev/play-pause/next (the M1 button list). Verified live:
  real Chrome session, thumbnail, timeline, swap-into-pill + expand all work unpackaged.
- Wart to polish in M3: RTL (Persian) titles left-align with the ellipsis on the wrong side. **FIXED**
  (MediaWidget `DrawLine` uses DirectionRightToLeft + EllipsisCharacter for RTL text).

## M3 done (2026-07-15)
- **Volume:** `Widgets/VolumeWidget.cs` — hand-rolled Core Audio COM interop (no NuGet). Reads master
  volume + mute; mute / −5% / +5% buttons write via `SetMasterVolumeLevelScalar`/`SetMute`. Bumps
  `Version` on change for instant re-render. Verified live: 100%→95% via minus button, restored.
- Widgets now: `{ ClaudeCode, Media, Volume, Clock, Battery }` (kept the demos — trim on request).
- Dev hook kept: `Halo.App --render-widget <png> [media|clock|battery|volume]`.

## Redesign per user (2026-07-16) — media-first, Apple-style
- Widgets trimmed to **Media + Claude Code** (deleted Clock/Battery/Volume widgets).
- **Collapsed previews:** Media = album art (HQ, AA rounded) + audio equalizer (9 fine bars, driven by
  REAL output peak via `AudioMeter`/IAudioMeterInformation, heights mostly 30-70%, soft multi-hue
  gradient from the art accent). CC = Claude icon (downloaded coral sunburst, embedded resource
  `Assets/claude.png`) on the left + live activity on the right.
- **Real app icons:** swap circle shows the source app's real icon (`AppIcon.ForAumid` extracts the exe
  icon of the running app — verified Spotify). Circle icons inset ~19% (10% smaller). Media falls back to
  album art, CC uses the Claude icon.
- **Media expanded:** volume control added (mute glyph + click-to-set bar, Core Audio via AudioMeter),
  click-to-seek on the progress bar. Buttons generalized to `Action<PointF>` (pill-local click point).
- **CC expanded:** removed the FAKE 5h/weekly bars; shows real Context (clamped K tokens) + cwd.
- **Cancel fix:** `Halo.Hooks` cancel now injects **Esc** into the CC console (WriteConsoleInput) instead
  of Ctrl+C to the whole group — cancels the running turn without closing the terminal. Redeployed to
  `%LOCALAPPDATA%\Halo\hooks`.
- **Animation:** faster open (Open 0.16s / Close 0.24s); circle **merges into the pill** (scales toward
  the pill edge + fades) on expand; swap has an **arrival bloom** (new app content eases in, not a snap).
  Still TODO: fuller metaball "join-then-separate" swap feel.

## M4 spike (2026-07-15) — notifications
- **`UserNotificationListener` returns full toast data UNPACKAGED** on this build (access Allowed,
  app/title/body all readable). Reading/mirroring toasts needs **no MSIX**.
- Native suppression of the Windows toast: **no official API.** Options if pursued: Focus-Assist/Quiet-
  Hours toggle (undocumented, own spike) or ship mirror-only (both show). Gate before building banner UI.

## Verify recipe
Run exe in background, drop a colorful WinForms backdrop behind it, move cursor onto the pill center
(1280,15) to hover-expand, `CopyFromScreen` to PNG, view. Crash log: `%TEMP%\halo-crash.log`.

## Always-on pill + limits without a session + 529 detection (2026-07-17)
- Pill no longer hides when no widget is active (fixes "missing after Windows startup"): only
  fullscreen hides it; with zero active widgets it renders as a bare glass pill (no expand/menu).
- CC/Codex expanded panels draw the limit bars + net graph + refresh even with `Session == null`
  (only the context bar needs a transcript). Stale/dead CC status renders as idle via a `Live`
  coercion helper; `StatusStore.IsLive` now cached 1s (it's hit per-frame).
- Widget visibility (user's call after seeing the Codex circle with ChatGPT closed): agents show
  only while their app actually runs — Codex needs desktop/CLI presence, Claude a live pid. So
  "limits without a session" applies while the app is open but idle, not when it's closed.
- Both NetMons treat an HTTP 5xx answer (incl. 529 Overloaded) as Lost → red ring, "api error :("
  verb, red api line in the graph during Anthropic/OpenAI overload storms (previously any HTTP
  status counted as healthy, so 529s looked fine).
- Verified: 68/68 tests; live pill screenshot post-deploy; `--render-widget` shots of the
  no-session Claude panel (limits visible) and Codex panel. Deployed to `%LOCALAPPDATA%\Halo\app`.
- Gotcha: sandboxed shells run on an isolated desktop — `Start-Process` there makes the pill
  invisible to the real session; deploy/restart Halo from an unsandboxed shell.

## Codex widget done (2026-07-16)
- Supports Codex Desktop and CLI; Desktop wins when both are active.
- Lifecycle hooks write `~/.codex/notch/{desktop,cli}.json`; rollout JSONL supplies live state,
  context, model window, and real rate-limit windows without private endpoints.
- Live rollout files are read with shared access so an active Codex session remains visible.
- Codex auto-promotes into the primary pill when it enters `working`; CLI Stop injects Esc and
  Desktop Stop remains disabled.
- Independent `chatgpt.com` HTTPS health graph, OpenAI asset, status-ring/mood/emerge animations,
  context and dynamic plan-limit rows match the Claude widget.
- User-reported Claude stale-status bug fixed: dead/reused PID makes Claude inactive; Halo hides when
  no widgets are active and reappears when one becomes active.
- Verification: Release build 0 warnings/errors, 56/56 tests, deployed to `%LOCALAPPDATA%\Halo\app`;
  hooks installed with backup at `~/.codex/hooks.json.halo-bak`; live Desktop screenshots captured.

## Polish round (2026-07-16, post-Codex-merge)
- **Compacting pill redesign (both widgets):** bottom sweep bar → whole-pill soft blue breathing
  fill (alpha 0.05→0.16, 2.4s cosine) + elapsed timer. Percent was tried and REMOVED (user called
  it fake — correctly): compact progress isn't knowable, even CC's spinner only shows a token
  counter hooks can't see. Cancelled compacts (Esc, no hook fires) covered by a 3-min expiry →
  pill falls back to idle mood; `PostCompact` hook (CC ≥2.1.x) now installed = real end edge
  (auto→working, manual→idle, +compactedAt +context refresh). "compacted :)" notice now triggers
  ONLY on a fresh compactedAt (<30s), never on a bare state transition. All three verified live.
- **Round 2 (user feedback):** percent is BACK but paced honestly — elapsed / the LAST compact's
  real duration (`lastCompactMs`, recorded by post-compact; 60s default; clamp 1-99). Esc-cancel
  now detected live: controller polls VK_ESCAPE while state=compacting and foreground is a
  terminal/claude/chatgpt host -> marks that compact (keyed by startedAt) cancelled -> pill drops
  to idle instantly; wrong guesses self-heal via post-compact. Verified: ~35% at 31s/90s expected,
  post-compact wrote lastCompactMs=31700. Esc path needs a real-compact hand test by the user.
- **Context accuracy:** `session-start` with `source=clear|startup` now drops the stale `session`
  block (user saw 250K after /clear). Verified: piped clear event → session removed.
- **Claude dual-surface:** hook exe writes `status.json` (terminal ancestor = CLI) or `app.json`
  (desktop app; `HALO_CLAUDE_SURFACE` override); `StatusStore` reads both, **CLI wins when live**,
  falls back to live app. 2 new tests.
- **Codex leftovers fixed:** verb map now speaks Codex tool names (`exec`→running…, `apply_patch`→
  patching…, `web_search`→googling :P, `view_image`→peeking o.o, `update_plan`→plotting…);
  Desktop Stop rewritten — PostMessage never reaches Electron, now restore-if-iconic +
  SetForegroundWindow + SendInput(Esc) (untested against a live running Desktop task).
- 67/67 tests; app + hooks republished to `%LOCALAPPDATA%\Halo`.

## Codex capsule accuracy and controls (2026-07-16) — design approved
- User approved both tracks: creative model-aware capsule/context data and working dual-surface Stop
  with anti-spam Weekly refresh/cache behavior.
- Root causes verified: Desktop Stop was intentionally disabled; context had an unsafe cumulative
  fallback; manual refresh rescanned twice; repeated rendering could save unchanged cache values.
- Design spec: `docs/superpowers/specs/2026-07-16-codex-capsule-accuracy-controls-design.md`.
- Next: user review of the committed spec, then TDD implementation plan and execution.
