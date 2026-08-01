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

    // Segoe Fluent Icons, not hand-drawn paths. The traced vectors were a stand-in and read as exactly
    // that beside the system's own shell; these are the same code points the approved design uses.
    internal static string Glyph(PageId page) => page switch
    {
        PageId.Home => "\uE80F",
        PageId.General => "\uE713",
        PageId.Features => "\uE71D",
        PageId.Agents => "\uE716",
        PageId.Access => "\uE8D7",
        _ => "\uE943",
    };

    // One accent per entry, so the rail is scannable by colour before it is read. Selection tints the
    // pill with the page's OWN colour rather than one blue for everything, which is what made six
    // different destinations look like six states of one thing.
    internal static (byte R, byte G, byte B) Accent(PageId page) => page switch
    {
        PageId.Home => (0x74, 0xE6, 0xC2),
        PageId.General => (0x7C, 0xB4, 0xFF),
        PageId.Features => (0xFF, 0x91, 0xC8),
        PageId.Agents => (0xD7, 0x9B, 0xFF),
        PageId.Access => (0xF0, 0xAE, 0x72),
        _ => (0x5F, 0xDF, 0xE5),
    };
}
