using System;
using System.Collections.Generic;
using System.IO;

namespace Halo.Shell;

// The order the swap strip shows apps in, once the user has had an opinion about it.
//
// Registration order in the NotchController constructor is the default and stays the fallback - it is a
// deliberate ranking, not an accident. What this adds is an override: a kind the user has dragged sits
// where they put it, and everything they have never touched keeps its built-in position relative to the
// rest. That matters because the strip's contents come and go on their own; an order stored as a plain
// list of what was on screen at the time would reshuffle the moment a download finished.
//
// Pure and file-backed separately, so the ranking can be tested without a window.
internal sealed class StripOrder
{
    private readonly List<string> _pinned = [];

    internal IReadOnlyList<string> Pinned => _pinned;

    internal StripOrder() { }

    internal StripOrder(IEnumerable<string> pinned)
    {
        foreach (var k in pinned)
            if (!string.IsNullOrWhiteSpace(k) && !_pinned.Contains(k)) _pinned.Add(k.Trim());
    }

    // Kinds the user has ranked come first, in their order; the rest follow in the order they arrived,
    // which is registration order. A ranked kind that is not on screen right now is skipped and keeps its
    // place for when it comes back.
    internal List<string> Apply(IReadOnlyList<string> present)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var here = new HashSet<string>(present, StringComparer.Ordinal);
        var result = new List<string>(present.Count);
        foreach (var k in _pinned)
            if (here.Contains(k) && seen.Add(k)) result.Add(k);
        foreach (var k in present)
            if (seen.Add(k)) result.Add(k);
        return result;
    }

    // Dragging moves a kind past its neighbour IN THE CURRENT VIEW, not in the stored list - the stored
    // list may name kinds that are not on screen, and stepping past one of those would look to the user
    // like the drag did nothing.
    internal bool Move(IReadOnlyList<string> present, string kind, int delta)
    {
        if (delta == 0) return false;
        var view = Apply(present);
        int at = view.IndexOf(kind);
        if (at < 0) return false;
        int to = Math.Clamp(at + delta, 0, view.Count - 1);
        if (to == at) return false;

        view.RemoveAt(at);
        view.Insert(to, kind);

        // The whole visible order is written down, not just the one that moved. Recording a single
        // position leaves the kinds around it unranked, so the next thing to appear could land between
        // them and undo the arrangement the user just made by hand.
        foreach (var k in view)
            _pinned.Remove(k);
        _pinned.InsertRange(0, view);
        return true;
    }

    internal string Serialise() => string.Join('\n', _pinned);

    internal static StripOrder Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? new StripOrder(File.ReadAllLines(path))
                : new StripOrder();
        }
        catch { return new StripOrder(); }
    }

    internal void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Serialise());
        }
        catch { }   // an unwritable state directory costs the arrangement, not the pill
    }
}
