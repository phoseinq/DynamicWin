using System;
using System.Collections.Generic;
using System.IO;

namespace Halo.Shell;

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
        catch { }
    }
}
