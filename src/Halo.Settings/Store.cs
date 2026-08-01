using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Halo.Settings;

// The panel's copy of the settings contract. Deliberately a copy: Halo.App holds the same shape in
// src/Halo.App/Settings/SettingsFile.cs, and the two executables share no code - the decision
// Halo.Hooks' AskEnvelope already records, so that a change to one cannot stop the other from loading.
// Both sides pin the round trip with tests.
internal sealed class Store
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal Store(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "settings.json");
        Load();
    }

    internal string Text(string key, string fallback)
        => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    internal bool Bool(string key, bool fallback)
        => _values.TryGetValue(key, out var v)
            ? v.Equals("on", StringComparison.OrdinalIgnoreCase)
            : fallback;

    // Written the moment a row is touched. There is no Apply button by design: the pill watches this
    // file and applies within a frame, so a button would only add a way to lose a change.
    internal void Set(string key, string value)
    {
        if (Text(key, "") == value) return;
        _values[key] = value;
        Save();
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
                    _values[key] = text;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var values = new JsonObject();
            foreach (var (key, value) in _values) values[key] = value;
            var json = new JsonObject { ["version"] = 1, ["values"] = values }
                .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);   // atomic on NTFS: the pill is a watcher
        }
        catch { }
    }
}
