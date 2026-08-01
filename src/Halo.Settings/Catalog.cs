using System.Collections.Generic;

namespace Halo.Settings;

internal enum PageId { Home, General, Features, Agents, Access, DocsAbout }

internal enum RowKind { Toggle, Choice, Slider, Action, Status }

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

// Six entries under three headers, not the twelve-page flat rail the first build copied. Eleven feature
// pages that each held one toggle were eleven clicks to find one switch; they are one Features page with
// the switches grouped, and the three agent pages are one Agents page.
internal sealed record NavGroup(string Header, IReadOnlyList<PageId> Pages);

// Every row here is a setting the pill actually reads. A control the underlying thing cannot honour is
// the same mistake as an invented number, and this project has rejected that twice.
internal static class Catalog
{
    private static Row Toggle(string key, string label, string description, bool on = true)
        => new(key, label, description, RowKind.Toggle, on ? "on" : "off", ["off", "on"]);

    private static Row Choice(string key, string label, string description, string fallback, params string[] options)
        => new(key, label, description, RowKind.Choice, fallback, options);

    private static Row Slider(string key, string label, string description, string fallback, params string[] stops)
        => new(key, label, description, RowKind.Slider, fallback, stops);

    private static Row Action(string key, string label, string description, string action)
        => new(key, label, description, RowKind.Action, "", [], action);

    internal static readonly NavGroup[] Nav =
    [
        new("", [PageId.Home]),
        new("SETTINGS", [PageId.General, PageId.Features, PageId.Agents]),
        new("SYSTEM", [PageId.Access]),
        new("REFERENCE", [PageId.DocsAbout]),
    ];

    internal static readonly Page[] Pages =
    [
        new(PageId.Home, "Home", "The switches you reach for most",
        [
            new("QUICK", [
                Toggle("general.startup", "Start with Windows", "Launch Halo after you sign in"),
                Toggle("general.pinned", "Keep pinned", "Hold the pill above other windows, including fullscreen ones", false),
                Action("general.reset", "Pill position", "Return the pill to the centre of the display", "Reset position"),
            ]),
        ]),
        new(PageId.General, "General", "Core behaviour and appearance for the Halo surface",
        [
            new("APPEARANCE", [
                Slider("appearance.scale", "Pill scale", "Scale geometry, type and hit targets together",
                    "100%", "90%", "95%", "100%", "105%", "110%"),
                Choice("appearance.glass", "Glass strength", "Balance wallpaper detail against contrast",
                    "Balanced", "Light", "Balanced", "Strong"),
                Choice("appearance.motion", "Motion", "How quickly the pill settles after it moves",
                    "Soft", "Reduced", "Soft", "Standard"),
            ]),
            new("STARTUP", [
                Toggle("general.startup", "Start with Windows", "Launch Halo after you sign in"),
                Toggle("general.pinned", "Stay visible over fullscreen", "Keep the pill above games and video", false),
            ]),
            new("BEHAVIOUR", [
                Toggle("general.capture", "Include Halo in captures", "Show the pill in screenshots and recordings", false),
                Toggle("general.follow", "Follow focused apps", "Bring the relevant surface forward automatically"),
                Action("general.reset", "Pill position", "Return the pill to the active display centre", "Reset position"),
            ]),
        ]),
        new(PageId.Features, "Features", "What the pill is allowed to show, and when",
        [
            new("SURFACES", [
                Toggle("feature.media", "Media", "Playback sessions and classic VLC controls"),
                Toggle("media.progress", "Show the timeline", "Draw the real playback position across the collapsed pill"),
                Toggle("feature.downloads", "Downloads", "Browser, store, game and app progress"),
                Toggle("feature.fileTray", "File Tray", "The drag-and-drop shelf and clipboard images"),
                Toggle("feature.bluetooth", "Bluetooth", "Connection and battery takeovers"),
            ]),
            new("NOTIFICATIONS", [
                Toggle("feature.notifications", "Mirror notifications", "Show Windows toasts in the pill"),
                Toggle("notifications.silence", "Silence the native banner",
                    "Stop Windows drawing its own banner for apps Halo mirrors. Fully reversible.", false),
            ]),
            new("ALERTS ABOUT THIS MACHINE", [
                Toggle("alert.battery", "Battery", "Low at 20%, critical at 10%, with a tap to turn on Power Saver"),
                Toggle("alert.cpu", "High CPU", "Once per tier, naming the process using the most"),
                Toggle("alert.memory", "High memory", "Once per tier, naming the process using the most"),
                Toggle("alert.internet", "Internet", "Slow, offline, and the API being unreachable"),
                Toggle("alert.clipboard", "Screenshots and copies", "A banner when something lands on the clipboard"),
                Toggle("alert.language", "Keyboard layout", "A one-second glance when the layout flips"),
                Toggle("alert.hourly", "Hourly chime", "On the hour, with the date and the sky", false),
            ]),
        ]),
        new(PageId.Agents, "Agents", "Claude Code, Codex, and anything else that reports in",
        [
            new("SESSIONS", [
                Toggle("feature.claudeCode", "Claude Code", "Live sessions, limits and the cancel button"),
                Toggle("feature.codex", "Codex", "Codex Desktop and CLI sessions"),
                Toggle("feature.genericAgents", "Other agents", "Any tool writing ~/.halo/agents"),
            ]),
            new("QUESTIONS", [
                Toggle("claude.ask", "Answer from the pill",
                    "Mirror Claude's question box and answer it by clicking a row"),
            ]),
            new("ALERTS", [
                Toggle("alert.context", "Context nearly full", "Once per session, past 80%"),
                Toggle("alert.limit", "Usage limits", "Once per window, past 80%"),
            ]),
        ]),
        new(PageId.Access, "Access", "What Halo needs from Windows to do its job",
        [
            new("PERMISSIONS", [
                new("access.notifications", "Notification access", "Required to mirror Windows toasts",
                    RowKind.Status, "", [], "Open settings"),
                new("access.startup", "Startup entry", "The shortcut that launches Halo when you sign in",
                    RowKind.Status, "", [], "Open folder"),
            ]),
        ]),
        new(PageId.DocsAbout, "Docs & About", "Where things are written down",
        [
            new("HALO", [
                new("about.version", "Version", "", RowKind.Status, "", []),
                new("about.state", "State folder", "Loose files Halo keeps: position, pin, tray, seen notifications",
                    RowKind.Status, "", [], "Open folder"),
            ]),
            new("PROJECT", [
                new("about.repo", "Repository", "github.com/phoseinq/DynamicWin", RowKind.Status, "", [], "Open"),
            ]),
        ]),
    ];

    internal static Page Get(PageId id) => System.Array.Find(Pages, p => p.Id == id)!;

    // 16x16 vector paths. Drawn rather than set as text, because an icon font put unrelated symbols on
    // half of these entries.
    internal static string Icon(PageId page) => page switch
    {
        PageId.Home => "M2,7.5 L8,2 L14,7.5 M3.5,6.5 L3.5,14 L12.5,14 L12.5,6.5 M6.5,14 L6.5,9.5 L9.5,9.5 L9.5,14",
        PageId.General => "M2,4 L14,4 M5,2 L5,6 M2,12 L14,12 M11,10 L11,14",
        PageId.Features => "M2,3.5 L14,3.5 M2,8 L14,8 M2,12.5 L14,12.5",
        PageId.Agents => "M3,3 A1.5,1.5 0 1 0 3,6 A1.5,1.5 0 1 0 3,3 M13,3 A1.5,1.5 0 1 0 13,6 A1.5,1.5 0 1 0 13,3 M8,10 A1.5,1.5 0 1 0 8,13 A1.5,1.5 0 1 0 8,10 M4.2,5.5 L7,10 M11.8,5.5 L9,10",
        PageId.Access => "M4,7 L12,7 L12,14 L4,14 Z M6,7 L6,5 A2,2 0 0 1 10,5 L10,7 M8,10 L8,12",
        _ => "M5,2 C3.5,2 3.5,4 3.5,8 C3.5,12 3.5,14 5,14 M11,2 C12.5,2 12.5,4 12.5,8 C12.5,12 12.5,14 11,14",
    };
}
