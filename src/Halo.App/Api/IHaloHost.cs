using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Halo.Api;

// What the API is allowed to ask the pill for. An interface rather than a bag of ten delegates because
// the list stopped being short: every new endpoint would have meant another constructor parameter
// threaded through NotchController's already-crowded setup.
//
// Split on purpose into reads and one Post. Reads happen on the listener's thread and must be cheap and
// tolerant of a half-updated frame - they are status, and a status read that took the render lock would
// let any local program stall the pill by polling it. Anything that CHANGES something goes through Post,
// which queues onto the frame loop, because the window, its GDI surfaces and the widget array are all
// owned by that thread and nothing else may touch them.
internal interface IHaloHost
{
    JsonObject State();
    JsonObject Media();
    JsonObject Agents();
    JsonObject Tray();
    JsonObject Settings();

    void Notify(NotifyRequest request);
    bool MediaControl(string action, int slot);
    bool Pill(string action);
    int TrayAdd(IReadOnlyList<string> paths);
    int SettingsPatch(JsonObject values);

    // queue work onto the render thread; returns false if the pill is shutting down
    bool Post(System.Action work);
}

internal sealed record NotifyRequest(
    string App, string Title, string Body, double Seconds, string Code, string LaunchPath);
