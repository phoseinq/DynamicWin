using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

// Whether a tool call is one the pill should offer to answer.
//
// PreToolUse fires for EVERY tool call, not only the ones that would have prompted — Claude decides that
// after hooks run. Intercepting naively would raise a banner for the `git status` already on the user's
// allowlist and *increase* the number of things they answer, so the gate reads `permissions.allow` itself
// and stays quiet on anything already covered.
//
// Every unreadable input answers NO. Silence means no decision, which leaves Claude's normal flow exactly
// as it is today — the worst failure this feature is allowed to have is that the terminal behaves the way
// it already does.
internal static class AskGate
{
    internal static bool ShouldAsk(string? toolName, JsonObject? toolInput, IReadOnlyList<string> allowRules)
    {
        if (string.IsNullOrEmpty(toolName) || toolInput is null) return false;

        // A question is not a permission: an allow rule says "you may run this", which has nothing to say
        // about which option a human would pick. Only the arity gate applies.
        if (toolName == "AskUserQuestion")
            return toolInput["questions"] is JsonArray q && q.Count == 1;

        string? target = TargetOf(toolName, toolInput);
        foreach (var rule in allowRules)
            if (AllowRuleMatches(rule, toolName, target)) return false;
        return true;
    }

    // Which field of tool_input carries the thing a rule's pattern is written against. A tool that is not
    // listed has no target, so only a bare `Tool` rule can cover it — being coarse here costs a banner,
    // never an allow that should not have happened.
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
        if (open < 0) return rule == toolName;          // bare tool name = blanket allow for that tool
        if (!rule.EndsWith(")", StringComparison.Ordinal)) return false;

        string tool = rule[..open], pattern = rule[(open + 1)..^1];
        if (tool.Length == 0 || pattern.Length == 0) return false;
        if (tool != toolName) return false;
        if (target is null) return false;               // a pattern cannot match what could not be read

        // "prefix:*" is Claude Code's own form and means "the command starts with prefix". The colon is a
        // separator, not a character the command has to contain — globbing it as written matches nothing.
        if (pattern.EndsWith(":*", StringComparison.Ordinal))
            return target.StartsWith(pattern[..^2], StringComparison.Ordinal);

        return Glob(pattern, target);
    }

    // '*' spans separators too. Deliberately coarser than the real rule language: a pattern this does not
    // understand simply fails to match, and a failed match only costs one banner for something already
    // allowed. The opposite error — matching too eagerly — would silently swallow a real prompt.
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
