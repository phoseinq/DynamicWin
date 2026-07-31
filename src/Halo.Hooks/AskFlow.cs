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
    private const int PollMs = 25;

    internal static void Run(string dir, JsonObject? input, string? sessionId, string? cwd, int pid)
    {
        try
        {
            string? tool = input?["tool_name"]?.GetValue<string>();
            var toolInput = input?["tool_input"] as JsonObject;
            if (!AskGate.ShouldAsk(tool, toolInput, AskSettings.AllowRules(cwd))) return;

            var ask = Envelope(tool!, toolInput!, sessionId, pid);
            var answer = Wait(dir, ask);
            if (answer is not null) Console.Out.Write(answer.ToHookStdout());
        }
        catch { }   // a hook may not break Claude Code, so every failure is silence
    }

    private static AskEnvelope Envelope(string tool, JsonObject toolInput, string? sessionId, int pid)
    {
        var options = new List<AskOption>();
        string? question = null;

        if (tool == "AskUserQuestion" && toolInput["questions"] is JsonArray qs && qs.Count == 1
            && qs[0] is JsonObject q)
        {
            question = q["question"]?.GetValue<string>();
            if (q["options"] is JsonArray opts)
                foreach (var n in opts)
                    if (n is JsonObject o && o["label"]?.GetValue<string>() is { Length: > 0 } label)
                        options.Add(new AskOption(label, o["description"]?.GetValue<string>() ?? ""));
        }
        else
        {
            // A permission is answerable exactly: allow is a real allow. An "always" chip is deliberately
            // not here - a hook decision only covers the call in front of it, so "always" would mean Halo
            // writing rules into the user's own settings.json, which is a different feature.
            options.Add(new AskOption("allow", "run it"));
            options.Add(new AskOption("deny", "skip it"));
        }

        return new AskEnvelope(
            Guid.NewGuid().ToString("n"), pid, sessionId, tool,
            AskGate.TargetOf(tool, toolInput), question, options,
            DateTimeOffset.UtcNow.AddMilliseconds(AnswerMs));
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
