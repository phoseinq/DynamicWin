using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Halo.Settings;

// The panel's copy of the settings contract. Deliberately a copy: Halo.App holds the same shape in
// src/Halo.App/Settings/SettingsFile.cs, and the two executables share no code - the decision
// Halo.Hooks' AskEnvelope already records, so that a change to one cannot stop the other from loading.
// Both sides pin the round trip with tests.
// Two layers, and only the lower one is ever on disk. Touching a row edits the DRAFT; the file - and so
// the pill, which watches it - sees nothing until Apply. Reset throws the draft away.
//
// It used to write straight through, on the argument that an Apply button is only a way to lose a change.
// That argument is wrong for these particular settings: several of them (pill scale, glass, motion) are
// things you flick between while looking at the result, and writing every intermediate value meant the
// pill visibly lurched through each one. A draft also gives "are you sure" something to be about.
internal sealed class Store
{
    private readonly string _path;
    private readonly Dictionary<string, string> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _draft = new(StringComparer.OrdinalIgnoreCase);
    private bool _resetPending;   // "reset to defaults" staged: an empty draft that still differs

    internal Store(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "settings.json");
        Load();
    }

    // How many rows the Apply button is about. Zero means both buttons are dead, which is the whole
    // contract the user asked for: nothing to apply, nothing to undo.
    internal int PendingCount => _resetPending ? Math.Max(1, _saved.Count) : _draft.Count;

    internal bool IsDirty => PendingCount > 0;



    internal string Text(string key, string fallback)
    {
        if (_draft.TryGetValue(key, out var d)) return d.Length > 0 ? d : fallback;
        if (_resetPending) return fallback;   // staged defaults: read as if the file were already empty
        return _saved.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    }

    internal bool Bool(string key, bool fallback)
    {
        if (_draft.TryGetValue(key, out var d)) return d.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (_resetPending) return fallback;
        return _saved.TryGetValue(key, out var v) ? v.Equals("on", StringComparison.OrdinalIgnoreCase) : fallback;
    }

    // Setting a row back to where it started REMOVES it from the draft rather than recording a no-op.
    //
    // The fallback is a parameter and that is the whole fix: this compared the new value against the
    // SAVED one, and a row that has never been written has no saved value at all - so flicking a toggle
    // off and straight back on compared "on" against "" , decided they differed, and left a pending change
    // that would have written nothing. The count only ever climbed. What a row started as is the saved
    // value if there is one and the catalog's default if there is not, which is exactly what every reader
    // below already does.
    internal void Set(string key, string value, string fallback = "")
    {
        if (string.Equals(Started(key, fallback), value, StringComparison.Ordinal)) _draft.Remove(key);
        else _draft[key] = value;
    }

    private string Started(string key, string fallback)
    {
        if (_resetPending) return fallback;   // staged defaults: the row started at its default
        return _saved.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    }

    // The draft is merged onto what is on disk NOW, not onto the snapshot this window opened with. The
    // pill writes this same file - dragging it parks a new scale, the pushpin writes over-fullscreen, the
    // api hands itself a token - so a panel left open across any of those used to write its stale copy
    // back over them, silently undoing a change the user had just made by hand. Re-reading costs one file
    // read per Apply and makes the panel a merge rather than an overwrite.
    internal void Apply()
    {
        if (!IsDirty) return;
        if (_resetPending) { _saved.Clear(); _resetPending = false; }
        else { _saved.Clear(); Load(); }
        foreach (var (key, value) in _draft) _saved[key] = value;
        _draft.Clear();
        Save();
    }

    internal void Discard()
    {
        _draft.Clear();
        _resetPending = false;
    }

    // Defaults are the ABSENCE of a value, not a set of values written over the file - the fallbacks live
    // in the catalog, and writing them out would freeze today's defaults into every install that ever
    // pressed this button. Staged like any other edit, so it is undoable until Apply.
    internal void StageDefaults()
    {
        _draft.Clear();
        _resetPending = _saved.Count > 0;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject root) return;
            if (root["values"] is not JsonObject values) return;
            foreach (var (key, node) in values)
                if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
                    _saved[key] = text;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var values = new JsonObject();
            foreach (var (key, value) in _saved) values[key] = value;
            var json = new JsonObject { ["version"] = 1, ["values"] = values }
                .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);   // atomic on NTFS: the pill is a watcher
        }
        catch { }
    }
}
