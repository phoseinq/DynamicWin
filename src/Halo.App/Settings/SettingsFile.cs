using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halo.Settings;

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

    internal string Text(string key, string fallback)
        => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    internal bool Bool(string key, bool fallback)
        => _values.TryGetValue(key, out var v)
            ? v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            : fallback;

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
