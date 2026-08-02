using System.Collections.Generic;

namespace Halo.Settings;

internal enum PageId { Home, General, Features, Agents, Api, Access, DocsAbout }

internal enum RowKind { Toggle, Choice, Slider, Action, Status }

internal sealed record Row(
    string Key,
    string Label,
    string Description,
    RowKind Kind,
    string Fallback,
    IReadOnlyList<string> Options,
    string ActionLabel = "");

internal sealed record Section(string Label, string Glyph, IReadOnlyList<Row> Rows);

internal sealed record Page(PageId Id, string Label, string Description, IReadOnlyList<Section> Sections);

internal sealed record NavGroup(string Header, IReadOnlyList<PageId> Pages);

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
        new("SYSTEM", [PageId.Api, PageId.Access]),
        new("REFERENCE", [PageId.DocsAbout]),
    ];

    internal static readonly Page[] Pages =
    [

        new(PageId.Home, "Home", "A quieter place to begin", []),
        new(PageId.General, "General", "Core behaviour and appearance for the Halo surface",
        [
            new("APPEARANCE", "\uE790", [
                Slider("appearance.scale", "Pill scale", "Scale geometry, type and hit targets together",
                    "100%", "90%", "95%", "100%", "105%", "110%"),
                Choice("appearance.glass", "Glass strength", "Balance wallpaper detail against contrast",
                    "Balanced", "Light", "Balanced", "Strong"),
                Choice("appearance.motion", "Motion", "How quickly the pill settles after it moves",
                    "Soft", "Reduced", "Soft", "Standard"),
            ]),
            new("STARTUP", "\uE7E8", [
                Toggle("general.startup", "Start with Windows", "Launch Halo after you sign in"),
                Toggle("general.fullscreen", "Stay visible over fullscreen", "Keep the pill above games and video", false),
            ]),
            new("BEHAVIOUR", "\uE945", [
                Toggle("general.capture", "Include Halo in captures", "Show the pill in screenshots and recordings", false),
                Toggle("general.follow", "Follow focused apps", "Bring the relevant surface forward automatically"),
                Action("general.reset", "Pill position", "Return the pill to the active display centre", "Reset position"),
            ]),
        ]),
        new(PageId.Features, "Features", "What the pill is allowed to show, and when",
        [
            new("SURFACES", "\uE7F4", [
                Toggle("feature.media", "Media", "Playback sessions and classic VLC controls"),
                Toggle("media.progress", "Show the timeline", "Draw the real playback position across the collapsed pill"),
                Toggle("feature.downloads", "Downloads", "Browser, store, game and app progress"),
                Toggle("feature.fileTray", "File Tray", "The drag-and-drop shelf and clipboard images"),
                Toggle("feature.bluetooth", "Bluetooth", "Connection and battery takeovers"),
            ]),
            new("NOTIFICATIONS", "\uE8BD", [
                Toggle("feature.notifications", "Mirror notifications", "Show Windows toasts in the pill"),
                Toggle("notifications.silence", "Silence the native banner",
                    "Stop Windows drawing its own banner for apps Halo mirrors. Fully reversible.", false),
            ]),
            new("ALERTS ABOUT THIS MACHINE", "\uE9D9", [
                Toggle("alert.battery", "Battery", "With a tap to turn on Power Saver. 10% is always critical."),
                Slider("alert.batteryAt", "Warn me at", "Where the first battery warning fires",
                    "20%", "10%", "15%", "20%", "25%", "30%", "40%"),
                Toggle("alert.cpu", "High CPU", "Once per tier, naming the process using the most"),
                Slider("alert.cpuAt", "Start warning at", "Higher tiers above this one still escalate",
                    "50%", "40%", "50%", "60%", "70%", "80%", "90%"),
                Toggle("alert.memory", "High memory", "Once per tier, naming the process using the most"),
                Slider("alert.memoryAt", "Start warning at", "Higher tiers above this one still escalate",
                    "70%", "50%", "60%", "70%", "80%", "90%"),
                Toggle("alert.internet", "Internet", "Slow, offline, and the API being unreachable"),
                Toggle("alert.clipboard", "Screenshots and copies", "A banner when something lands on the clipboard"),
                Toggle("alert.language", "Keyboard layout", "A one-second glance when the layout flips"),
                Toggle("alert.hourly", "Hourly chime", "On the hour, with the date and the sky", false),
            ]),
        ]),
        new(PageId.Agents, "Agents", "Claude Code, Codex, and anything else that reports in",
        [
            new("SESSIONS", "\uE716", [
                Toggle("feature.claudeCode", "Claude Code", "Live sessions, limits and the cancel button"),
                Toggle("feature.codex", "Codex", "Codex Desktop and CLI sessions"),
                Toggle("feature.genericAgents", "Other agents", "Any tool writing ~/.halo/agents"),
            ]),
            new("QUESTIONS", "\uE9CE", [
                Toggle("claude.ask", "Answer from the pill",
                    "Mirror Claude's question box and answer it by clicking a row"),
            ]),
            new("ALERTS", "\uEA80", [
                Toggle("alert.context", "Context nearly full", "Once per session"),
                Slider("alert.contextAt", "Context warning at", "Also where the agent ring turns amber",
                    "80%", "60%", "70%", "75%", "80%", "85%", "90%"),
                Toggle("alert.limit", "Usage limits", "Once per window"),
                Slider("alert.limitAt", "Usage warning at", "Share of a five-hour or weekly window",
                    "80%", "60%", "70%", "80%", "90%", "95%"),
            ]),
        ]),
        new(PageId.Api, "API", "Let other programs drive the pill",
        [
            new("ENDPOINT", "\uE968", [
                Toggle("api.enabled", "Local API", "Listen on 127.0.0.1 for local programs. Nothing off this machine can reach it.", false),
                Choice("api.port", "Port", "Change it only if something else already has this one",
                    "7317", "7317", "7318", "8317", "9317"),
                new("api.token", "Token", "Generated when you first switch the API on. Send it as an Authorization: Bearer header.",
                    RowKind.Status, "", [], "Copy"),
            ]),
            new("WHAT CALLERS MAY DO", "\uE8D7", [
                Toggle("api.notify", "Post a notification", "POST /notify - title, body, and an optional code or file to open"),
                Toggle("api.ask", "Ask a question", "POST /ask with options, then poll /ask/{nonce} for the answer"),
                Toggle("api.state", "Read what is on screen", "GET /state, /media, /agents, /tray", false),
                Toggle("api.control", "Press buttons", "POST /media, /pill and /tray: play, skip, expand, pin, add files", false),
                Toggle("api.settings", "Read and change settings", "GET and PATCH /settings. This is every switch in this window.", false),
            ]),
        ]),
        new(PageId.Access, "Access", "What Halo needs from Windows to do its job",
        [
            new("PERMISSIONS", "\uE890", [
                new("access.notifications", "Notification access", "Required to mirror Windows toasts",
                    RowKind.Status, "", [], "Open settings"),
                new("access.startup", "Startup entry", "The shortcut that launches Halo when you sign in",
                    RowKind.Status, "", [], "Open folder"),
            ]),
        ]),
        new(PageId.DocsAbout, "Docs & About", "Where things are written down",
        [
            new("HALO", "\uE946", [
                new("about.version", "Version", "", RowKind.Status, "", []),
                new("about.state", "State folder", "Loose files Halo keeps: position, pin, tray, seen notifications",
                    RowKind.Status, "", [], "Open folder"),
            ]),
            new("PROJECT", "\uE943", [
                new("about.repo", "Repository", "github.com/phoseinq/DynamicWin", RowKind.Status, "", [], "Open"),
            ]),
        ]),
    ];

    internal static Page Get(PageId id) => System.Array.Find(Pages, p => p.Id == id)!;

    internal static readonly PageId[] HomeShortcuts =
        [PageId.General, PageId.Features, PageId.Agents, PageId.Access];

    internal static string Sub(PageId page) => page switch
    {
        PageId.General => "Behaviour and appearance",
        PageId.Features => "App surfaces",
        PageId.Agents => "Coding sessions",
        PageId.Api => "Drive Halo from code",
        _ => "Windows controls",
    };

    internal static string Glyph(PageId page) => page switch
    {
        PageId.Home => "\uE80F",
        PageId.General => "\uE713",
        PageId.Features => "\uE71D",
        PageId.Agents => "\uE716",
        PageId.Api => "\uE968",
        PageId.Access => "\uE8D7",
        _ => "\uE943",
    };

    internal static (byte R, byte G, byte B) Accent(PageId page) => page switch
    {
        PageId.Home => (0x74, 0xE6, 0xC2),
        PageId.General => (0x7C, 0xB4, 0xFF),
        PageId.Features => (0xFF, 0x91, 0xC8),
        PageId.Agents => (0xD7, 0x9B, 0xFF),
        PageId.Api => (0x8B, 0xE0, 0xC8),
        PageId.Access => (0xF0, 0xAE, 0x72),
        _ => (0x5F, 0xDF, 0xE5),
    };
}
