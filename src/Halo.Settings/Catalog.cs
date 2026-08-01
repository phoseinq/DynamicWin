using System.Collections.Generic;

namespace Halo.Settings;

internal enum PageId
{
    General, Appearance, Media, Downloads, FileTray, Bluetooth, Notifications, Alerts,
    ClaudeCode, Codex, OtherAgents, Access,
}

internal enum RowKind { Toggle, Choice, Action, Status }

internal sealed record Row(
    string Key,
    string Label,
    string Description,
    RowKind Kind,
    string Fallback,
    IReadOnlyList<string> Options,
    string ActionLabel = "");

internal sealed record Section(string Label, IReadOnlyList<Row> Rows);

internal sealed record Page(PageId Id, string Label, string Description, IReadOnlyList<Section> Sections);

// Every row here is a setting the pill actually reads. The rejected demo panel was full of plausible
// rows that changed nothing, and this project's rule about invented numbers is the same rule: a control
// the underlying thing cannot honour is worse than no control.
internal static class Catalog
{
    private static Row Toggle(string key, string label, string description, bool on = true)
        => new(key, label, description, RowKind.Toggle, on ? "on" : "off", ["off", "on"]);

    private static Row Choice(string key, string label, string description, string fallback, params string[] options)
        => new(key, label, description, RowKind.Choice, fallback, options);

    private static Row Action(string key, string label, string description, string action)
        => new(key, label, description, RowKind.Action, "", [], action);

    // one page per widget family, so the panel's list and the pill's list are two views of one list
    private static Page Feature(PageId id, string key, string label, string description,
        params Row[] extra)
    {
        var rows = new List<Row>
        {
            Toggle("feature." + key, "Show " + label.ToLowerInvariant(),
                "Let this take over the pill when it has something to say"),
        };
        rows.AddRange(extra);
        return new Page(id, label, description, [new Section("SURFACE", rows)]);
    }

    internal static readonly Page[] Pages =
    [
        new(PageId.General, "General", "Core behaviour for the Halo surface",
        [
            new("STARTUP", [
                Toggle("general.startup", "Start with Windows", "Launch Halo after you sign in"),
            ]),
            new("THE PILL", [
                Toggle("general.pinned", "Keep pinned", "Hold the pill above other windows, including fullscreen ones", false),
                Toggle("general.capture", "Include Halo in captures", "Show the pill in screenshots and recordings", false),
                Toggle("general.follow", "Follow the focused app", "Bring the surface belonging to the front window forward"),
                Action("general.reset", "Pill position", "Return the pill to the centre of the display", "Reset position"),
            ]),
        ]),
        new(PageId.Appearance, "Appearance", "Glass, colour and motion",
        [
            new("GLASS", [
                Choice("appearance.glass", "Glass strength", "Balance wallpaper detail against contrast",
                    "Balanced", "Light", "Balanced", "Strong"),
            ]),
            new("MOTION", [
                Choice("appearance.motion", "Motion", "How quickly the pill settles after it moves",
                    "Soft", "Reduced", "Soft", "Standard"),
            ]),
        ]),
        Feature(PageId.Media, "media", "Media", "Playback surfaces in the pill",
            Toggle("media.progress", "Show the timeline", "Draw the real playback position across the collapsed pill")),
        Feature(PageId.Downloads, "downloads", "Downloads", "Browser, store and game progress",
            Toggle("downloads.pulse", "Breathe while working", "Pulse the background when progress cannot be measured")),
        Feature(PageId.FileTray, "fileTray", "File Tray", "The drag-and-drop shelf and clipboard images"),
        Feature(PageId.Bluetooth, "bluetooth", "Bluetooth", "Connection and battery takeovers"),
        new(PageId.Notifications, "Notifications", "Windows toasts, mirrored into the pill",
        [
            new("MIRRORING", [
                Toggle("feature.notifications", "Mirror notifications", "Show Windows toasts in the pill"),
                Toggle("notifications.silence", "Silence the native banner",
                    "Stop Windows drawing its own banner for apps Halo mirrors. Fully reversible.", false),
            ]),
        ]),
        new(PageId.Alerts, "Alerts", "The banners Halo raises about this machine",
        [
            new("SYSTEM", [
                Toggle("alert.battery", "Battery", "Low at 20%, critical at 10%, with a tap to turn on Power Saver"),
                Toggle("alert.cpu", "High CPU", "Once per tier, naming the process using the most"),
                Toggle("alert.memory", "High memory", "Once per tier, naming the process using the most"),
                Toggle("alert.internet", "Internet", "Slow, offline, and the API being unreachable"),
            ]),
            new("AGENTS", [
                Toggle("alert.context", "Context nearly full", "Once per session, past 80%"),
                Toggle("alert.limit", "Usage limits", "Once per window, past 80%"),
            ]),
            new("GLANCE", [
                Toggle("alert.hourly", "Hourly chime", "On the hour, with the date and the sky", false),
                Toggle("alert.clipboard", "Screenshots and copies", "A banner when something lands on the clipboard"),
                Toggle("alert.language", "Keyboard layout", "A one-second glance when the layout flips"),
            ]),
        ]),
        new(PageId.ClaudeCode, "Claude Code", "Sessions, limits and the question banner",
        [
            new("SURFACE", [
                Toggle("feature.claudeCode", "Show Claude Code", "Let a live session take over the pill"),
            ]),
            new("QUESTIONS", [
                Toggle("claude.ask", "Answer from the pill",
                    "Mirror Claude's question box and answer it by clicking a row"),
            ]),
        ]),
        Feature(PageId.Codex, "codex", "Codex", "Codex Desktop and CLI sessions"),
        Feature(PageId.OtherAgents, "genericAgents", "Other agents", "Any tool writing ~/.halo/agents"),
        new(PageId.Access, "Access", "What Halo needs from Windows to do its job",
        [
            new("PERMISSIONS", [
                new("access.notifications", "Notification access", "Required to mirror Windows toasts",
                    RowKind.Status, "", [], "Open settings"),
                new("access.startup", "Startup entry", "The shortcut that launches Halo when you sign in",
                    RowKind.Status, "", [], "Open folder"),
            ]),
        ]),
    ];

    // 16x16 vector paths, from the approved preview. Drawn rather than set as text because an icon font
    // put unrelated symbols on half of these pages.
    internal static string Icon(PageId page) => page switch
    {
        PageId.General => "M2,4 L14,4 M5,2 L5,6 M2,12 L14,12 M11,10 L11,14",
        PageId.Appearance => "M8,1 L9.5,6.5 L15,8 L9.5,9.5 L8,15 L6.5,9.5 L1,8 L6.5,6.5 Z",
        PageId.Media => "M4,2 L14,8 L4,14 Z",
        PageId.Downloads => "M8,1 L8,10 M4,7 L8,11 L12,7 M2,14 L14,14",
        PageId.FileTray => "M1.5,4 L6,4 L7.5,6 L14.5,6 L14.5,14 L1.5,14 Z M1.5,4 L1.5,2.5 L6,2.5 L7.5,4",
        PageId.Bluetooth => "M6,2 L11,6 L6,10 L6,2 M6,6 L3,3 M6,6 L3,9 M6,10 L11,14 L6,14 L6,10",
        PageId.Notifications => "M3,11 L13,11 L11.5,9 L11.5,6 A3.5,3.5 0 0 0 4.5,6 L4.5,9 Z M6.5,13 A1.5,1.5 0 0 0 9.5,13",
        PageId.Alerts => "M8,1.5 L15,14 L1,14 Z M8,6 L8,10 M8,11.5 L8,12.5",
        PageId.ClaudeCode => "M2,3 L14,3 L14,13 L2,13 Z M4,6 L6.5,8 L4,10 M8,10 L11,10",
        PageId.Codex => "M6,2 C4,2 4,4 4,6 C4,7 3,8 2,8 C3,8 4,9 4,10 C4,12 4,14 6,14 M10,2 C12,2 12,4 12,6 C12,7 13,8 14,8 C13,8 12,9 12,10 C12,12 12,14 10,14",
        PageId.OtherAgents => "M3,3 A1.5,1.5 0 1 0 3,6 A1.5,1.5 0 1 0 3,3 M13,3 A1.5,1.5 0 1 0 13,6 A1.5,1.5 0 1 0 13,3 M8,10 A1.5,1.5 0 1 0 8,13 A1.5,1.5 0 1 0 8,10 M4.2,5.5 L7,10 M11.8,5.5 L9,10",
        _ => "M4,7 L12,7 L12,14 L4,14 Z M6,7 L6,5 A2,2 0 0 1 10,5 L10,7 M8,10 L8,12",
    };
}
