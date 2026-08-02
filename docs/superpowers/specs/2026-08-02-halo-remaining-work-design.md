# Halo - remaining work, decomposed

Date: 2026-08-02
Status: awaiting review

Eight reported items span six independent subsystems. One spec covering all of them would be a plan
nobody can review, so this document defines six units that ship on their own, in the order they should
be built. Each unit names its root cause where one is already established, its change, and how it is
verified. Nothing here depends on a unit later in the list.

Already fixed this session and out of scope below: the About page's version (`Directory.Build.props`
is now the single source), the morph's frame rate (a morph always gets 120), the pin's hover label,
and the "Start with Windows" status row reading the Startup folder instead of the scheduled task.

## Order

A -> B -> C -> D -> E -> F. A is a contained bug and unblocks nothing, so it goes first to bank a win.
F is last on purpose: after B and C we know which code is genuinely Windows-shaped, and drawing the
seams before that is drawing them from memory.

---

## A. The ask banner drops one of the two built-in choices

**Root cause, established.** `AskBanner` models exactly one built-in extra row:

```
internal static readonly AskOption Other = new("Chat about this", "say it in your own words");
private static bool HasOther(PendingAsk ask) => ask.IsQuestion;
if (HasOther(ask)) options.Add(Other);
```

The CLI offers two distinct affordances - "Type something" (answer the question in free text) and
"Chat about this" (leave the question and talk instead). The banner folds both into that single row:
line 211, `bool typing = typed != null && IsOther(row.Option)`, turns the "Chat about this" row into
the text field as soon as the user types. So the two are not merely displayed as one, they are
*implemented* as one, and the free-text choice has no row of its own.

**Change.** Two built-in options rather than one, each with its own row and its own outcome. The
typing field binds to the free-text row; the chat row stays a plain choice. `IsOther` splits into two
predicates so nothing has to compare against a display string.

**Open question for implementation, not for this design:** whether `Halo.Hooks`' answer envelope
already distinguishes a free-text answer from a chat break-out. If it does not, the envelope gains a
kind field. `AskEnvelope` / `AskFlow` are where that lives.

**Verified by.** `AskBannerLayoutTests` gains cases for both rows present, their hit-rects being
distinct, and the typing field landing on the free-text row. Plus a `--render-*` PNG of a banner with
four options so the row count is eyeballed, since the report came from a screenshot in the first place.

---

## B. Motion: the black flash, a frame-rate ceiling, and bars that step

Three symptoms, one surface. Grouped because they all touch the render loop and the drawing helpers.

### B1. The black flash mid-morph

**Not yet root-caused.** The honest statement: the pill flashes dark for roughly a frame while it
grows or shrinks. It cannot be screenshotted (`WDA_EXCLUDEFROMCAPTURE`), and `--render-*` hooks render
one still frame, so they cannot show a timing artefact. The leading hypothesis is the glass backdrop:
the frosted look is a captured, blurred region refreshed on its own cadence (`CaptureOpenMs = 16`,
`CaptureCollapsedMs = 50`), and on expansion the newly revealed area is tinted before it has a capture
that covers it - dark tint over nothing reads as black.

**Approach.** Instrument before changing anything: a debug counter that records, per frame, the morph
size, the age of the capture, and whether the capture covers the current rect; dumped to a file behind
an env knob so a real morph can be replayed as numbers. Only then decide between (a) forcing a capture
refresh on the first frame of a morph, (b) capturing at the expanded rect for the whole morph so the
region never under-covers, or (c) holding the previous frame's glass and cross-fading. The cadence fix
already shipped may have reduced it; the instrumentation says by how much.

**This unit may end in "no change needed".** That is an acceptable outcome and must be reported as one
rather than papered over with a speculative fix.

### B2. A user-editable frame-rate ceiling

**Why.** `AdaptFrameRate` picks 30/60/120 from CPU headroom, and the morph now forces 120. On a weak
machine the right answer may be "never go above 60", and that is a judgement about the user's hardware
that the app cannot make from a CPU sample alone.

**Change.** One new settings row, `appearance.fps`, next to the existing `appearance.motion`. Values:
Auto (today's behaviour, the default), 120, 60, 30 - a ceiling, not a target. `AdaptFrameRate`'s
chosen tier and `CadenceFps`'s morph override are both clamped to it, so the ceiling is honoured
everywhere rather than only when idle. Auto must remain the default: the adaptive behaviour is
measured, and a fixed number is worse for most machines.

**Verified by.** `CadenceTests` extends to the clamp - a 60 ceiling must hold a morph at 60, and a 120
ceiling must not raise a 30 tier. Logic-only, no UI harness needed.

### B3. Progress bars step instead of jumping

**Root cause.** Bars are drawn straight from their source value, and the sources arrive in jumps -
downloaded bytes land per network read, agent context lands per turn. So the bar teleports.

**Change.** Ease the *displayed* value toward the true value each frame, the same way rings already do
through `EaseRings` / `Toward` in `NotchController`. One shared helper so every bar behaves alike
rather than each widget rolling its own. Two constraints that are not negotiable here: the displayed
value must always converge on the real one and must never lead it, because a bar that runs ahead of
the truth is an invented number, which this project has rejected twice. "1 percent at a time" is
implemented as a per-frame cap on movement, not as integer-only stepping - at 120fps integer steps
would take 0.8s to cross a bar and would themselves look like stepping.

**Verified by.** Logic tests on the easing helper: convergence, never overshooting, never moving
backwards on a forward-only source, and reaching 100 percent exactly. Plus a `--render-*` PNG of a
mid-animation bar.

---

## C. The settings panel truth pass

**Why.** One row - "Start with Windows" - held three separate defects: a status probe reading a
Startup folder that no longer matters, a button opening that folder, and copy calling a scheduled task
a shortcut. A row that lies is worse than a row that is missing. There is no reason to think that row
was unique.

**Change.** Enumerate every row in `Catalog.cs`; for each, trace its key to a consumer and its action
to an effect. Produce a table of row -> key -> who reads it -> what it does. Fix what is dead or
lying. `appearance.fps` from B2 lands here.

**Verified by.** A test that every `Toggle`/`Choice` key in the catalog has a reader somewhere in
`Halo.App`, and that every `RowKind.Status` with a button has a case in `Actions.Run`. That test is
what stops the next dead row, which is more valuable than any single fix this pass makes.

---

## D. Bug reports, on a crash and on demand

**What exists.** `Program.cs` already writes `%TEMP%\halo-crash.log` on an unhandled exception. Nobody
is ever told it happened, and nothing helps a user turn it into a report.

**Change.** Two entry points, one path.
- *On a crash:* the next launch notices a crash log newer than the last one it acknowledged and offers
  a report through the existing local-alert machinery. It does not interrupt - a crash that already
  happened is not urgent.
- *On demand:* a row in the settings panel's About section.

Both open a prefilled GitHub issue in the browser and put the log's path on the clipboard. Prefilled
means version, Windows build, and which widgets were active - facts the app already holds.

**Constraints.** Nothing is uploaded automatically, and the log is never pasted into the issue body:
it can contain notification text and file paths, which belong to the user. They attach it, or they do
not. This keeps the unit inside the no-new-dependencies rule too - it is a `Process.Start` on a URL.

**Verified by.** Logic tests on the report body builder and on the "is there a new crash to mention"
decision, which is an edge-latch of the same shape as the existing local alerts.

---

## E. A new pin icon

Pure art, no logic. The current pushpin carries three states - dim outline (off), solid slate
(pinned), lit amber head (visible in captures) - and any replacement must keep all three legible at
14px against a dark pill, plus the hold-gesture growth. Now that the hover label is gone, the icon is
the only readout, which raises the bar rather than lowering it.

Verified with the existing `--render-pin` hook, which draws every state side by side.

---

## F. Linux-ready seams, inside the existing project

**Scope, as chosen: no new projects.** No `Halo.Core`, no `Halo.Platform.Windows`. The work is to
separate pure logic from Win32 calls where they are currently tangled, and to introduce an interface
only where a real boundary already exists rather than one imagined for a port that has not started.

**Why this and not the full split.** Interfaces with exactly one implementation are a guess about the
second one. This repo's own history is a record of dead ends kept as comments; a speculative
abstraction layer would be a large, untested one. Extracting pure logic, by contrast, pays off
immediately - it is the same move that made `NotchVisibility` and `AgentNoticeCoordinator` testable,
and it is exactly what a port would need first.

**Change.** Survey `Halo.App` for the genuine seams - media, notifications, downloads, Bluetooth, the
overlay window - and for each, separate the decision-making from the platform call, leaving the
platform call as a thin edge. Document the resulting boundaries in `docs/`. Introduce an interface
only where two callers already differ.

**Verified by.** Newly-pure logic gains tests it could not have before; that test count is the
measure of this unit. Release stays 0/0 and behaviour is unchanged - this unit must not be visible to
a user.

---

## Testing and risk, across all units

Every unit keeps the project bar: Release build 0 warnings / 0 errors, `dotnet test` green with the
count reported, a `--render-*` PNG described for anything visual, and a dated `PROGRESS.md` entry
saying root cause, change, verification, and deployed vs. pushed.

The two units carrying real risk are B1, which may not have a fix, and F, which touches a lot of
working code for no user-visible gain. Both are scoped so that stopping early still leaves the tree
green.
