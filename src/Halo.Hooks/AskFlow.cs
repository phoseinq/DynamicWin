using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

// The hook half of "answer Claude's prompt from the pill": decide whether this call is worth asking
// about, put the question where Halo can see it, and wait for a human to click.
//
// The safety rule the whole feature hangs on lives here: a decision is printed ONLY when an answer file
// came back. Silence, timeout, Halo not running, a malformed file - all of them print nothing, which
// leaves Claude's normal flow exactly as it is today.
internal static class AskFlow
{
    private const int AckMs = 300;         // Halo is not running -> get out of the way immediately
    private const int AnswerMs = 20_000;   // then the banner goes and the terminal prompt stands
    // A question is not on a countdown any more: its box is up in the terminal until a human deals with
    // it, and the banner must stand for as long as the box does. This is only the backstop for a session
    // killed mid-question, where nothing will ever come along to clear the file.
    private const int QuestionMs = 30 * 60_000;
    private const int PollMs = 25;

    internal static void Run(string dir, JsonObject? input, string? sessionId, string? cwd, int pid)
    {
        try
        {
            string? tool = input?["tool_name"]?.GetValue<string>();
            var toolInput = input?["tool_input"] as JsonObject;
            if (!AskGate.ShouldAsk(tool, toolInput, AskSettings.AllowRules(cwd))) return;

            var ask = Envelope(tool!, toolInput!, sessionId, pid);

            // A QUESTION is published and then left alone: no ack wait, no answer wait, and above all no
            // decision on stdout. A hook cannot say "the user picked option two" - the only way to answer
            // one from out here is to DENY the call and put the choice in the reason, which meant Claude
            // Code never drew its own box and rendered the answer in red under "Error:". So the tool is
            // allowed to run and draw its box; the pill mirrors the same question beside it and answers by
            // typing the number into this terminal (Halo.Interop.ConsoleRead). Two surfaces, one question,
            // and whichever the user reaches for first wins.
            if (ask.IsQuestion) { Publish(dir, ask, pid); return; }

            // A PERMISSION still blocks, because there the hook's decision IS the answer.
            var answer = Wait(dir, ask);
            if (answer is not null) Console.Out.Write(answer.ToHookStdout());
        }
        catch { }   // a hook may not break Claude Code, so every failure is silence
    }

    // Nobody is waiting on this one, so its life is the tool call's, not a countdown: the PostToolUse hook
    // clears it when the box is answered in the terminal, and the pill clears it when answered there. The
    // long expiry is only the backstop for a session that dies mid-question.
    private static void Publish(string dir, AskEnvelope ask, int pid)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Clear(dir, pid);   // one question per session on screen; a stale one would outrank the new one
            WriteAtomic(Path.Combine(dir, $"ask-{ask.Nonce}.json"), ask.ToJson());
        }
        catch { }
    }

    // Every parked question belonging to this agent. Called when its AskUserQuestion finishes, however it
    // was answered - the pill has no other way to learn that the box in the terminal was used.
    internal static void Clear(string dir, int pid)
    {
        try
        {
            if (pid <= 0 || !Directory.Exists(dir)) return;
            foreach (var path in Directory.GetFiles(dir, "ask-*.json"))
            {
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) continue;
                    if (o["pid"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var p) && p == pid)
                        Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    private static AskEnvelope Envelope(string tool, JsonObject toolInput, string? sessionId, int pid)
    {
        var options = new List<AskOption>();
        string? question = null;
        bool multiSelect = false, hasPreview = false;

        if (tool == "AskUserQuestion" && toolInput["questions"] is JsonArray qs && qs.Count == 1
            && qs[0] is JsonObject q)
        {
            question = q["question"]?.GetValue<string>();
            // both were sitting right here unread. The banner draws rows past the options that only exist
            // on some shapes of the box, and it cannot tell which shape it is from labels alone.
            multiSelect = q["multiSelect"] is JsonValue mv && mv.TryGetValue<bool>(out var m) && m;
            if (q["options"] is JsonArray opts)
                foreach (var n in opts)
                    if (n is JsonObject o && o["label"]?.GetValue<string>() is { Length: > 0 } label)
                    {
                        options.Add(new AskOption(label, o["description"]?.GetValue<string>() ?? ""));
                        if (o["preview"] is JsonValue pv && pv.TryGetValue<string>(out var p)
                            && !string.IsNullOrEmpty(p)) hasPreview = true;
                    }
        }
        else
        {
            // A permission is answerable exactly: allow is a real allow. An "always" chip is deliberately
            // not here - a hook decision only covers the call in front of it, so "always" would mean Halo
            // writing rules into the user's own settings.json, which is a different feature.
            options.Add(new AskOption("allow", "run it"));
            options.Add(new AskOption("deny", "skip it"));
        }

        bool isQuestion = tool == "AskUserQuestion";
        return new AskEnvelope(
            Guid.NewGuid().ToString("n"), pid, sessionId, tool,
            AskGate.TargetOf(tool, toolInput), question, options,
            DateTimeOffset.UtcNow.AddMilliseconds(isQuestion ? QuestionMs : AnswerMs),
            multiSelect, hasPreview);
    }

    private static AskAnswer? Wait(string dir, AskEnvelope ask)
    {
        string askPath = Path.Combine(dir, $"ask-{ask.Nonce}.json");
        string ackPath = Path.Combine(dir, $"ack-{ask.Nonce}");
        string answerPath = Path.Combine(dir, $"answer-{ask.Nonce}.json");
        try
        {
            Directory.CreateDirectory(dir);
            WriteAtomic(askPath, ask.ToJson());

            // The ack means "seen", not "guaranteed". Buying more than that would mean a protocol that
            // survives Halo dying mid-question, and the fallback for that is already correct: wait it out.
            if (!WaitForFile(ackPath, AckMs)) return null;
            if (!WaitForFile(answerPath, AnswerMs)) return null;

            var answer = AskAnswer.FromJson(ReadOrNull(answerPath));
            return answer?.Nonce == ask.Nonce ? answer : null;
        }
        catch { return null; }
        finally
        {
            Delete(askPath);
            Delete(ackPath);
            Delete(answerPath);
        }
    }

    private static bool WaitForFile(string path, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (File.Exists(path)) return true;
            System.Threading.Thread.Sleep(PollMs);   // sleeps rather than spins: this waits on a human
        }
        return File.Exists(path);
    }

    // temp name then rename, which is atomic on NTFS: the reader is a file watcher on the same directory
    // and must never be handed half a document
    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static string? ReadOrNull(string path)
    {
        for (int i = 0; i < 5; i++)   // the writer may still be renaming it into place
        {
            try { return File.ReadAllText(path); }
            catch (IOException) { System.Threading.Thread.Sleep(PollMs); }
        }
        return null;
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
