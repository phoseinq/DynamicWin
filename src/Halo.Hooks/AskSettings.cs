using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

// The `permissions.allow` rules, read by the hook itself.
//
// PreToolUse cannot ask Claude "would you have prompted for this?" - it runs before that decision. So the
// gate has to know what is already allowed, or it would raise a banner for every `git status` on the
// allowlist and make the user answer MORE things than before.
//
// Cached by mtime rather than per call: this process spawns once per tool call, but a long session spawns
// it thousands of times, and re-reading three JSON files each time to answer a question whose answer
// almost never changes is exactly the kind of cost that does not show up in one measurement.
internal static class AskSettings
{
    private static readonly Dictionary<string, (DateTime Stamp, string[] Rules)> Cache = new();

    internal static IReadOnlyList<string> AllowRules(string? cwd)
    {
        var rules = new List<string>();
        foreach (var path in Sources(cwd))
            rules.AddRange(RulesFrom(path));
        return rules;
    }

    private static IEnumerable<string> Sources(string? cwd)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".claude", "settings.json");
        if (string.IsNullOrEmpty(cwd)) yield break;
        yield return Path.Combine(cwd, ".claude", "settings.json");
        yield return Path.Combine(cwd, ".claude", "settings.local.json");
    }

    private static string[] RulesFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (Cache)
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp)
                    return hit.Rules;

            var parsed = Parse(File.ReadAllText(path));
            lock (Cache) Cache[path] = (stamp, parsed);
            return parsed;
        }
        catch { return []; }   // unreadable settings must not turn into "nothing is allowed"
    }

    private static string[] Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return [];
            if (o["permissions"]?["allow"] is not JsonArray allow) return [];
            var rules = new List<string>();
            foreach (var n in allow)
                if (n?.GetValue<string>() is { Length: > 0 } rule) rules.Add(rule);
            return [.. rules];
        }
        catch { return []; }
    }
}
