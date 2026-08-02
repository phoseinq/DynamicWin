using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Halo.Settings;

internal sealed class Store
{
    private readonly string _path;
    private readonly Dictionary<string, string> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _draft = new(StringComparer.OrdinalIgnoreCase);
    private bool _resetPending;

    internal Store(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "settings.json");
        Load();
    }

    internal int PendingCount => _resetPending ? Math.Max(1, _saved.Count) : _draft.Count;

    internal bool IsDirty => PendingCount > 0;

    internal string Text(string key, string fallback)
    {
        if (_draft.TryGetValue(key, out var d)) return d.Length > 0 ? d : fallback;
        if (_resetPending) return fallback;
        return _saved.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    }

    internal bool Bool(string key, bool fallback)
    {
        if (_draft.TryGetValue(key, out var d)) return d.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (_resetPending) return fallback;
        return _saved.TryGetValue(key, out var v) ? v.Equals("on", StringComparison.OrdinalIgnoreCase) : fallback;
    }

    internal void Set(string key, string value, string fallback = "")
    {
        if (string.Equals(Started(key, fallback), value, StringComparison.Ordinal)) _draft.Remove(key);
        else _draft[key] = value;
    }

    private string Started(string key, string fallback)
    {
        if (_resetPending) return fallback;
        return _saved.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    }

    internal void Apply()
    {
        if (!IsDirty) return;
        if (_resetPending) { _saved.Clear(); _resetPending = false; }
        foreach (var (key, value) in _draft) _saved[key] = value;
        _draft.Clear();
        Save();
    }

    internal void Discard()
    {
        _draft.Clear();
        _resetPending = false;
    }

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
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }
}
