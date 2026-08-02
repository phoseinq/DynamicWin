using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Halo.Api;
using Halo.ClaudeCode;
using Halo.Widgets;

namespace Halo.Shell;

// NotchController's side of the local API, in its own file because the controller is already the biggest
// class here and none of this belongs in the frame loop.
//
// The split the interface describes is enforced here: every read below is a snapshot of things that are
// already published for other threads to look at, and every mutation goes through Post onto the render
// thread. A read that took the render lock would let any local program stall the pill by polling it, and
// a write that did not take the render thread would be touching the widget array and the GDI surfaces
// from a socket handler.
internal sealed partial class NotchController : IHaloHost
{
    private readonly ConcurrentQueue<Action> _posted = new();

    public bool Post(Action work)
    {
        _posted.Enqueue(work);
        return true;
    }

    // Drained once per frame rather than once a second: this is the path a "next track" or "expand" comes
    // in on, and a second of latency on a remote control is the difference between working and broken.
    private void DrainPosted()
    {
        int budget = 16;   // a caller in a loop must not be able to starve the frame it is running inside
        while (budget-- > 0 && _posted.TryDequeue(out var work))
        {
            try { work(); } catch { }
        }
    }

    public JsonObject State()
    {
        var state = new JsonObject();
        try
        {
            state["expanded"] = _progress > 0.5f;
            state["pinned"] = Pinned(_pinned);
            state["inCaptures"] = _recordable;
            state["scale"] = Math.Round(_notch.Scale, 3);
            state["offsetX"] = Math.Round(_offsetX, 1);
            state["empty"] = _empty;
            state["micInUse"] = Privacy.Mic;
            state["cameraInUse"] = Privacy.Cam;

            var widgets = new JsonArray();
            foreach (var i in ActiveIndices())
                widgets.Add(new JsonObject
                {
                    ["kind"] = _widgets[i].GetType().Name,
                    ["primary"] = i == _primary,
                });
            state["active"] = widgets;

            var banner = _notif;
            state["banner"] = banner is null
                ? null
                : new JsonObject { ["app"] = banner.App, ["title"] = banner.Title, ["body"] = banner.Body };

            var ask = _ask;
            state["question"] = ask is null ? null : new JsonObject
            {
                ["nonce"] = ask.Nonce,
                ["question"] = ask.Question,
                ["options"] = Labels(ask),
            };
        }
        catch { }
        return state;
    }

    private static JsonArray Labels(PendingAsk ask)
    {
        var labels = new JsonArray();
        foreach (var option in ask.Options) labels.Add(option.Label);
        return labels;
    }

    public JsonObject Media()
    {
        var slots = new JsonArray();
        try
        {
            for (int slot = 0; slot < MediaSessions.MaxSlots; slot++)
            {
                string app = _mediaSessions.SlotApp(slot);
                if (app.Length == 0) continue;
                var widget = Find<MediaWidget>(w => w.Slot == slot);
                slots.Add(new JsonObject
                {
                    ["slot"] = slot,
                    ["app"] = app,
                    ["title"] = widget?.TitleText,
                    ["artist"] = widget?.ArtistText,
                    ["playing"] = widget?.Playing ?? false,
                    // -1 rather than a made-up 0: a live stream has no position to report and neither does
                    // a player that has not published a timeline yet
                    ["progress"] = Math.Round(widget?.RingProgress ?? -1f, 4),
                });
            }
        }
        catch { }
        return new JsonObject { ["sessions"] = slots };
    }

    public JsonObject Agents()
    {
        var sessions = new JsonArray();
        try
        {
            for (int slot = 0; slot < StatusStore.MaxSessions; slot++)
                if (_claudeStore.SessionLive(slot) is { } cc)
                    sessions.Add(Agent("claude", cc));
            // Codex has its own snapshot type rather than sharing CcStatus - the two modules are mirrors,
            // not one implementation - so it is described by hand instead of forced through Agent().
            if (_codexStore.Current is { } codex)
                sessions.Add(new JsonObject
                {
                    ["kind"] = "codex",
                    ["state"] = codex.State,
                    ["tool"] = codex.CurrentTool,
                    ["cwd"] = codex.Cwd,
                    ["pid"] = codex.Pid,
                });
        }
        catch { }
        return new JsonObject
        {
            ["sessions"] = sessions,
            ["claudeFiveHourPct"] = Pct(Halo.ClaudeCode.Limits.FiveHour),
            ["claudeWeeklyPct"] = Pct(Halo.ClaudeCode.Limits.Week),
        };
    }

    // Limits carries -1 for "not known yet", and multiplying that by 100 handed callers a confident
    // -100%. Null is the honest answer, and it is the same rule the pill keeps on its own surfaces: a
    // figure nobody can obtain is shown as absent, never as a number.
    private static JsonNode? Pct(double fraction)
        => fraction < 0 ? null : JsonValue.Create(Math.Round(fraction * 100, 1));

    private static JsonObject Agent(string kind, CcStatus status) => new()
    {
        ["kind"] = kind,
        ["name"] = status.Name,
        ["state"] = status.State,
        ["tool"] = status.CurrentTool,
        ["target"] = status.ToolTarget,
        ["cwd"] = status.Cwd,
        ["pid"] = status.Pid,
    };

    public JsonObject Tray()
    {
        var files = new JsonArray();
        try { foreach (var path in FileTray.Paths()) files.Add(path); } catch { }
        return new JsonObject { ["files"] = files };
    }

    public JsonObject Settings()
    {
        var values = new JsonObject();
        try { foreach (var (key, value) in _settings.Current.Values) values[key] = value; } catch { }
        return new JsonObject { ["values"] = values };
    }

    public void Notify(NotifyRequest request)
    {
        try
        {
            _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
            {
                App = request.App,
                Title = request.Title,
                Body = request.Body,
                Code = request.Code,
                LaunchPath = request.LaunchPath,
                Kind = "api",
                Duration = Math.Clamp(request.Seconds, 2, 30),
            });
        }
        catch { }
    }

    // Transport goes to SMTC directly rather than through the widget's own buttons: the widget's handlers
    // are wired to hit-tested rectangles and a caller has no rectangle.
    public bool MediaControl(string action, int slot)
    {
        try
        {
            var session = _mediaSessions.Session(slot < 0 ? PrimaryMediaSlot() : slot);
            if (session is null) return false;
            switch (action)
            {
                case "play": _ = session.TryPlayAsync(); return true;
                case "pause": _ = session.TryPauseAsync(); return true;
                case "toggle": _ = session.TryTogglePlayPauseAsync(); return true;
                case "next": _ = session.TrySkipNextAsync(); return true;
                case "previous": _ = session.TrySkipPreviousAsync(); return true;
                default: return false;
            }
        }
        catch { return false; }
    }

    private int PrimaryMediaSlot()
    {
        if (!_empty && _widgets[_primary] is MediaWidget primary) return primary.Slot;
        for (int slot = 0; slot < MediaSessions.MaxSlots; slot++)
            if (_mediaSessions.SlotApp(slot).Length > 0) return slot;
        return 0;
    }

    public bool Pill(string action)
    {
        switch (action)
        {
            case "expand": return Post(() => { _apiHold = true; });
            case "collapse": return Post(() => { _apiHold = false; });
            case "pin": return Post(() => { if (!_pinned) { _pinned = true; SavePin(); } });
            case "unpin": return Post(() => { if (_pinned) { _pinned = false; SavePin(); } });
            case "recenter": return Post(() => { _offsetX = 0; SaveOffset(); });
            default: return false;
        }
    }

    // Held open by the API rather than faked as a hover: hover is recomputed from the real cursor every
    // frame, so writing it would be overwritten within 8ms.
    private bool _apiHold;

    public int TrayAdd(IReadOnlyList<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) continue;
                FileTray.Add(path);
                added++;
            }
            catch { }
        }
        return added;
    }

    public int SettingsPatch(JsonObject values)
    {
        int written = 0;
        foreach (var (key, node) in values)
        {
            try
            {
                if (node is not JsonValue value) continue;
                string text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"');
                if (_settings.Set(key, text)) written++;
            }
            catch { }
        }
        return written;
    }

    private T? Find<T>(Func<T, bool> match) where T : class, IWidget
    {
        foreach (var widget in _widgets)
            if (widget is T typed && match(typed)) return typed;
        return null;
    }
}
