using System;
using System.Collections.Generic;

namespace Halo.Agents;

internal readonly record struct MoodContext(
    TimeSpan? Running = null,
    float ContextFrac = 0f,
    float UsageFrac = 0f,
    long PromptTokens = 0,
    int ToolRuns = 0,
    int? Hour = null,

    string? Target = null,

    int MaxChars = 0);

internal static class Moods
{
    internal const int MaxWidth = 22;

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

    internal const float TightAt = 0.80f;
    internal const float ThinAt = 0.90f;
    internal const int AgainAfter = 4;
    internal const long HeavyTokens = 60_000;

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
            "reading the manual…", "eyeing the wiring…", "analyzing…",
        },
        ["running"] = new[]
        {
            "running…", "executing…", "in flight…", "crunching…", "under way…", "churning…",
            "processing…", "off it goes…", "shell's busy…", "in progress…",
            "on the hob…", "in the oven…", "cranking it…", "working…",
        },
        ["digging"] = new[]
        {
            "digging…", "rummaging…", "spelunking…", "sifting…", "prospecting…", "foraging…",
            "indexing…",
            "poking around…", "on the trail…", "combing code…", "raking through…",
            "torch and gloves…", "under the floor…", "behind the panel…", "hood's up…",
            "hmm, where…", "it's in here…",
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
            "hmm, ok…", "erm…", "uhh…", "let's see…", "right then…", "so…",

            "reflecting…", "synthesizing…", "undulating…",
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

        ["watching"] = new[]
        {
            "watching…", "keeping an eye…", "on the dial…", "waiting on it…",
            "watching the pot…", "tailing it…",
        },
        ["reviewing"] = new[]
        {
            "reviewing…", "checking the work…", "inspecting…", "snagging…", "second look…",
            "going over it…", "analyzing…",
        },
        ["publishing"] = new[]
        {
            "publishing…", "shipping it…", "out the door…", "posting it…", "handing it over…",
        },
        ["consulting"] = new[]
        {
            "consulting…", "asking a tool…", "asking next door…", "phoning a friend…",
            "calling the desk…", "connecting…",
        },
        ["peeking"] = new[]
        {
            "peeking o.o", "having a peek…", "eyes on the shot…", "taking a look…",
        },

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
            "hmmm…", "hmm, tricky…", "erm, hang on…",
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
            "hmmmm…", "well, hmm…", "still erm-ing…",
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

        ["idle" + TightSuffix] = new[]
        {
            "worth a /compact", "desk needs clearing", "no room left",
        },
        ["unknown" + TightSuffix] = new[]
        {
            "no room to think…", "desk is buried…", "bench is covered…", "hmm, no room…",
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

        ["unknown" + AgainSuffix] = new[]
        {
            "same drill…", "on repeat…", "hmm, again…",
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

        internal static IEnumerable<string> Keys => Pool.Keys;

    internal static string[] Set(string key) => Pool.TryGetValue(key, out var v) ? v : Array.Empty<string>();

    private static readonly Random Rng = new();
    private static readonly object Gate = new();

    private static readonly Dictionary<string, (string line, DateTime at)> Held = new(StringComparer.Ordinal);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(60);

        internal static string Fixed(string slot)
    {
        var i = slot.IndexOf('@');
        if (i > 0) slot = slot.Substring(0, i);
        var set = Set(slot);
        return set.Length > 0 ? set[0] : "hmm…";
    }

        internal static string Line(string slot) => Line(slot, null);

        internal static string Line(string slot, TimeSpan? running) => Line(slot, new MoodContext(running));

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

        internal static string Modifier(in MoodContext ctx)
    {
        foreach (var (suffix, when) in Ladder) if (when(ctx)) return suffix;
        return "";
    }

        internal static string Line(string slot, in MoodContext ctx) => Line(slot, ctx, DateTime.UtcNow);

        internal static string Line(string slot, in MoodContext ctx, DateTime now)
    {
        var key = slot;
        foreach (var (suffix, when) in Ladder)
        {
            if (!when(ctx)) continue;
            var candidate = slot + suffix;
            if (!Pool.ContainsKey(candidate)) continue;
            key = candidate;
            break;
        }

        if (key == slot && Fact(slot, ctx.Target, ctx.MaxChars) is { } f) return f;
        string? stale = null;
        lock (Gate)
        {
            if (Held.TryGetValue(key, out var h))
            {

                if (now - h.at < Hold && (ctx.MaxChars <= 0 || h.line.Length <= ctx.MaxChars))
                    return h.line;
                stale = h.line;
            }
        }
        var picked = Pick(key, stale, ctx.MaxChars);
        lock (Gate) Held[key] = (picked, now);
        return picked;
    }

        internal static string? Fact(string? slot, string? target, int maxChars = 0)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var verb = slot switch
        {
            "writing" => "writing ",
            "patching" => "patching ",
            "reading" => "reading ",
            "peeking" => "peeking at ",
            "running" => "running ",
            "digging" => "digging ",
            "fetching" => "fetching ",
            "searching" => "searching ",
            "delegating" or "consulting" => "asking ",
            "skill" => "",
            _ => null,
        };
        if (verb is null) return null;
        var line = verb + target.Trim() + "…";
        int ceiling = maxChars > 0 ? Math.Min(maxChars, MaxWidth) : MaxWidth;
        return line.Length <= ceiling ? line : null;
    }

    internal static string PrettyTool(string? tool)
    {
        var t = (tool ?? "").Trim();
        if (t.Length == 0) return Fixed("unknown");
        if (t.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = t.Split("__", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) t = parts[1];
        }
        t = t.Replace('_', ' ').Replace('-', ' ').Trim().ToLowerInvariant();
        if (t.Length == 0) return Fixed("unknown");
        if (t.Length > MaxWidth - 1) t = t.Substring(0, MaxWidth - 1).TrimEnd();
        return t + "…";
    }

        internal static string Pick(string key, string? avoid = null, int maxChars = 0)
    {
        var set = Set(key);
        if (set.Length == 0) return Fixed(key);

        if (maxChars > 0)
        {
            var fits = Array.FindAll(set, s => s.Length <= maxChars);
            if (fits.Length == 0)
            {
                var shortest = set[0];
                foreach (var s in set) if (s.Length < shortest.Length) shortest = s;
                return shortest;
            }
            set = fits;
        }
        lock (Gate)
        {
            int i = Rng.Next(set.Length);

            if (avoid is not null && set.Length > 1 && set[i] == avoid) i = (i + 1) % set.Length;
            return set[i];
        }
    }
}
