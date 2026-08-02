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
  `AskStore.RowNumber(int optionCount, AskDelivery delivery)` as `internal static int` - the pure
  part, which is what gets tested.

This is the task that fixes the reported defect. `Write` currently walks `ask.Options.Count` steps,
which reaches the first row past the options. That row is "Type something". "Chat about this" is one
further.

- [ ] **Step 1: Write the failing test**

Add to `tests/Halo.Tests/AskQueueTests.cs`:

```csharp
    // Every row past the options carries a number and is fired by typing it: the free-text row sits in
    // the list at N+1, and the chat row prints its own "N+2." below the list. Four options therefore put
    // them at 5 and 6, which is what the reported screenshot showed.
    [Theory]
    [InlineData(4, AskDelivery.FreeText, 5)]
    [InlineData(4, AskDelivery.Chat, 6)]
    [InlineData(1, AskDelivery.FreeText, 2)]
    [InlineData(1, AskDelivery.Chat, 3)]
    public void Each_built_in_row_is_reached_by_its_own_number(int options, AskDelivery delivery, int number)
        => Assert.Equal(number, AskStore.RowNumber(options, delivery));

    [Fact]
    public void A_numbered_option_does_not_go_through_the_built_in_path()
        => Assert.Equal(0, AskStore.RowNumber(4, AskDelivery.Option));

    // One keystroke is one digit. Eight options push the chat row to 10, which cannot be typed - and
    // sending "1" would answer with the first option, so this must refuse rather than approximate.
    [Fact]
    public void Past_nine_rows_there_is_no_digit_to_send()
    {
        Assert.Equal(10, AskStore.RowNumber(8, AskDelivery.Chat));
        Assert.True(AskStore.RowNumber(8, AskDelivery.Chat) > 9);
    }
```

- [ ] **Step 2: Run it to verify it fails**

```
dotnet test tests\Halo.Tests\Halo.Tests.csproj --filter "FullyQualifiedName~AskQueueTests"
```

Expected: FAIL - `AskDelivery` and `AskStore.RowNumber` do not exist.

- [ ] **Step 3: Add the delivery kind and the pure row number**

In `src/Halo.App/ClaudeCode/AskStore.cs`, above `internal sealed class AskStore`:

```csharp
// Which of the three ways an answer leaves the pill. The two built-in rows differ only in how far down
// the box you walk before pressing Enter, but that one step is the difference between answering the
// question and abandoning it.
internal enum AskDelivery { Option, FreeText, Chat }
```

Inside the class, next to `Write`:

```csharp
    // Every row the box shows carries a number, and typing it selects that row outright - including the
    // two past the options: __other__ sits in the list at N+1, and the chat row prints its own "N+2." and
    // is fired by that digit (verified in Claude Code's bundle, see the plan's Findings).
    //
    // This replaces walking down the list with counted arrow presses. The walk had no safe failure mode:
    // the list wraps, so a count one too large did not land past the end, it came back around onto a real
    // option and answered the question with it. A digit is either right or it does nothing.
    internal static int RowNumber(int optionCount, AskDelivery delivery) => delivery switch
    {
        AskDelivery.FreeText => optionCount + 1,
        AskDelivery.Chat => optionCount + 2,
        _ => 0,
    };
```

Note the consequence for `Press`: the existing `index < 9` guard applies to these rows too, since a
two-digit row number cannot be sent as one keystroke. With the built-ins that ceiling is reached two
rows earlier than before - guard on `RowNumber(...) <= 9` and return false above it rather than sending
a digit that means something else.

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

`Write` stops walking and types the row's number instead. The `VkDown` loop goes entirely:

```csharp
    private static bool Write(PendingAsk ask, string text, AskDelivery delivery)
    {
        int pid = ask.Pid;
        int row = RowNumber(ask.Options.Count, delivery);
        // one keystroke is one digit, and sending the wrong one answers with a real option
        if (row is <= 0 or > 9 || string.IsNullOrWhiteSpace(text)) return false;
        if (!Interop.ConsoleRead.Type(pid, row.ToString())) return false;
```

The pooled tail - sleep, `Enter`, sleep, `Type(text)`, sleep, `Enter` - stays, and so do its comments
about the box needing a moment between steps. Two things change in them:

- The paragraph explaining the Down-walk is now wrong. Replace it with what the bundle showed: every
  row prints its own number and typing that number selects it, so there is nothing to walk. Keep the
  history - say that this used to count `Down` presses, and that the list wraps, so an off-by-one did
  not miss harmlessly but came back around and answered with a real option.
- The first `Enter` was there to activate a highlighted row before typing into it. Typing the digit
  already activates it, so verify in Task 5 whether that `Enter` is still needed for the free-text row
  and remove it if it is not. Do not remove it on assumption - the sleeps and key order in this method
  were established by driving a live box, and the comment says so.

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

- [ ] **Step 3: If a digit lands on the wrong row, fix the arithmetic, not the test**

`%LOCALAPPDATA%\Halo\` holds the ask trace written by `Trace` - it records which row was targeted and
whether the keystroke was sent. Read it before changing anything. Adjust `RowNumber` and the
`[InlineData]` rows in Task 3 together, and record in the comment what the live box actually did.

Also settle here whether the free-text row still needs the `Enter` before the text is typed, and
whether a question whose options carry `preview` really does render without numbered built-ins - that
is the one Findings claim taken from reading the bundle rather than from watching it happen.

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

Task 0 did not need live boxes. Claude Code ships as a Bun-compiled binary with its JavaScript source
readable inside it, so the rule was read off the implementation instead of inferred from behaviour -
which is better evidence than driving boxes could ever be, because it covers cases nobody thought to
try.

Source: `%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`, version 2.1.220, the
question component at byte offset ~255258000.

**The rule, from the source:**

```js
const vJf = JP.multiSelect ? "Type something" : "Type something.";
let aLI = { type:"input", value:"__other__", label:"Other", placeholder:vJf, ... };

let m3S = qZe && !JP.multiSelect
    ? [{ type:"text", value:"__chat__", label:"Chat about this" }]
    : [];

let h3S = [...oLI, aLI, ...lLI];   // options, then __other__, then optionally __chat__
```

- The free-text row (`__other__`, placeholder "Type something") is appended **unconditionally**.
- The chat row (`__chat__`, "Chat about this") is appended **only when `qZe && !multiSelect`**.
- Row numbers therefore run: options `1..n`, free-text `n+1`, chat `n+2`. Confirmed independently by
  `MMr = JP.options.length + 1 + 1` in the same component, whose matching digit key calls
  `onRespondToClaude` - the chat action.

**A second layout exists.** When `!multiSelect && options.some(o => o.preview) && !qZe`, a different
component renders: it has no free-text row at all, offering a **Notes** field instead ("press n to add
notes"), with a chat row of its own at the bottom. This is the source of the combinations that looked
inexplicable from the outside.

**The blocker this exposes, which was not in the original plan.** The rule depends on `multiSelect` and
on whether any option carries a `preview` - and `AskEnvelope` forwards neither. It carries only
`Options` as label/description pairs, plus `Tool`, `Target`, `Question`. So `Halo.App` cannot evaluate
the rule with what it is given today, no matter how the banner is written.

`Halo.Hooks` does receive both: the `PreToolUse` payload contains the tool input, and
`questions[].multiSelect` and `questions[].options[].preview` are in it. **So Task 3 gains a
prerequisite: extend `AskEnvelope` with `MultiSelect` and `HasPreview`, write them in `ToJson`, read
them in `FromJson`, and populate them where the envelope is built.** `FromJson` already ignores
unknown fields and defaults missing ones, which is exactly the compatibility this needs - an older
pill reading a newer envelope, and vice versa, since the hooks are deployed separately from the pill.

**`qZe` is Ink's internal accessibility (screen-reader) mode, default false.** Proof:
`function Ea(){return y3u.useContext(xmo)}` over a context named `InternalAccessibilityContext`
created with `createContext(!1)`. So in an ordinary terminal `qZe` is **false**, the `__chat__` branch
above never runs, and the chat row is not in the option list at all. It is rendered separately:

```js
S3S = !qZe && <>... <h>{MMr}. Chat about this</h> ...</>
```

- The chat row is a block **below** the list, printing its own number `MMr = options.length + 2`.
- It is gated on `!qZe` only - **not** on `multiSelect`.
- The list gets `onDownFromLastItem: s3S`, and `s3S = () => Q4S(true)` moves focus onto that block.

So the normal box, which is what everyone actually sees, is: options `1..n`, `__other__` free-text at
`n+1` inside the list, chat at `n+2` below it. That matches the screenshot exactly.

**Down wraps - and that makes the current mechanism dangerous.** A generic `focus-next-option` reducer
in the same bundle falls back to `optionMap.first` when the focused row has no `.next`. The question
list overrides that with `onDownFromLastItem`, but the finding still matters: walking by counting Down
presses has no safe failure mode. A count that is one too many does not land harmlessly past the end -
it wraps onto a real option and answers the question with it.

**The mechanism should not be a walk at all.** The same key handler shows the box accepts the row's
digit directly:

```js
if(!uwt){ if(!qZe && !BPn && _q(OWe.key)===String(MMr)){ OWe.preventDefault(); $6t(); return } }
```

Typing `options.length + 2` fires `onRespondToClaude` - the chat action - with no navigation at all.
`AskStore.Press` already types digits for numbered options; the built-in rows are simply two more
digits. `BPn` is "the free-text field has focus", so the digit path is live right up until the user is
actually typing into that field, which is exactly the right condition.

**Revised rule for what to draw.** Both built-in rows are present in a normal terminal, always -
`__other__` unconditionally, chat whenever `!qZe`. The combinations that looked inexplicable come from
the *other* component: when `!multiSelect && options.some(o => o.preview)`, the box renders the preview
layout instead, which has no numbered free-text row and whose chat row is not numbered either
(`<h>Chat about this</h>`, no `MMr` prefix). So `HasPreview` is what the pill actually needs in order
to know whether the numbered built-ins exist. `MultiSelect` should be forwarded too: it swaps the list
widget and changes how `__other__` is submitted, so the pill should not pretend the two cases are the
same.

**`AskBuiltInCases.All`** - the test fixture Task 1 consumes. Write it as a small internal static class
in `tests/Halo.Tests/`, one entry per combination above, so the layout tests and the live verification
in Task 5 are driven by the same list.

---

## What is deliberately not in this plan

- The other five units in the spec (motion, the panel truth pass, bug reports, the pin icon, the Linux
  seams). Each gets its own plan.
- Making the walk distances robust against Claude Code changing its box again. That has now happened
  twice, so it is tempting - but a general fix means reading the box's rows back, and `ConsoleRead`
  can only type into a terminal, not parse one. Worth raising as its own piece of work; not worth
  guessing at here.
