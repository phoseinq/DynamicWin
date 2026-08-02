using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Halo.Api;

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

    bool Post(System.Action work);
}

internal sealed record NotifyRequest(
    string App, string Title, string Body, double Seconds, string Code, string LaunchPath);
