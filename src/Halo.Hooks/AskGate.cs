using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

internal static class AskGate
{
    internal static bool ShouldAsk(string? toolName, JsonObject? toolInput, IReadOnlyList<string> allowRules)
    {
        if (string.IsNullOrEmpty(toolName) || toolInput is null) return false;

        if (toolName == "AskUserQuestion")
            return toolInput["questions"] is JsonArray q && q.Count == 1;

        if (!AnswerPermissions) return false;

        string? target = TargetOf(toolName, toolInput);
        foreach (var rule in allowRules)
            if (AllowRuleMatches(rule, toolName, target)) return false;
        return true;
    }

    internal static bool AnswerPermissions;

    internal static string? TargetOf(string? toolName, JsonObject? toolInput)
    {
        if (toolInput is null) return null;
        string? field = toolName switch
        {
            "Bash" or "PowerShell" => "command",
            "Read" or "Write" or "Edit" or "NotebookEdit" => "file_path",
            "WebFetch" => "url",
            _ => null,
        };
        if (field is null) return null;
        return toolInput[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    internal static bool AllowRuleMatches(string rule, string? toolName, string? target)
    {
        if (string.IsNullOrWhiteSpace(rule) || string.IsNullOrEmpty(toolName)) return false;

        int open = rule.IndexOf('(');
        if (open < 0) return rule == toolName;
        if (!rule.EndsWith(")", StringComparison.Ordinal)) return false;

        string tool = rule[..open], pattern = rule[(open + 1)..^1];
        if (tool.Length == 0 || pattern.Length == 0) return false;
        if (tool != toolName) return false;
        if (target is null) return false;

        if (pattern.EndsWith(":*", StringComparison.Ordinal))
            return target.StartsWith(pattern[..^2], StringComparison.Ordinal);

        return Glob(pattern, target);
    }

    private static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
