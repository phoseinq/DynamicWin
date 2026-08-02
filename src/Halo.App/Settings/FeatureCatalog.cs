using System;

namespace Halo.Settings;

internal enum FeatureId
{
    Media,
    Downloads,
    FileTray,
    Bluetooth,
    Notifications,
    ClaudeCode,
    Codex,
    GenericAgents,
}

internal sealed record FeatureDefinition(FeatureId Id, string Key, string Label, string Description);

internal static class FeatureCatalog
{

    internal static readonly FeatureDefinition[] All =
    [
        new(FeatureId.Media, "media", "Media", "Playback sessions and classic VLC controls"),
        new(FeatureId.Downloads, "downloads", "Downloads", "Browser, store, game and app progress"),
        new(FeatureId.FileTray, "fileTray", "File Tray", "Drag-and-drop shelf and clipboard images"),
        new(FeatureId.Bluetooth, "bluetooth", "Bluetooth", "Connection and battery takeovers"),
        new(FeatureId.Notifications, "notifications", "Notifications", "Mirror Windows toast banners"),
        new(FeatureId.ClaudeCode, "claudeCode", "Claude Code", "Claude sessions, limits and controls"),
        new(FeatureId.Codex, "codex", "Codex", "Codex Desktop and CLI sessions"),
        new(FeatureId.GenericAgents, "genericAgents", "Other agents", "Generic agent status files"),
    ];

    internal static FeatureDefinition For(FeatureId id)
        => Array.Find(All, item => item.Id == id)!;
}
