using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

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
        catch { return []; }
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
