using System;
using System.Collections.Generic;

namespace Halo.Agents;

// The pill's voice. Every line the agent widgets show under the icon used to be a single literal in
// ClaudeCodeWidget/CodexWidget, so the product said the same four things forever. This is the same
// wording widened into a set per situation, and the widgets pick from it.
//
// It is a written table, not a generated one. An earlier cut asked the local Claude Code / Codex CLI
// to write these at runtime: it worked, but it spent tokens on the user's own subscription and it had
// to launch their agent CLI to do it, which is a process-spawn and an account touch for the sake of
// some cosmetic copy. Neither is worth it for text nobody reads twice. Everything below ships in the
// binary: no network, no key, no subprocess, no per-machine setting, nothing to fail.
//
// Two constraints shaped the shape of it:
//   - Draw* runs per frame. Rerolling a line there would flicker at the frame rate, so Line() is
//     deliberately NOT random per call - it latches per key for 60s and holds. Cheap enough to sit on
//     the render path: a dictionary probe and a compare.
//   - 22 characters is the hard CEILING, not the target. The collapsed pill is ~220px wide with an
//     icon and a timer on it, and a longer line is clipped mid-word, which reads as a rendering bug
//     rather than a joke. MaxWidth is enforced by a test. In practice the table averages about a
//     dozen: a glanced line has to land in one look, and "a very long think…" says nothing that
//     "long think…" does not.
/// <summary>
/// What the pill knows about the moment it is describing, beyond which slot it is in. Every field is
/// something already on the panel - no new measurement, nothing invented. <c>default</c> means "know
/// nothing", so a caller that has only a clock (or nothing at all) still works: zero reads as unknown
/// for the fractions and the counts, and <see cref="Hour"/> is nullable because midnight is 0.
/// </summary>
internal readonly record struct MoodContext(
    TimeSpan? Running = null,
    float ContextFrac = 0f,
    float UsageFrac = 0f,
    long PromptTokens = 0,
    int ToolRuns = 0,
    int? Hour = null);

internal static class Moods
{
    internal const int MaxWidth = 22;

    // The situation is more than one dimension, and elapsed time was the only one the table could see.
    // A verb that has not changed in four minutes has stopped being information - but so has one that
    // says "reading…" while the context bar is at 91%, where what you want to know is that the desk is
    // full. So a slot can carry a set per situation, and the ladder below picks which one speaks.
    private static readonly TimeSpan LongAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AgesAfter = TimeSpan.FromMinutes(8);
    private const string LongSuffix = "@long";
    private const string AgesSuffix = "@ages";
    private const string TightSuffix = "@tight";
    private const string ThinSuffix = "@thin";
    private const string AgainSuffix = "@again";
    private const string HeavySuffix = "@heavy";
    private const string LateSuffix = "@late";
    private const string EarlySuffix = "@early";

    // Thresholds, shared with the tests so a change here cannot leave them asserting an old number.
    // TightAt is deliberately ClaudeCodeWidget.ContextWarnAt: the voice should turn at the same figure
    // the /compact banner fires on, or the pill would be wry about a session the panel calls fine.
    internal const float TightAt = 0.80f;
    internal const float ThinAt = 0.90f;
    internal const int AgainAfter = 4;        // distinct tool hand-offs inside one turn
    internal const long HeavyTokens = 60_000; // prompt tokens on the running turn

    // The first entry of each set is the line that shipped before this existed, so the product's own
    // voice stays in the rotation rather than being replaced wholesale.
    private static readonly Dictionary<string, string[]> Pool = new(StringComparer.Ordinal)
    {
        ["idle"] = new[]
        {
            "let's work :)", "standing by", "all yours", "nothing on", "clear desk",
            "say the word", "on standby", "queue's empty", "awaiting orders", "idle", "ready",
            "put me to work", "unoccupied", "primed", "twiddling thumbs", "at your service",
            "tools down", "bench is clear", "apron on", "gloves on",
        },
        ["offline"] = new[]
        {
            "offline :(", "no signal", "off the grid", "unplugged", "link down", "no connection",
            "no route out", "cut off", "adrift", "stranded", "disconnected", "net's gone",
        },
        ["apiDown"] = new[]
        {
            "api down :(", "api's out", "api unreachable", "no answer", "upstream silent",
            "api asleep", "api's away", "upstream dark", "no reply",
        },
        ["netError"] = new[]
        {
            "net error :(", "line broke", "dropped", "connection lost", "net gave out",
            "it hung up", "line went dead", "pipe broke", "signal cut",
        },
        ["apiError"] = new[]
        {
            "api error :(", "api said no", "bad reply", "refused", "error back", "api objects",
            "a firm no", "rejected",
        },
        ["compacted"] = new[]
        {
            "compacted :)", "room again", "made space", "trimmed", "breathing room",
            "lighter now", "roomier", "slimmer now", "fresh headroom", "bench wiped down",
            "tidy again",
        },
        ["outOfCredit"] = new[]
        {
            "outta juice XD", "tank's empty", "credit spent", "budget's gone", "out of runway",
            "spent up", "dry till reset", "all used up", "meter's at zero", "gauge on empty",
        },
        // The working verbs lean on ordinary hands-on work - a kitchen, a toolbox, a job half done on the
        // bench - because that is what the thing is actually doing and a metaphor carries information a
        // synonym does not: "kettle's on…" tells you to go away for a minute, "still running…" does not.
        ["writing"] = new[]
        {
            "writing…", "editing…", "typing…", "drafting…", "on the keys…", "writing code…",
            "composing…", "authoring…", "making changes…", "putting it down…", "shaping it…",
            "laying bricks…", "mixing cement…", "measuring twice…", "cutting to fit…",
        },
        ["reading"] = new[]
        {
            "reading…", "skimming…", "having a look…", "eyes on it…", "studying…", "parsing it…",
            "reading up…", "absorbing…", "scanning…", "poring over it…",
            "reading the manual…", "eyeing the wiring…",
        },
        ["running"] = new[]
        {
            "running…", "executing…", "in flight…", "crunching…", "under way…", "churning…",
            "processing…", "off it goes…", "shell's busy…", "in progress…",
            "on the hob…", "in the oven…", "cranking it…",
        },
        ["digging"] = new[]
        {
            "digging…", "rummaging…", "spelunking…", "sifting…", "prospecting…", "foraging…",
            "poking around…", "on the trail…", "combing code…", "raking through…",
            "torch and gloves…", "under the floor…", "behind the panel…", "hood's up…",
        },
        ["fetching"] = new[]
        {
            "fetching…", "downloading…", "grabbing it…", "retrieving…", "collecting…",
            "in transit…", "on the wire…", "reeling it in…", "pulling it down…",
            "van's on the way…", "waiting on parts…",
        },
        ["searching"] = new[]
        {
            "googling :P", "searching…", "looking it up…", "trawling…", "web hunting…",
            "asking the web…", "browsing…", "querying…",
            "asking the forum…", "thumbing the index…",
        },
        ["delegating"] = new[]
        {
            "delegating…", "handing off…", "passing it on…", "calling backup…", "deputising…",
            "sending help…", "farming it out…", "sharing the load…",
            "calling a plumber…", "an apprentice goes…",
        },
        ["planning"] = new[]
        {
            "planning…", "sketching…", "outlining…", "mapping it…", "scoping it…",
            "drawing it up…", "lining it up…", "thinking ahead…",
            "envelope maths…", "chalk on the wall…", "tape measure out…",
        },
        ["skill"] = new[]
        {
            "using a skill…", "loading a skill…", "by the book…", "on the playbook…",
            "the recipe…", "following steps…", "mise en place…", "the manual says…",
        },
        ["asking"] = new[]
        {
            "asking you :)", "your turn", "your move", "over to you", "needs a call",
            "a question", "wants a word", "your say-so", "needs a hand", "hold this?",
        },
        ["unknown"] = new[]
        {
            "hmm…", "thinking…", "considering…", "mulling it…", "chewing on it…",
            "figuring it out…", "reasoning…", "weighing it up…", "deliberating…", "sizing it up…",
            "having a think…", "turning it over…",
            "measuring up…", "eyeing it up…", "head-scratching…",
        },
        ["compacting"] = new[]
        {
            "compacting…", "condensing…", "packing up…", "making room…", "trimming…",
            "boiling it down…", "squeezing…", "clearing the bench…", "reducing it…",
        },
        ["patching"] = new[]
        {
            "patching…", "applying a fix…", "mending…", "amending…", "stitching…",
            "splicing it in…", "touching it up…",
            "duct tape…", "wd-40 moment…", "bit of filler…", "sealing the leak…",
        },
        ["plotting"] = new[]
        {
            "plotting…", "replanning…", "revising…", "reordering…", "re-scoping…",
            "shuffling tasks…", "redrawing it…", "new blueprint…",
        },

        // ---- the same situations once they have been going a couple of minutes ----
        ["writing" + LongSuffix] = new[]
        {
            "still writing…", "long file…", "still typing…", "quite the essay…", "chapter two…",
            "still laying bricks…",
        },
        ["reading" + LongSuffix] = new[]
        {
            "still reading…", "long read…", "deep in it…", "still going…", "engrossed…",
        },
        ["running" + LongSuffix] = new[]
        {
            "still running…", "long job…", "still churning…", "taking its time…", "any minute…",
            "still on the hob…",
        },
        ["digging" + LongSuffix] = new[]
        {
            "still digging…", "big haystack…", "deep in it…", "still hunting…", "big tree…",
            "still under there…",
        },
        ["fetching" + LongSuffix] = new[]
        {
            "still fetching…", "slow pipe…", "trickling in…", "still coming…", "byte by byte…",
        },
        ["searching" + LongSuffix] = new[]
        {
            "still searching…", "still looking…", "page four…", "web is coy…", "hard to find…",
        },
        ["delegating" + LongSuffix] = new[]
        {
            "helper's busy…", "still out…", "no word back…", "sub is thinking…",
        },
        ["planning" + LongSuffix] = new[]
        {
            "still planning…", "big plan…", "still sketching…", "many boxes…",
        },
        ["skill" + LongSuffix] = new[]
        {
            "long recipe…", "still on it…", "many steps…",
        },
        ["unknown" + LongSuffix] = new[]
        {
            "still thinking…", "deep thought…", "long think…", "cogitating…", "one minute…",
        },
        ["compacting" + LongSuffix] = new[]
        {
            "still compacting…", "lots to fold…", "big history…", "still squeezing…",
        },
        ["patching" + LongSuffix] = new[]
        {
            "still patching…", "fiddly patch…", "still stitching…", "careful now…", "more filler…",
        },
        ["plotting" + LongSuffix] = new[]
        {
            "still plotting…", "big list…", "still shuffling…", "many tasks…",
        },

        // ---- and once it is long enough that you start wondering if it is stuck ----
        ["writing" + AgesSuffix] = new[]
        {
            "war and peace…", "some novel…", "hope it's good…", "a whole wall of it…",
        },
        ["reading" + AgesSuffix] = new[]
        {
            "a long book…", "every last line…",
        },
        ["running" + AgesSuffix] = new[]
        {
            "long haul…", "make a coffee…", "settle in…", "kettle's on…", "low and slow…",
            "still simmering…",
        },
        ["digging" + AgesSuffix] = new[]
        {
            "needle, meet hay…", "deep down there…", "floorboards are up…",
        },
        ["fetching" + AgesSuffix] = new[]
        {
            "dial-up speeds…", "glacial…",
        },
        ["searching" + AgesSuffix] = new[]
        {
            "web's hiding it…", "page ten…",
        },
        ["delegating" + AgesSuffix] = new[]
        {
            "still out there…", "no word yet…",
        },
        ["planning" + AgesSuffix] = new[]
        {
            "grand strategy…", "epic scope…",
        },
        ["skill" + AgesSuffix] = new[]
        {
            "a long recipe…", "still in it…",
        },
        ["unknown" + AgesSuffix] = new[]
        {
            "deep in thought…", "still cooking…", "hard problem…",
        },
        ["compacting" + AgesSuffix] = new[]
        {
            "huge history…", "still packing…",
        },
        ["patching" + AgesSuffix] = new[]
        {
            "stubborn patch…", "still fiddling…", "more tape…",
        },
        ["plotting" + AgesSuffix] = new[]
        {
            "epic list…", "grand agenda…",
        },

        // ---- the context window is nearly full: the bench is covered and there is nowhere to put
        // anything down. Outranks the duration bands, because at 90% that is the more useful thing to
        // know about a turn than the fact it has been going four minutes.
        ["idle" + TightSuffix] = new[]
        {
            "worth a /compact", "desk needs clearing", "no room left",
        },
        ["unknown" + TightSuffix] = new[]
        {
            "no room to think…", "desk is buried…", "bench is covered…",
        },
        ["running" + TightSuffix] = new[]
        {
            "no room to work…", "elbows in…",
        },
        ["writing" + TightSuffix] = new[]
        {
            "margins are gone…", "writing in the gaps…",
        },
        ["reading" + TightSuffix] = new[]
        {
            "no shelf left…", "nowhere to file it…",
        },
        ["digging" + TightSuffix] = new[]
        {
            "nowhere to put it…", "bench is full…",
        },

        // ---- the usage window is nearly spent. Above 95% the widgets switch to outOfCredit, which is a
        // slot of its own; this is the stretch where it still works but you should know it is rationing.
        ["idle" + ThinSuffix] = new[]
        {
            "nearly out", "last drops", "running low",
        },
        ["unknown" + ThinSuffix] = new[]
        {
            "rationing it…", "last of the tank…",
        },
        ["running" + ThinSuffix] = new[]
        {
            "coasting…", "on fumes…",
        },
        ["writing" + ThinSuffix] = new[]
        {
            "short strokes…", "sparing the ink…",
        },

        // ---- it keeps reaching for tools inside one turn. Counted, never guessed - and never printed as
        // a number, because the count the pill can see is tool hand-offs, not attempts at the same thing.
        ["unknown" + AgainSuffix] = new[]
        {
            "same drill…", "on repeat…",
        },
        ["running" + AgainSuffix] = new[]
        {
            "again…", "one more pass…", "round after round…",
        },
        ["digging" + AgainSuffix] = new[]
        {
            "another cupboard…", "next drawer…",
        },
        ["writing" + AgainSuffix] = new[]
        {
            "another draft…", "again, with feeling…",
        },
        ["patching" + AgainSuffix] = new[]
        {
            "another go at it…", "third coat…",
        },

        // ---- a big prompt on the running turn: the turn itself is heavy, whatever it is doing.
        ["unknown" + HeavySuffix] = new[]
        {
            "hands full…", "a big order…",
        },
        ["running" + HeavySuffix] = new[]
        {
            "heavy load…", "big job on…",
        },
        ["reading" + HeavySuffix] = new[]
        {
            "a lot on the bench…", "the whole file…",
        },
        ["writing" + HeavySuffix] = new[]
        {
            "a long shift…", "big pour…",
        },

        // ---- the wall clock, last on the ladder: it only speaks when nothing more pressing does, which
        // is exactly the idle pill at two in the morning.
        ["idle" + LateSuffix] = new[]
        {
            "still up?", "night shift", "burning oil", "quiet hours",
        },
        ["unknown" + LateSuffix] = new[]
        {
            "small hours…", "night thoughts…",
        },
        ["running" + LateSuffix] = new[]
        {
            "the night shift…", "graveyard shift…",
        },
        ["writing" + LateSuffix] = new[]
        {
            "by lamplight…", "one more then bed…",
        },
        ["idle" + EarlySuffix] = new[]
        {
            "kettle first", "morning", "coffee then work",
        },
        ["unknown" + EarlySuffix] = new[]
        {
            "waking up…", "still yawning…",
        },
        ["running" + EarlySuffix] = new[]
        {
            "early start…", "beating the rush…",
        },
    };

    /// <summary>Every key in the table, for the tests and the dump hook.</summary>
    internal static IEnumerable<string> Keys => Pool.Keys;

    internal static string[] Set(string key) => Pool.TryGetValue(key, out var v) ? v : Array.Empty<string>();

    private static readonly Random Rng = new();
    private static readonly object Gate = new();

    // Latching lives here rather than at the call sites because a single frame can ask for more than
    // one key (OutageText and ToolVerb both run, and only one is displayed); a caller-side latch would
    // flip between them every frame and strobe the text. Keyed, so the order callers ask in cannot
    // matter, and the bands latch independently - crossing a boundary swaps the wording once instead of
    // fighting the hold.
    //
    // The expiry is measured from when the line was ROLLED, and reported live as the reason the pill only
    // ever said one thing: the read path used to stamp `at` again on every hit, which is a sliding window,
    // and Draw* hits it 125 times a second. Any key the pill kept looking at could therefore never expire,
    // so a long thinking block sat on "still cooking…" indefinitely and the whole table was decorative.
    private static readonly Dictionary<string, (string line, DateTime at)> Held = new(StringComparer.Ordinal);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(60);

    /// <summary>The line shipped before this table existed. Never throws.</summary>
    internal static string Fixed(string slot)
    {
        var i = slot.IndexOf('@');
        if (i > 0) slot = slot.Substring(0, i);
        var set = Set(slot);
        return set.Length > 0 ? set[0] : "hmm…";
    }

    /// <summary>
    /// The line to show for a slot. Stable while a state persists, different next time you land on it.
    /// Safe to call per frame and from anywhere on the render path.
    /// </summary>
    internal static string Line(string slot) => Line(slot, null);

    /// <summary>
    /// As <see cref="Line(string)"/>, but told how long the thing has been going, so a slot with a
    /// long-running set can switch to it. A null duration just means "no clock for this one".
    /// </summary>
    internal static string Line(string slot, TimeSpan? running) => Line(slot, new MoodContext(running));

    // Which situations can modify a slot, most pressing first. Order IS the design: only one suffix
    // ever speaks, because stacking them explodes combinatorially and most pairs read badly anyway
    // ("no room to think, and again, at 3am"). Urgency descends - what is wrong with the session, then
    // how long this has taken, then what the turn is like, then merely what time it is.
    private static readonly (string suffix, Func<MoodContext, bool> when)[] Ladder =
    {
        (TightSuffix, c => c.ContextFrac >= TightAt),
        (ThinSuffix, c => c.UsageFrac >= ThinAt),
        (AgesSuffix, c => c.Running >= AgesAfter),
        (LongSuffix, c => c.Running >= LongAfter),
        (AgainSuffix, c => c.ToolRuns >= AgainAfter),
        (HeavySuffix, c => c.PromptTokens >= HeavyTokens),
        (LateSuffix, c => c.Hour is >= 0 and <= 4),
        (EarlySuffix, c => c.Hour is >= 5 and <= 7),
    };

    /// <summary>
    /// The one suffix this moment earns, or "" for none. Pure and table-blind - <see cref="Line"/> walks
    /// past a suffix the slot has no set for, so this answers "what is most true right now", not "what
    /// will be shown".
    /// </summary>
    internal static string Modifier(in MoodContext ctx)
    {
        foreach (var (suffix, when) in Ladder) if (when(ctx)) return suffix;
        return "";
    }

    /// <summary>
    /// As <see cref="Line(string)"/>, told everything the widget knows about the moment. Falls down the
    /// ladder until it finds a set the slot actually has, so a slot needs only the situations worth
    /// wording and everything else keeps the plain line rather than going quiet.
    /// </summary>
    internal static string Line(string slot, in MoodContext ctx) => Line(slot, ctx, DateTime.UtcNow);

    /// <summary>
    /// As above, on an injected clock. The hold is a minute, so this is the only way a test can watch a
    /// line expire - and expiry is the half of this that broke in the field.
    /// </summary>
    internal static string Line(string slot, in MoodContext ctx, DateTime now)
    {
        var key = slot;
        foreach (var (suffix, when) in Ladder)
        {
            if (!when(ctx)) continue;
            var candidate = slot + suffix;
            if (!Pool.ContainsKey(candidate)) continue;   // nothing written for it: try the next one down
            key = candidate;
            break;
        }
        string? stale = null;
        lock (Gate)
        {
            if (Held.TryGetValue(key, out var h))
            {
                if (now - h.at < Hold) return h.line;
                stale = h.line;   // expired: reroll, but away from the line that was just up
            }
        }
        var picked = Pick(key, stale);
        lock (Gate) Held[key] = (picked, now);
        return picked;
    }

    /// <summary>
    /// Roll a fresh line for a key, ignoring whatever is currently held. <paramref name="avoid"/> is the
    /// line that just expired: a reroll landing on it again looks exactly like the frozen pill this whole
    /// mechanism exists to prevent, and with a two-line set that is a coin flip every minute.
    /// </summary>
    internal static string Pick(string key, string? avoid = null)
    {
        var set = Set(key);
        if (set.Length == 0) return Fixed(key);
        lock (Gate)
        {
            int i = Rng.Next(set.Length);
            // stepping to the neighbour rather than rerolling: bounded, and the bias it introduces is
            // invisible against a line that changes once a minute
            if (avoid is not null && set.Length > 1 && set[i] == avoid) i = (i + 1) % set.Length;
            return set[i];
        }
    }
}
