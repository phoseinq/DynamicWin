using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

internal static class AskFlow
{
    private const int AckMs = 300;
    private const int AnswerMs = 20_000;

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

            if (ask.IsQuestion) { Publish(dir, ask, pid); return; }

            var answer = Wait(dir, ask);
            if (answer is not null) Console.Out.Write(answer.ToHookStdout());
        }
        catch { }
    }

    private static void Publish(string dir, AskEnvelope ask, int pid)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Clear(dir, pid);
            WriteAtomic(Path.Combine(dir, $"ask-{ask.Nonce}.json"), ask.ToJson());
        }
        catch { }
    }

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

            options.Add(new AskOption("allow", "run it"));
            options.Add(new AskOption("deny", "skip it"));
        }

        bool isQuestion = tool == "AskUserQuestion";
        return new AskEnvelope(
            Guid.NewGuid().ToString("n"), pid, sessionId, tool,
            AskGate.TargetOf(tool, toolInput), question, options,
            DateTimeOffset.UtcNow.AddMilliseconds(isQuestion ? QuestionMs : AnswerMs));
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
            System.Threading.Thread.Sleep(PollMs);
        }
        return File.Exists(path);
    }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static string? ReadOrNull(string path)
    {
        for (int i = 0; i < 5; i++)
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
