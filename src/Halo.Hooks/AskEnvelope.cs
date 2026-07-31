using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

internal sealed record AskOption(string Label, string Description);

// What the hook writes for the pill to answer. Read by Halo.App as loose JSON under ~/.claude/notch,
// which is how every agent surface in this app already talks: no dependency, and debuggable with `cat`.
//
// ExpiresAt is wall-clock UTC on purpose. A monotonic deadline would let a machine that slept for an hour
// come back to a question nobody remembers being asked; wall clock expires it instead.
internal sealed record AskEnvelope(
    string Nonce,
    int Pid,
    string? Session,
    string Tool,
    string? Target,
    string? Question,
    IReadOnlyList<AskOption> Options,
    DateTimeOffset ExpiresAt)
{
    internal bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    internal string ToJson()
    {
        var options = new JsonArray();
        foreach (var o in Options)
            options.Add(new JsonObject { ["label"] = o.Label, ["description"] = o.Description });
        return new JsonObject
        {
            ["nonce"] = Nonce,
            ["pid"] = Pid,
            ["session"] = Session,
            ["tool"] = Tool,
            ["target"] = Target,
            ["question"] = Question,
            ["options"] = options,
            ["expiresAt"] = ExpiresAt.ToString("o"),
        }.ToJsonString();
    }

    // Unknown fields are ignored rather than rejected: the pill and the hook are deployed separately here
    // (hooks quick-deploy over the installed copy), so a newer writer must not break an older reader.
    internal static AskEnvelope? FromJson(string? json)
    {
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject o) return null;
            string? nonce = o["nonce"]?.GetValue<string>();
            string? tool = o["tool"]?.GetValue<string>();
            if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(tool)) return null;
            if (!DateTimeOffset.TryParse(o["expiresAt"]?.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var expires))
                return null;

            var options = new List<AskOption>();
            if (o["options"] is JsonArray arr)
                foreach (var n in arr)
                    if (n is JsonObject oo && oo["label"]?.GetValue<string>() is { Length: > 0 } label)
                        options.Add(new AskOption(label, oo["description"]?.GetValue<string>() ?? ""));

            return new AskEnvelope(
                nonce,
                o["pid"] is JsonValue pv && pv.TryGetValue<int>(out var pid) ? pid : 0,
                o["session"]?.GetValue<string>(),
                tool,
                o["target"]?.GetValue<string>(),
                o["question"]?.GetValue<string>(),
                options,
                expires);
        }
        catch { return null; }
    }
}

// The pill's reply. Decision is Claude Code's own vocabulary, so it goes to stdout unchanged.
internal sealed record AskAnswer(string Nonce, string Decision, string? Reason)
{
    internal string ToJson() => new JsonObject
    {
        ["nonce"] = Nonce,
        ["decision"] = Decision,
        ["reason"] = Reason,
    }.ToJsonString();

    internal static AskAnswer? FromJson(string? json)
    {
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject o) return null;
            string? nonce = o["nonce"]?.GetValue<string>();
            string? decision = o["decision"]?.GetValue<string>();
            if (string.IsNullOrEmpty(nonce) || decision is not ("allow" or "deny" or "ask")) return null;
            return new AskAnswer(nonce, decision, o["reason"]?.GetValue<string>());
        }
        catch { return null; }
    }

    // The contract with Claude Code, and the only thing this hook is allowed to print. Asserted as an
    // exact string in the tests because it is an external interface, not an internal detail.
    internal string ToHookStdout() => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = Decision,
            ["permissionDecisionReason"] = Reason ?? "",
        },
    }.ToJsonString();
}
