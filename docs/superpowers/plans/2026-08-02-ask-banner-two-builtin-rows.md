# Ask banner: both built-in rows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The ask banner shows both of Claude Code's built-in rows - "Type something" and "Chat about
this" - and each one drives the row it names.

**Architecture:** The banner appends its own rows after the options the hook forwarded. Today it
appends exactly one, `AskBanner.Other`, labelled "Chat about this". Answers for an `AskUserQuestion`
are delivered as *keystrokes into the agent's terminal*: a numbered option is typed as its digit, and
a built-in row is reached by walking `Down` off the end of the list and pressing `Enter`. That walk is
`ask.Options.Count` steps, which lands on the first row past the options - "Type something" - while the
banner labels it "Chat about this". So the single row is both incomplete and mislabelled. This plan
splits it into two rows and gives each its own walk distance.

**Tech Stack:** C# / .NET 9, GDI+, xunit. No new packages.

## Global Constraints

- No new NuGet packages. `System.Drawing.Common` is the only one.
- Release build must end at 0 warnings / 0 errors: `dotnet build Halo.sln -c Release`.
- Source files stay ASCII - no Persian in code, comments, or test fixtures.
- UI strings are English.
- Comments explain the root cause or the failed alternative, in lowercase prose. Do not add comments
  that restate the code.
- Every interop call stays wrapped in `try { } catch { }`; a failed probe degrades silently.
- Work in `.worktrees/claude-master` on branch `master`.
- Never display invented numbers, and never let the banner claim an outcome it does not deliver -
  that mislabelling is the bug being fixed here.

## Background the implementer needs

Read these before Task 1. They are short and this plan will not make sense without them.

- `src/Halo.App/Widgets/AskBanner.cs` lines 55-75: the `Other` option, `IsOther`, `HasOther`. The
  comment there records that this row USED to say "Something else / type your own answer" and was
  renamed when Claude Code's box changed. It is about to change again; update that comment rather
  than deleting its history.
- `src/Halo.App/ClaudeCode/AskStore.cs` lines 176-238: `Press` and `Write`. `Write` is the walk. Its
  comment says the key map was "established by driving a live box and reading back what came out, not
  from any documentation" - which is why Task 5 exists.
- `src/Halo.App/Shell/NotchController.cs` lines 1833-1838: the click handler. `IsOther` starts typing
  mode; every other row answers immediately by label.
- `src/Halo.App/Shell/NotchController.cs` line 2271: Enter while typing sends the typed text through
  the same `Answer(ask, label)` entry point, where it fails to match any option label and falls
  through to `Write`.

**Observed row numbering in the case that was screenshotted (four options, both built-ins):**

| Terminal row | What it is | What the banner shows today |
|---|---|---|
| 1-4 | the four options | rows 1-4, correct |
| 5 | Type something | *missing* |
| 6 | Chat about this | shown as row 5, but its click walks to row 5 |

**The complication that shapes this whole plan.** The two built-in rows are not always both there.
Reported from use: sometimes only one appears, sometimes the other, sometimes neither, sometimes both -
all four combinations occur. The banner today appends one row unconditionally, so it is wrong in three
of the four cases, and because answers are delivered by *counting Down presses*, being wrong is not
cosmetic: with "Type something" absent, "Chat about this" moves up a row and the same walk now
activates something else entirely.

The pill cannot currently know which of them the box is showing. The hook forwards the tool's options
untouched and these rows are Claude Code's own UI, absent from the payload. `ConsoleRead` can type into
a terminal but cannot read one back, so the box cannot be interrogated either.

That is why Task 0 exists, and why it comes first: **no row may be drawn until we can tell whether it
is there.** If Task 0 finds no reliable rule, the fallback is explicit and is not a failure - draw
neither built-in row and let those answers be given in the terminal. This project already holds the
line that a control the underlying app cannot honour is hidden rather than shipped as a silent no-op,
and a row that activates the wrong thing is worse than a row that is missing.

---

### Task 0: Establish, by driving live boxes, when each built-in row appears

Nothing else in this plan can be trusted until this is answered. It is investigation, not code, and it
ends with a table written into this document.

**Files:**
- Modify: this plan - append the findings to the "Findings from Task 0" section at the bottom.

**Interfaces:**
- Produces: the presence rule and the clamp/wrap answer that Tasks 1 and 3 both read.

- [ ] **Step 1: Produce all four combinations and record each one**

Drive real `AskUserQuestion` prompts in a terminal and photograph or transcribe the box each time.
Vary the things most likely to matter, one at a time: the number of options, whether options carry
descriptions, whether the question is multi-select, and the tool being a question versus a permission
prompt. For each box record: how many options, whether "Type something" is present, whether "Chat
about this" is present, and the row number of each.

The goal is a rule expressed in terms of something the pill can actually see - the fields on
`PendingAsk`. A rule that depends on anything else is not usable, and finding that out is a real
result.

- [ ] **Step 2: Determine whether the highlight clamps or wraps at the bottom**

In a box with four options, press Down far more times than there are rows - say fifteen - and see
where the highlight ends up. If it clamps at the last row, over-walking is a deterministic way to
reach the bottom row whatever is above it, and Task 3 can use that instead of counting. If it wraps,
counting is the only option and every count must be exactly right.

Record the answer. This single fact decides how fragile the whole mechanism is.

- [ ] **Step 3: Write the findings into this plan**

Fill in the "Findings from Task 0" section at the bottom of this document with the table and the
clamp/wrap answer. Commit it before writing any code, so the rule is reviewable on its own.

```bash
git add docs/superpowers/plans/2026-08-02-ask-banner-two-builtin-rows.md
git commit -m "docs: record which ask built-in rows appear when, driven live"
```

- [ ] **Step 4: Decide the shape, and say so out loud**

One of three outcomes, and which one it is changes Tasks 1 and 3:

1. **A rule exists in terms of `PendingAsk`.** Proceed as written; `BuiltInsFor` implements it.
2. **No rule, but the box clamps at the bottom.** Draw only the chat row, reached by over-walking to
   the bottom - correct whether or not a free-text row sits above it. Drop the free-text row from
   Task 1 and drop `AskDelivery.FreeText` from Task 3.
3. **No rule and the box wraps.** Draw neither built-in row. Tasks 1 and 3 shrink to deleting `Other`
   and its walk; Task 4's PNG then shows options only. Say this plainly in `PROGRESS.md` as a
   capability that was removed because it could not be made correct.

---

### Task 1: Two built-in rows in the model and the layout

**Files:**
- Modify: `src/Halo.App/Widgets/AskBanner.cs` (the `Other` block at ~55-75, and `Layout` at ~124)
- Test: `tests/Halo.Tests/AskBannerLayoutTests.cs`

**Interfaces:**
- Consumes: `PendingAsk`, `AskOption(string Label, string Description)` - both already exist.
- Produces: `AskBanner.FreeText`, `AskBanner.Chat` (both `static readonly AskOption`);
  `AskBanner.IsFreeText(AskOption)`, `AskBanner.IsChat(AskOption)`, `AskBanner.IsBuiltIn(AskOption)` -
  all `internal static bool`. `AskBanner.Other` and `AskBanner.IsOther` are removed; Tasks 2 and 3
  depend on the new names.

- [ ] **Step 1: Write the failing test**

Add to `tests/Halo.Tests/AskBannerLayoutTests.cs`:

```csharp
    // The box offers up to two rows past the options - free text and a break-out to the prompt - and all
    // four combinations occur. The banner appended exactly one unconditionally, labelled as the second
    // while its click walked to the first, so it was wrong in three cases out of four.
    //
    // Ask(...) below builds the shape Task 0 found produces BOTH rows. Substitute the real inputs from
    // the Findings table; if Task 0 landed on outcome 2 or 3, delete the cases that no longer exist.
    [Fact]
    public void Both_built_in_rows_follow_the_options_when_the_box_has_both()
    {
        var rows = AskBanner.Layout(Ask(
            new AskOption("a", ""), new AskOption("b", "")), AskBanner.W).Rows;

        Assert.Equal(4, rows.Count);
        Assert.True(AskBanner.IsFreeText(rows[2].Option), "row 3 should be the free-text row");
        Assert.True(AskBanner.IsChat(rows[3].Option), "row 4 should be the chat row");
    }

    // The three other combinations. Each one used to draw a row that was not on the box, or hide one that
    // was - and because the answer is delivered by counting Down presses, a phantom row does not merely
    // look wrong, it activates whatever really sits at that position.
    [Fact]
    public void Only_the_row_the_box_actually_has_is_drawn()
    {
        foreach (var (ask, wantFreeText, wantChat) in BuiltInCases())
        {
            var rows = AskBanner.Layout(ask, AskBanner.W).Rows;
            int extra = rows.Count - ask.Options.Count;
            Assert.Equal((wantFreeText ? 1 : 0) + (wantChat ? 1 : 0), extra);
            Assert.Equal(wantFreeText, rows.Any(r => AskBanner.IsFreeText(r.Option)));
            Assert.Equal(wantChat, rows.Any(r => AskBanner.IsChat(r.Option)));
        }
    }

    // Built from the Findings table Task 0 wrote: one entry per combination, each constructed from the
    // PendingAsk fields that were shown to drive it.
    private static IEnumerable<(PendingAsk Ask, bool FreeText, bool Chat)> BuiltInCases()
        => AskBuiltInCases.All;

    [Fact]
    public void The_built_in_rows_are_told_apart_by_identity_not_by_label()
    {
        // a real option is allowed to carry the same words
        var impostor = new AskOption("Chat about this", "say it in your own words");
        Assert.False(AskBanner.IsChat(impostor));
        Assert.False(AskBanner.IsBuiltIn(impostor));
        Assert.True(AskBanner.IsBuiltIn(AskBanner.Chat));
        Assert.True(AskBanner.IsBuiltIn(AskBanner.FreeText));
    }

    [Fact]
    public void A_tool_permission_prompt_gets_no_built_in_rows()
    {
        var ask = new PendingAsk("n", 1, "s", "Bash", "ls", "run this?",
            new[] { new AskOption("yes", ""), new AskOption("no", "") },
            DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.Equal(2, AskBanner.Layout(ask, AskBanner.W).Rows.Count);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "FullyQualifiedName~AskBannerLayoutTests"
```

Expected: FAIL - `AskBanner.IsFreeText` / `IsChat` / `IsBuiltIn` / `FreeText` / `Chat` do not exist.

- [ ] **Step 3: Replace the single built-in with two**

In `src/Halo.App/Widgets/AskBanner.cs`, replace the `Other` / `IsOther` / `HasOther` block:

```csharp
    // Claude Code's own question UI always lets you ignore the options, and a banner that only offered
    // the canned answers quietly removed that. Appended rather than sent by the hook: the hook forwards
    // the tool's options untouched, and these are the pill's, so inventing them there would put choices
    // in the payload that Claude never offered. Reference identity is how the click handler tells them
    // apart - the label is display text and a real option could carry the same one.
    //
    // What sits past the options has changed under us twice. It was one bare free-text field
    // ("Something else / type your own answer"); then the box replaced it with "Chat about this", which
    // cancels the question and drops the words into the prompt, and this row was renamed to match. The
    // box now offers BOTH, in that order, and one row could only ever name one of them - so the row said
    // "Chat about this" while its keystroke walk landed on "Type something", which is a banner answering
    // a different question than the one it showed. Two rows, two walks, and AskStore.Write is told which.
    internal static readonly AskOption FreeText = new("Type something", "answer in your own words");
    internal static readonly AskOption Chat = new("Chat about this", "leave the question and talk instead");

    internal static bool IsFreeText(AskOption option) => ReferenceEquals(option, FreeText);
    internal static bool IsChat(AskOption option) => ReferenceEquals(option, Chat);
    internal static bool IsBuiltIn(AskOption option) => IsFreeText(option) || IsChat(option);

    // Reachable only by walking down off the end of the list (see AskStore.Write). These rows are Halo's,
    // not Claude's: the hook never invents an option Claude did not offer. Which of them the box is
    // showing is not in the payload and the terminal cannot be read back, so this is a rule derived from
    // driving live boxes - see the Findings table in the plan. A row drawn that the box does not have is
    // not a cosmetic error: the answer is delivered by counting Down presses, so it fires whatever really
    // sits at that position.
    internal static IReadOnlyList<AskOption> BuiltInsFor(PendingAsk ask)
    {
        if (!ask.IsQuestion) return Array.Empty<AskOption>();
        var built = new List<AskOption>(2);
        if (HasFreeTextRow(ask)) built.Add(FreeText);
        if (HasChatRow(ask)) built.Add(Chat);
        return built;
    }
```

`HasFreeTextRow` and `HasChatRow` are the rule from Task 0's Findings table, written in terms of
`PendingAsk` fields. Implement them from that table - each gets a one-line comment saying what was
observed, not what the code does. If Task 0 reached outcome 3, both return `false` and the two
`AskOption`s above are deleted with them.

Then in `Layout`, replace `if (HasOther(ask)) options.Add(Other);` with:

```csharp
        options.AddRange(BuiltInsFor(ask));
```

- [ ] **Step 4: Run the tests to verify they pass**

```
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "FullyQualifiedName~AskBannerLayoutTests"
```

Expected: PASS, all tests in the class.

- [ ] **Step 5: Fix the two remaining `IsOther` call sites so the solution compiles**

`src/Halo.App/Widgets/AskBanner.cs` ~line 211, in `Draw` - the typing field belongs to the free-text
row only:

```csharp
            bool typing = typed != null && IsFreeText(row.Option);
```

`src/Halo.App/Shell/NotchController.cs` ~line 1833 - both built-ins collect words, so both begin
typing; Task 3 is what makes them differ:

```csharp
                    if (AskBanner.IsBuiltIn(_askChips[i].Option)) BeginTyping(_askChips[i].Option);
```

`BeginTyping` does not take a parameter yet. For this task only, add the parameter and ignore it:

```csharp
    private void BeginTyping(AskOption row)
    {
        if (_askTyped != null) return;
        _askBuiltIn = row;
        _askTyped = _askDraftNonce == _ask?.Nonce ? _askDraft : "";
    }
```

and add the field next to `_askTyped` (~line 260):

```csharp
    private AskOption? _askBuiltIn;   // which of the two rows past the options is collecting the words
```

- [ ] **Step 6: Build and run the full suite**

```
dotnet build Halo.sln -c Release
dotnet test tests\Halo.Tests\Halo.Tests.csproj
```

Expected: 0 warnings / 0 errors, all tests pass. Report the count.

- [ ] **Step 7: Commit**

```bash
git add src/Halo.App/Widgets/AskBanner.cs src/Halo.App/Shell/NotchController.cs tests/Halo.Tests/AskBannerLayoutTests.cs
git commit -m "fix: the ask banner shows both rows past the options

It appended one row labelled Chat about this, while its keystroke walk landed
on Type something. Two rows now, told apart by identity."
```

---

### Task 2: The typing field lands on the free-text row

**Files:**
- Test: `tests/Halo.Tests/AskBannerLayoutTests.cs`

**Interfaces:**
- Consumes: `AskBanner.Layout`, `AskBanner.IsFreeText`, `AskBanner.IsChat` from Task 1.
- Produces: nothing new. This task is a regression pin.

The draw path was changed in Task 1 Step 5; this task proves it, because "which row turns into the
text field" is the exact thing that was wrong and it has no other test.

- [ ] **Step 1: Write the failing test**

```csharp
    // The words are typed into the row that promises to take words. When one row served both purposes
    // this could not be got wrong; with two rows it silently can.
    [Fact]
    public void Only_the_free_text_row_can_become_the_typing_field()
    {
        var rows = AskBanner.Layout(Ask(new AskOption("a", "")), AskBanner.W).Rows;

        Assert.True(AskBanner.IsFreeText(rows[1].Option));
        Assert.False(AskBanner.IsFreeText(rows[2].Option));
        Assert.False(AskBanner.IsFreeText(rows[0].Option));
    }
```

- [ ] **Step 2: Run it**

```
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "FullyQualifiedName~AskBannerLayoutTests"
```

Expected: PASS immediately - Task 1 already made it true. If it fails, Task 1 Step 3 put the two rows
in the wrong order; the free-text row comes first, matching the box.

- [ ] **Step 3: Commit**

```bash
git add tests/Halo.Tests/AskBannerLayoutTests.cs
git commit -m "test: pin the typing field to the free-text row"
```

---

### Task 3: Each built-in row walks to the row it names

**Files:**
- Modify: `src/Halo.App/ClaudeCode/AskStore.cs` (`Answer` ~148, `Press` ~182, `Write` ~219)
- Modify: `src/Halo.App/Shell/NotchController.cs` (~line 2271, where Enter sends the typed text)
- Test: `tests/Halo.Tests/AskQueueTests.cs`

**Interfaces:**
- Consumes: `AskBanner.FreeText` / `AskBanner.Chat` from Task 1; `PendingAsk.Options`.
- Produces: `internal enum AskDelivery { Option, FreeText, Chat }` in `Halo.ClaudeCode`;
  `AskStore.Answer(PendingAsk ask, string label, AskDelivery delivery = AskDelivery.Option)`;
  `AskStore.WalkSteps(int optionCount, AskDelivery delivery)` as `internal static int` - the pure
  part, which is what gets tested.

This is the task that fixes the reported defect. `Write` currently walks `ask.Options.Count` steps,
which reaches the first row past the options. That row is "Type something". "Chat about this" is one
further.

- [ ] **Step 1: Write the failing test**

Add to `tests/Halo.Tests/AskQueueTests.cs`:

```csharp
    // The banner reaches the rows past the options by walking Down off the end of the list. With four
    // options the box highlights row 1, so four steps land on row 5 and five land on row 6. Which row
    // those ARE depends on whether the box is showing a free-text row: without it, the chat row moves up
    // one and a walk sized for the other layout activates the wrong thing. Getting this off by one is a
    // banner that answers a question it did not ask.
    [Theory]
    [InlineData(4, AskDelivery.FreeText, true, 4)]
    [InlineData(4, AskDelivery.Chat, true, 5)]
    [InlineData(4, AskDelivery.Chat, false, 4)]   // no free-text row: chat is the first row past the options
    [InlineData(1, AskDelivery.FreeText, true, 1)]
    [InlineData(1, AskDelivery.Chat, false, 1)]
    public void The_walk_reaches_the_row_it_names(int options, AskDelivery delivery, bool hasFreeText, int steps)
        => Assert.Equal(steps, AskStore.WalkSteps(options, delivery, hasFreeText));

    [Fact]
    public void A_numbered_option_is_not_walked_to_at_all()
        => Assert.Equal(0, AskStore.WalkSteps(4, AskDelivery.Option, true));
```

- [ ] **Step 2: Run it to verify it fails**

```
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "FullyQualifiedName~AskQueueTests"
```

Expected: FAIL - `AskDelivery` and `AskStore.WalkSteps` do not exist.

- [ ] **Step 3: Add the delivery kind and the pure walk**

In `src/Halo.App/ClaudeCode/AskStore.cs`, above `internal sealed class AskStore`:

```csharp
// Which of the three ways an answer leaves the pill. The two built-in rows differ only in how far down
// the box you walk before pressing Enter, but that one step is the difference between answering the
// question and abandoning it.
internal enum AskDelivery { Option, FreeText, Chat }
```

Inside the class, next to `Write`:

```csharp
    // The box highlights row 1, so N steps land on row N+1: with N options that is the first row past
    // them. Which row that is depends on what the box is showing - the chat row is one further only when
    // a free-text row is above it, and assuming it always is was the bug.
    internal static int WalkSteps(int optionCount, AskDelivery delivery, bool hasFreeText) => delivery switch
    {
        AskDelivery.FreeText => optionCount,
        AskDelivery.Chat => optionCount + (hasFreeText ? 1 : 0),
        _ => 0,
    };
```

`hasFreeText` comes from `AskBanner.BuiltInsFor(ask).Any(AskBanner.IsFreeText)` at the call site in
`Write`, so the drawn rows and the walk are computed from one source and cannot disagree - the same
reason `Layout` is separate from painting.

- [ ] **Step 4: Run it to verify it passes**

```
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "FullyQualifiedName~AskQueueTests"
```

Expected: PASS.

- [ ] **Step 5: Thread the delivery kind through Answer, Press and Write**

`Answer` gains the parameter and passes it on:

```csharp
    internal bool Answer(PendingAsk ask, string label, AskDelivery delivery = AskDelivery.Option)
    {
        if (ask.IsQuestion) return Press(ask, label, delivery);
```

`Press` uses it instead of guessing from whether the label matched an option:

```csharp
    private bool Press(PendingAsk ask, string label, AskDelivery delivery)
    {
        if (ask.Pid <= 0) { Trace($"no pid for {ask.Nonce}"); return false; }
        int index = -1;
        if (delivery == AskDelivery.Option)
            for (int i = 0; i < ask.Options.Count && index < 0; i++)
                if (string.Equals(ask.Options[i].Label, label, StringComparison.Ordinal)) index = i;

        bool sent = index >= 0 && index < 9
            // the box numbers its rows from one, and beyond nine there is no single key to send
            ? Interop.ConsoleRead.Type(ask.Pid, (index + 1).ToString())
            : Write(ask, label, delivery);
        Trace($"{(index >= 0 ? "row " + (index + 1) : delivery + " words")} -> pid {ask.Pid} = {sent}");
```

`Write` takes the delivery and asks `WalkSteps` how far to go:

```csharp
    private static bool Write(PendingAsk ask, string text, AskDelivery delivery)
    {
        int pid = ask.Pid;
        int steps = WalkSteps(ask.Options.Count, delivery);
        if (steps <= 0 || string.IsNullOrWhiteSpace(text)) return false;
        if (!Interop.ConsoleRead.Press(pid, Interop.ConsoleRead.VkDown, steps)) return false;
```

The rest of `Write` - the pooled Enter / sleep / Type / Enter sequence - is unchanged. Leave its
comment about the sleeps; add one line recording that the walk distance is now per-row.

- [ ] **Step 6: Send the delivery kind from the click handler**

In `src/Halo.App/Shell/NotchController.cs`, where Enter commits the typed text (~line 2271), pass what
`BeginTyping` recorded:

```csharp
            string answer = _askTyped.Trim();
            var delivery = AskBanner.IsChat(_askBuiltIn!) ? AskDelivery.Chat : AskDelivery.FreeText;
```

and use `delivery` in the `Answer` call on the following lines. Clear `_askBuiltIn = null;` wherever
`_askTyped` is set back to null, so a cancelled banner cannot leave the last choice behind.

- [ ] **Step 7: Build and run the full suite**

```
dotnet build Halo.sln -c Release
dotnet test tests\Halo.Tests\Halo.Tests.csproj
```

Expected: 0 warnings / 0 errors, all green. Report the count.

- [ ] **Step 8: Commit**

```bash
git add src/Halo.App/ClaudeCode/AskStore.cs src/Halo.App/Shell/NotchController.cs tests/Halo.Tests/AskQueueTests.cs
git commit -m "fix: each built-in ask row walks to the row it names

Write walked Options.Count steps for both, which lands on Type something.
Chat about this is one further, so clicking it answered the question instead
of leaving it."
```

---

### Task 4: A render hook so the banner can be eyeballed

**Files:**
- Modify: `src/Halo.App/Program.cs` (the argv hook block that already holds `--render-notif`)

**Interfaces:**
- Consumes: `AskBanner.Draw`, `AskBanner.Layout`, `PendingAsk`.
- Produces: a `--render-ask <png>` argv hook. Nothing consumes it in code; it exists because this bug
  arrived as a screenshot and the pill cannot be screenshotted.

- [ ] **Step 1: Find the existing hook and copy its shape**

```
grep -n "render-notif" src/Halo.App/Program.cs
```

Read the surrounding block. Match it exactly - same bitmap setup, same exit path, same
`try { } catch { }`.

- [ ] **Step 2: Add the hook**

Render a `PendingAsk` with four options into a bitmap sized by `AskBanner.Layout(...).Height`, and
save it. Use four options with descriptions of different lengths so the row-growth path is exercised
alongside the new rows:

```csharp
        if (args.Length >= 2 && args[0] == "--render-ask")
        {
            var ask = new Halo.ClaudeCode.PendingAsk("n", 0, "s", "AskUserQuestion", null,
                "which two of the queue should I do now?",
                new[]
                {
                    new Halo.Widgets.AskOption("frame rate + bars", "the smoothness pair"),
                    new Halo.Widgets.AskOption("bug report + pin icon", "the reporting pair"),
                    new Halo.Widgets.AskOption("pin icon + panel pass", ""),
                    new Halo.Widgets.AskOption("bug report + panel pass", ""),
                },
                DateTimeOffset.UtcNow.AddMinutes(10));
            int h = Halo.Widgets.AskBanner.Layout(ask, Halo.Widgets.AskBanner.W).Height;
            using var bmp = new System.Drawing.Bitmap(Halo.Widgets.AskBanner.W, h,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Halo.Widgets.AskBanner.Draw(g, Halo.Widgets.AskBanner.W, h, 1f, ask, -1, 60, null);
            }
            bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
```

If `AskBanner.Draw`'s signature differs from this call, use the real one - `NotchController.cs` line
~2180 shows how the app itself calls it.

- [ ] **Step 3: Render and look at it**

```
dotnet run --project src\Halo.App -- --render-ask ask.png
```

Open `ask.png`. Confirm: six rows, numbered 1-6; rows 5 and 6 read "Type something" and "Chat about
this" in that order; no text is clipped; the numbering matches the table in the Background section.

- [ ] **Step 4: Build clean and commit**

```bash
dotnet build Halo.sln -c Release
git add src/Halo.App/Program.cs
git commit -m "feat: --render-ask draws the banner with both built-in rows"
```

---

### Task 5: Verify against a live box, then write it down

The key map in `Write` was, in its own comment, "established by driving a live box and reading back
what came out, not from any documentation of the key map". Unit tests cannot check that a `Down`
lands where this plan claims. This task is the real verification and it must not be skipped.

**Files:**
- Modify: `PROGRESS.md`

- [ ] **Step 1: Deploy the build**

```
pwsh installer\build.ps1
dist\DynamicWinSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
%LOCALAPPDATA%\Programs\Halo\Halo.App.exe
```

Relaunch from an unsandboxed shell - a sandboxed one runs on an isolated desktop and the pill will be
invisible to the real session. If ISCC fails with `EndUpdateResource failed (110)` that is antivirus
locking the output; retry up to ~6 times.

- [ ] **Step 2: Drive a real question in every combination Task 0 found**

For each row of the Findings table - both built-ins, free-text only, chat only, neither - ask a real
question of that shape in a terminal Halo can see, and check two things: the banner draws exactly the
rows the box has, and clicking each one activates the row it names.

Where the row takes words, type a word and press Enter, then confirm the outcome:
- "Type something" must be recorded as an answer to the question.
- "Chat about this" must abandon the question and leave the words in the prompt.

The "neither" case is a real case: the banner must show the options alone, with nothing appended.

This is the step the whole plan exists for. A green test suite proves the arithmetic; only this proves
the arithmetic was about the right box.

- [ ] **Step 3: If a walk lands on the wrong row, fix the constant, not the test**

`%LOCALAPPDATA%\Halo\` holds the ask trace written by `Trace` - it records which row was targeted and
whether the keystroke was sent. Read it before changing anything. Adjust `WalkSteps` and the
`[InlineData]` rows in Task 3 together, and record in the comment what the live box actually did.

- [ ] **Step 4: Append to PROGRESS.md**

Add a dated entry at the top, under the existing 2026-08-02 heading style. State: the root cause (one
appended row labelled as the second of the box's two, whose walk reached the first), the change, how
it was verified - including the live-box result from Step 2 and the `--render-ask` PNG - the test
count, and **deployed vs. pushed**, which diverge constantly in this repo.

- [ ] **Step 5: Commit**

```bash
git add PROGRESS.md
git commit -m "docs: record the ask banner fix and how it was verified live"
```

---

## Findings from Task 0

Task 0 fills this in before any code is written. Until it is filled in, Tasks 1 and 3 cannot be
implemented, because both read the rule from here.

**Which rows the box shows.** One line per box driven. Record enough that the rule can be stated in
terms of `PendingAsk` fields alone.

| Options | Descriptions? | Multi-select? | Tool | "Type something"? | "Chat about this"? | Their row numbers |
|---|---|---|---|---|---|---|
| | | | | | | |

**The rule, stated in terms of `PendingAsk`:** _(written by Task 0)_

**Down at the bottom: clamps or wraps?** _(written by Task 0)_ - if it clamps, over-walking is a
deterministic way to reach the last row and Task 3 can use it; if it wraps, every count must be exact.

**Which of Task 0 Step 4's three outcomes applies:** _(written by Task 0)_

**`AskBuiltInCases.All`** - the test fixture Task 1 consumes. Task 0 writes it as a small internal
static class in `tests/Halo.Tests/`, one entry per row of the table above, so the layout tests and the
live verification in Task 5 are driven by the same list.

---

## What is deliberately not in this plan

- The other five units in the spec (motion, the panel truth pass, bug reports, the pin icon, the Linux
  seams). Each gets its own plan.
- Making the walk distances robust against Claude Code changing its box again. That has now happened
  twice, so it is tempting - but a general fix means reading the box's rows back, and `ConsoleRead`
  can only type into a terminal, not parse one. Worth raising as its own piece of work; not worth
  guessing at here.
