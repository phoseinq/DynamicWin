using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halo.Settings;

// What the pill and the settings panel agree on, and the whole of it.
//
// One flat map of key to string, because that is exactly the shape the panel's rows already have - a
// toggle is "on"/"off", a choice is its label, a slider is its stop - and a typed schema on either side
// would have to be edited in two places every time a row is added. Unknown keys are kept on write, so a
// newer panel writing a setting this build has never heard of does not lose it on the next save.
//
// The shape is DUPLICATED in the panel rather than shared through a library, the same decision
// AskEnvelope records for Halo.Hooks: two executables that ship together but must not be able to break
// each other at load time. Both sides pin the round-trip with tests.
internal sealed class SettingsFile
{
    internal const int CurrentVersion = 1;

    private readonly Dictionary<string, string> _values;

    internal SettingsFile(IDictionary<string, string>? values = null)
        => _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    internal static SettingsFile Empty => new();

    internal IReadOnlyDictionary<string, string> Values => _values;

    // Defaults live at the READ, never in the file. A settings file that has to list every default is a
    // file that goes stale the moment a default is reconsidered, and every key here has a sensible
    // "the user has not said" answer.
    internal string Text(string key, string fallback)
        => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    internal bool Bool(string key, bool fallback)
        => _values.TryGetValue(key, out var v)
            ? v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            : fallback;

    // Tolerates the trailing unit the panel's sliders carry ("100%"), because the row's value IS its label
    internal float Number(string key, float fallback)
    {
        if (!_values.TryGetValue(key, out var v) || v.Length == 0) return fallback;
        var span = v.AsSpan().Trim().TrimEnd('%').Trim();
        return float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    internal SettingsFile With(string key, string value)
    {
        var next = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase) { [key] = value };
        return new SettingsFile(next);
    }

    internal string ToJson()
    {
        var values = new JsonObject();
        foreach (var (key, value) in _values) values[key] = value;
        return new JsonObject { ["version"] = CurrentVersion, ["values"] = values }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // Anything unreadable is "the user has not set anything", never an exception: this is read on the
    // render path's own process at startup, and a half-written file must cost defaults, not a launch.
    internal static SettingsFile FromJson(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return Empty;
            if (JsonNode.Parse(json) is not JsonObject root) return Empty;
            if (root["values"] is not JsonObject values) return Empty;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, node) in values)
            {
                if (node is null) continue;
                string? text = node is JsonValue value && value.TryGetValue<string>(out var s)
                    ? s : node.ToJsonString().Trim('"');
                if (!string.IsNullOrEmpty(text)) map[key] = text;
            }
            return new SettingsFile(map);
        }
        catch { return Empty; }
    }

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "settings.json");

    // tmp then rename, atomic on NTFS - the other process is a file watcher and must never be handed
    // half a document
    internal bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    internal static SettingsFile Read(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : Empty; }
        catch { return Empty; }
    }
}

// The keys, in one place, so a typo cannot make the panel write a setting the pill never reads. Feature
// keys are derived rather than listed: the widget catalog is what decides which features exist.
internal static class SettingsKeys
{
    internal const string StartWithWindows = "general.startup";
    internal const string OverFullscreen = "general.fullscreen";
    internal const string InCaptures = "general.capture";
    internal const string FollowFocus = "general.follow";
    internal const string Scale = "appearance.scale";
    internal const string Glass = "appearance.glass";
    internal const string Motion = "appearance.motion";

    internal static string Feature(FeatureId id) => "feature." + FeatureCatalog.For(id).Key;
}
