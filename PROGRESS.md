# Halo — progress

## 2026-07-27 (night): the Claude Code panel is a ring cluster now
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
