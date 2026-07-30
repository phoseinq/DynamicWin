using Halo.Agents;
using System.Text.Json.Nodes;
using Xunit;

namespace Halo.Tests;

// "running…" cannot tell a three-second `git status` from a two-minute `dotnet build`, and that was the one
// thing the pill had no way to say about a tool call. The hook now forwards what the tool is acting ON, and
// the wording puts it in front of the voice when there is nothing more pressing to report.
public class ToolTargetTests
{
    private static string? Target(string tool, string json)
        => Halo.Hooks.Program.ToolTarget(tool, Halo.Hooks.Program.AsObject(JsonNode.Parse(json)));

    // measured live: the field was written but always null, because tool_input arrives as a JSON STRING
    // from some surfaces and `as JsonObject` on that is null
    [Fact]
    public void AStringifiedPayloadIsReadTheSameAsAnObject()
    {
        var stringified = JsonValue.Create("""{"file_path":"C:\\repo\\Fx.cs"}""");
        Assert.Equal("Fx.cs", Halo.Hooks.Program.ToolTarget("Edit",
            Halo.Hooks.Program.AsObject(stringified)));
        Assert.Null(Halo.Hooks.Program.AsObject(JsonValue.Create("not json")));
        Assert.Null(Halo.Hooks.Program.AsObject(null));
    }

    [Fact]
    public void AFileToolNamesTheFileAndNotThePath()
    {
        Assert.Equal("Fx.cs", Target("Edit", """{"file_path":"C:\\repo\\src\\Halo.App\\Widgets\\Fx.cs"}"""));
        Assert.Equal("Moods.cs", Target("Read", """{"file_path":"/home/x/Moods.cs"}"""));
    }

    // the PROGRAM, not the command line: the verb is the news and the flags are noise the pill has no room
    // for. Paths, quoting and env prefixes all have to fall away for that to be true.
    [Theory]
    [InlineData("git status", "git")]
    [InlineData("dotnet build Halo.sln -c Release", "dotnet")]
    [InlineData("\"C:\\Program Files\\nodejs\\npm.cmd\" install", "npm.cmd")]
    [InlineData("/usr/bin/python3 -m pytest", "python3")]
    [InlineData("HALO_X=1 pwsh -File build.ps1", "pwsh")]
    [InlineData("cargo.exe test", "cargo")]
    public void AShellCommandNamesItsProgram(string command, string expected)
        => Assert.Equal(expected, Target("Bash", $$"""{"command":{{JsonValue.Create(command)!.ToJsonString()}}}"""));

    // a chain is more than one program, so it honestly names none of them - the voice is a better answer
    // than picking the first of four
    [Theory]
    [InlineData("git add -A && git commit -m x")]
    [InlineData("cat file | grep foo")]
    [InlineData("build; test")]
    public void AChainedCommandNamesNothing(string command)
        => Assert.Null(Target("Bash", $$"""{"command":{{JsonValue.Create(command)!.ToJsonString()}}}"""));

    [Fact]
    public void AFetchNamesTheHostAndASearchItsQuery()
    {
        Assert.Equal("learn.microsoft.com",
            Target("WebFetch", """{"url":"https://learn.microsoft.com/en-us/windows/apps/x"}"""));
        Assert.Equal("layered window", Target("WebSearch", """{"query":"layered window"}"""));
        Assert.Null(Target("WebFetch", """{"url":"not a url"}"""));
    }

    // a payload that is not the shape we expected is a null, never a guess: the pill has a voice to fall
    // back on and an invented line is the one outcome that cannot be allowed
    [Theory]
    [InlineData("Edit", """{"file_path":42}""")]
    [InlineData("Edit", """{}""")]
    [InlineData("Bash", """{"command":"   "}""")]
    [InlineData("SomeToolNobodyMapped", """{"file_path":"a.cs"}""")]
    public void AnUnexpectedPayloadNamesNothing(string tool, string json)
        => Assert.Null(Target(tool, json));

    [Fact]
    public void TheFactBeatsTheVoiceWhenThereIsNothingMorePressingToSay()
    {
        var calm = new MoodContext(Hour: 14, Target: "Fx.cs");
        Assert.Equal("writing Fx.cs…", Moods.Line("writing", calm));
        Assert.Equal("running git…", Moods.Line("running", new MoodContext(Hour: 14, Target: "git")));
    }

    // ...and the situation beats the fact, because a session about to run out of room is bigger news than
    // which file is open
    [Fact]
    public void ASituationBeatsTheFact()
    {
        var tight = new MoodContext(ContextFrac: 0.97f, Hour: 14, Target: "Fx.cs");
        var line = Moods.Line("writing", tight);
        Assert.DoesNotContain("Fx.cs", line);
        Assert.Contains(line, Moods.Set("writing" + "@tight"));
    }

    [Fact]
    public void AFactTooLongForThePillGivesWayToTheVoice()
    {
        var ctx = new MoodContext(Hour: 14, Target: "SomeVeryLongFileNameIndeed.cs");
        var line = Moods.Line("writing", ctx);
        Assert.True(line.Length <= Moods.MaxWidth);
        Assert.Contains(line, Moods.Set("writing"));
    }

    // thinking is not doing anything TO something, so it has no verb and never takes a fact
    [Fact]
    public void ASlotWithNoVerbNeverTakesAFact()
    {
        Assert.Null(Moods.Fact("unknown", "Fx.cs"));
        Assert.Null(Moods.Fact("writing", null));
        Assert.Equal("brainstorming…", Moods.Fact("skill", "brainstorming"));
    }
}

// The pill's gap is narrow and it narrows further when the elapsed clock grows a digit. The words are now
// chosen against the room they will actually get, because the alternative was what shipped: a nineteen
// character line in twelve characters of space, and a renderer shrinking the font to 9px to make that true.
public class FittingTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(14)]
    [InlineData(22)]
    public void EverySlotCanSpeakWithinABudget(int budget)
    {
        foreach (var slot in new[] { "unknown", "running", "reading", "writing", "digging", "delegating",
                                     "compacting", "idle", "asking", "consulting", "watching" })
        {
            var line = Moods.Pick(slot, avoid: null, maxChars: budget);
            // the shortest line in a set is the floor: if even that does not fit, it is still what gets
            // drawn, because a too-long true line beats a made-up short one
            int floor = int.MaxValue;
            foreach (var s in Moods.Set(slot)) floor = System.Math.Min(floor, s.Length);
            Assert.True(line.Length <= System.Math.Max(budget, floor),
                $"{slot} answered '{line}' ({line.Length}) for a budget of {budget}");
        }
    }

    // a fact is a fact, but it still has to be readable: too long for the room means the voice answers
    [Fact]
    public void ATightBudgetTurnsAFactBackIntoTheVoice()
    {
        Assert.Null(Moods.Fact("writing", "SomethingLong.cs", maxChars: 10));
        Assert.Equal("writing Fx.cs…", Moods.Fact("writing", "Fx.cs", maxChars: 14));
    }

    // and the held line is re-rolled when its room shrinks, rather than being drawn too small
    [Fact]
    public void AHeldLineGivesWayWhenTheRoomShrinks()
    {
        var now = System.DateTime.UtcNow;
        var roomy = Moods.Line("digging", new MoodContext(Hour: 14, MaxChars: 22), now);
        var tight = Moods.Line("digging", new MoodContext(Hour: 14, MaxChars: 9), now);
        Assert.True(tight.Length <= 9, $"'{tight}' is too long for the space it was given");
        if (roomy.Length > 9) Assert.NotEqual(roomy, tight);
    }
}
