using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Halo.ClaudeCode;

internal sealed record AskOption(string Label, string Description);

// A question the hook has parked, waiting for someone to click a chip. Mirrors Halo.Hooks.AskEnvelope;
// the two projects share no code by design, so the shape is duplicated and the round-trip is pinned by
// tests on both sides.
internal sealed record PendingAsk(
    string Nonce,
    int Pid,
    string? Session,
    string Tool,
    string? Target,
    string? Question,
    IReadOnlyList<AskOption> Options,
    DateTimeOffset ExpiresAt)
{
    internal bool IsQuestion => Tool == "AskUserQuestion";
}

// FIFO, one banner at a time, and an expired head steps aside rather than blocking what is behind it.
// Pure so it can be tested, the way NotchVisibility and AgentNoticeCoordinator were.
internal sealed class AskQueue
{
    private readonly List<PendingAsk> _items = [];

    internal int Count => _items.Count;

    // The directory is rescanned on every poll, so the same ask arrives over and over. Re-observing keeps
    // the original position: a rescan that reordered the queue would swap the banner out from under the
    // user's cursor between one poll and the next.
    internal void Observe(PendingAsk ask)
    {
        foreach (var existing in _items)
            if (existing.Nonce == ask.Nonce) return;
        _items.Add(ask);
    }

    internal PendingAsk? Head(DateTimeOffset now)
    {
        foreach (var item in _items)
            if (now < item.ExpiresAt) return item;
        return null;
    }

    internal void Remove(string nonce) => _items.RemoveAll(i => i.Nonce == nonce);

    internal IReadOnlyList<string> Nonces() => _items.ConvertAll(i => i.Nonce);

    internal IReadOnlyList<string> Sweep(DateTimeOffset now)
    {
        var dropped = new List<string>();
        foreach (var item in _items)
            if (now >= item.ExpiresAt) dropped.Add(item.Nonce);
        foreach (var nonce in dropped) Remove(nonce);
        return dropped;
    }
}

// The pill's side of the rendezvous: read the asks, ack them so the hook keeps waiting, hand the head to
// the banner, and write the answer a click produces.
//
// Driven by StatusStore's existing watcher and 1s poll over the same directory, so this adds no timer and
// no thread of its own.
internal sealed class AskStore
{
    private readonly string _dir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly AskQueue _queue = new();
    private readonly HashSet<string> _acked = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _version;

    internal AskStore(string dir, Func<DateTimeOffset>? clock = null)
    {
        _dir = dir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal int Version => System.Threading.Volatile.Read(ref _version);

    internal PendingAsk? Pending
    {
        get { lock (_lock) return _queue.Head(_clock()); }
    }

    internal void Rescan()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var now = _clock();
            string? before;
            lock (_lock) before = _queue.Head(now)?.Nonce;

            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(_dir, "ask-*.json"))
            {
                var ask = Parse(path);
                if (ask is null || now >= ask.ExpiresAt) continue;
                onDisk.Add(ask.Nonce);
                lock (_lock) _queue.Observe(ask);

                // Ack EVERY ask on sight, not just the one on screen. The hook gives up after 300ms
                // without an ack, so acking only the head would make a second, queued question fall back
                // to the terminal before its turn ever came.
                if (_acked.Add(ask.Nonce)) Touch(Path.Combine(_dir, $"ack-{ask.Nonce}"));
            }

            // The hook deletes its ask file the moment it gives up or gets an answer, and nothing here was
            // noticing: the queue kept serving a question whose asker had already walked away, so the pill
            // showed the PREVIOUS question while the new one waited behind it. The directory is the
            // authority on what is still being asked.
            List<string> gone;
            lock (_lock)
            {
                gone = [.. _queue.Nonces().Where(n => !onDisk.Contains(n))];
                foreach (var nonce in gone) _queue.Remove(nonce);
            }
            foreach (var nonce in gone) Forget(nonce);

            List<string> expired;
            lock (_lock) expired = [.. _queue.Sweep(now)];
            foreach (var nonce in expired) Forget(nonce);

            string? after;
            lock (_lock) after = _queue.Head(now)?.Nonce;
            if (before != after) System.Threading.Interlocked.Increment(ref _version);
        }
        catch { }   // a failed probe is normal here and must degrade silently
    }

    // A question and a permission are answered through completely different doors.
    //
    // A permission's answer IS the hook's decision, so it goes back through the rendezvous file the hook is
    // sitting on. A question's cannot: a PreToolUse hook has no way to say "the user picked option two",
    // and the deny-with-the-answer-in-the-reason trick that used to live here meant Claude Code never drew
    // its own box and rendered the answer in red under "Error:". So the tool now runs, the terminal draws
    // its box, and the pill answers it the way a human at that keyboard would - by typing the number into
    // that terminal. No focus is taken (see ConsoleRead.Type), so answering on the pill does not pull the
    // window forward.
    internal bool Answer(PendingAsk ask, string label)
    {
        if (ask.IsQuestion) return Press(ask, label);
        try
        {
            string decision = label;
            string reason = $"{label} from the pill";
            var json = new JsonObject
            {
                ["nonce"] = ask.Nonce,
                ["decision"] = decision,
                ["reason"] = reason,
            }.ToJsonString();

            string path = Path.Combine(_dir, $"answer-{ask.Nonce}.json");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);   // atomic on NTFS: the hook must never read half of it
        }
        catch { }
        finally
        {
            lock (_lock) _queue.Remove(ask.Nonce);
            System.Threading.Interlocked.Increment(ref _version);
        }
        return true;
    }

    // Type the option's number into the agent's own terminal, exactly as the person sitting at it would.
    //
    // Nothing is sent if the terminal cannot be reached - a keystroke aimed at a window we have not found
    // would land in whatever DID have focus, and typing a stray digit into someone's editor is a worse
    // failure than the banner simply not working. The banner stays up in that case, because the question
    // in the terminal is still standing too.
    private bool Press(PendingAsk ask, string label)
    {
        if (ask.Pid <= 0) { Trace($"no pid for {ask.Nonce}"); return false; }
        int index = -1;
        for (int i = 0; i < ask.Options.Count && index < 0; i++)
            if (string.Equals(ask.Options[i].Label, label, StringComparison.Ordinal)) index = i;

        bool sent = index >= 0 && index < 9
            // the box numbers its rows from one, and beyond nine there is no single key to send
            ? Interop.ConsoleRead.Type(ask.Pid, (index + 1).ToString())
            : Write(ask, label);
        Trace($"{(index >= 0 ? "row " + (index + 1) : "words")} -> pid {ask.Pid} = {sent}");
        if (!sent) return false;

        // Dropped locally the moment it is sent rather than waiting for the hook's PostToolUse sweep: the
        // banner must go on the click, and a rescan finding the file still there would put it straight back.
        lock (_lock) _queue.Remove(ask.Nonce);
        Forget(ask.Nonce);
        try { File.Delete(Path.Combine(_dir, $"ask-{ask.Nonce}.json")); } catch { }
        System.Threading.Interlocked.Increment(ref _version);
        return true;
    }

    // Words of your own, one row PAST the last option - so the way in is to walk down off the end of the
    // list. Established by driving a live box and reading back what came out, not from any documentation of
    // the key map.
    //
    // What that row IS has changed under us. It used to be a bare free-text field you could type straight
    // into; the box now puts "Chat about this" there, a row that has to be activated before anything is
    // listening, and activating it cancels the question and hands the words to the prompt instead. Hence
    // the Enter before the text: without it the letters were going into a highlighted row that ignores
    // them, and the trailing Enter then triggered the row with nothing typed - the question just vanished.
    // Harmless in the old shape too, where Enter on the field did nothing worth avoiding.
    //
    // The walk is done here and now, because its result is what tells the caller whether the banner may
    // come down; everything after it runs on the pool, since the box needs a moment between each step and
    // half a second of sleeps on the render thread is a visible stutter.
    private static bool Write(PendingAsk ask, string text)
    {
        int pid = ask.Pid, rows = ask.Options.Count;
        if (rows <= 0 || string.IsNullOrWhiteSpace(text)) return false;
        if (!Interop.ConsoleRead.Press(pid, Interop.ConsoleRead.VkDown, rows)) return false;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                System.Threading.Thread.Sleep(140);
                if (!Interop.ConsoleRead.Press(pid, Interop.ConsoleRead.VkEnter)) return;
                System.Threading.Thread.Sleep(180);   // the box tears down and the prompt takes focus
                if (!Interop.ConsoleRead.Type(pid, text)) return;
                System.Threading.Thread.Sleep(140);
                Interop.ConsoleRead.Press(pid, Interop.ConsoleRead.VkEnter);
            }
            catch { }
        });
        return true;
    }

    // Dev trace for the one path that cannot be watched any other way: the pill is uncapturable, the
    // keystroke goes to a window that is not in front, and "it did not send" has no other evidence.
    private static void Trace(string line)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "ask-debug.txt");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch { }
    }

    private void Forget(string nonce)
    {
        _acked.Remove(nonce);
        // the hook owns ask-*/answer-* and deletes them itself; sweeping our own ack is enough, and doing
        // more would race the process that is still holding them
        Delete(Path.Combine(_dir, $"ack-{nonce}"));
    }

    private PendingAsk? Parse(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
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
            if (options.Count == 0) return null;   // nothing to click is not a question

            return new PendingAsk(
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

    private static void Touch(string path)
    {
        try { if (!File.Exists(path)) File.WriteAllText(path, ""); } catch { }
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
