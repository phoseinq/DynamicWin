using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace Halo.Notifications;

/// <summary>
/// Tracks connected Bluetooth devices and reports the "featured" one (the most
/// recently connected device we could read a battery level for) to the pill.
/// If the featured device disconnects, the next-most-recent connected device
/// with a readable battery takes over instead of the widget just going blank.
/// </summary>
internal sealed class BtBattery
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string NameKey = "System.ItemNameDisplay";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(60);

    private readonly Action<string, int> _onConnect;
    private readonly Action<string> _onDisconnect;
    private readonly Action<int> _onBatteryRefresh;
    private DeviceWatcher? _watcher;
    private volatile bool _live;
    private System.Threading.Timer? _trigger;
    private System.Threading.Timer? _refresh;

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _names = new();
    private readonly List<string> _order = new();
    private string? _featuredId;
    private string? _featuredName;

    private static readonly string TriggerPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "bt-test.txt");

    private static readonly string DebugPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "bt-debug.txt");
    private static void Log(string m) { try { System.IO.File.AppendAllText(DebugPath, $"{DateTime.Now:HH:mm:ss} {m}\r\n"); } catch { } }

    public BtBattery(Action<string, int> onConnect, Action<string> onDisconnect, Action<int> onBatteryRefresh)
    {
        _onConnect = onConnect;
        _onDisconnect = onDisconnect;
        _onBatteryRefresh = onBatteryRefresh;
        try
        {
            string sel = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _watcher = DeviceInformation.CreateWatcher(sel, new[] { NameKey }, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += OnAdded;
            _watcher.Removed += OnRemoved;
            _watcher.Updated += (_, u) => Log($"updated: {u.Id}");
            _watcher.EnumerationCompleted += (_, __) => { _live = true; Log("enumeration complete — live"); };
            _watcher.Start();
            Log("watcher started");
        }
        catch (Exception ex) { Log("start failed: " + ex.Message); }

        _trigger = new System.Threading.Timer(_ => PollTrigger(), null, 1000, 1000);
        _refresh = new System.Threading.Timer(_ => _ = RefreshFeatured(), null, RefreshEvery, RefreshEvery);
    }

    private void PollTrigger()
    {
        try
        {
            if (!System.IO.File.Exists(TriggerPath)) return;
            var line = System.IO.File.ReadAllText(TriggerPath).Trim();
            System.IO.File.Delete(TriggerPath);
            var parts = line.Split('|');
            string name = parts[0].Trim();
            int pct = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var p) ? p : 80;
            if (name.Length > 0) { Log($"trigger: {name} {pct}%"); _onConnect(name, pct); }
        }
        catch { }
    }

    private async void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        try
        {
            string name = info.Name?.Length > 0 ? info.Name : "Bluetooth device";
            if (!_live) { Log($"seed (already connected): {name}"); TrackConnected(info.Id, name); return; }

            Log($"connected: {name}");
            TrackConnected(info.Id, name);

            int pct = await Battery(name);
            if (pct < 0) { await Task.Delay(2500); pct = await Battery(name); }
            Log($"featured: {name} pct={pct}");
            if (pct < 0) return;

            lock (_lock) { _featuredId = info.Id; _featuredName = name; }
            _onConnect(name, pct);
        }
        catch (Exception ex) { Log("added failed: " + ex.Message); }
    }

    private void TrackConnected(string id, string name)
    {
        lock (_lock)
        {
            _names[id] = name;
            if (!_order.Contains(id)) _order.Add(id);
        }
    }

    private async void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try
        {
            string? name;
            bool wasFeatured;
            lock (_lock)
            {
                if (!_names.TryGetValue(update.Id, out name)) return;
                _names.Remove(update.Id);
                _order.Remove(update.Id);
                wasFeatured = update.Id == _featuredId;
            }
            Log($"removed (disconnected): {name}");
            if (!wasFeatured) return;

            // The featured device dropped -- try to hand off to another still-connected device.
            List<(string id, string name)> candidates;
            lock (_lock)
            {
                candidates = new List<(string, string)>();
                for (int i = _order.Count - 1; i >= 0; i--)
                    candidates.Add((_order[i], _names[_order[i]]));
            }
            foreach (var (id, candName) in candidates)
            {
                int pct = await Battery(candName);
                if (pct < 0) continue;
                lock (_lock) { _featuredId = id; _featuredName = candName; }
                Log($"handoff: {candName} pct={pct}");
                _onConnect(candName, pct);
                return;
            }

            lock (_lock) { _featuredId = null; _featuredName = null; }
            _onDisconnect(name!);
        }
        catch (Exception ex) { Log("removed handler failed: " + ex.Message); }
    }

    private async Task RefreshFeatured()
    {
        string? name;
        lock (_lock) { name = _featuredName; }
        if (name is null) return;
        int pct = await Battery(name);
        if (pct >= 0) _onBatteryRefresh(pct);
    }

    private static async Task<int> Battery(string name)
    {
        try
        {
            var objs = await PnpObject.FindAllAsync(PnpObjectType.Device, new[] { NameKey, BatteryKey });
            int best = -1;
            foreach (var o in objs)
            {
                if (!o.Properties.TryGetValue(BatteryKey, out var bv) || bv == null) continue;
                int pct = bv switch { byte b => b, int i => i, sbyte sb => sb, _ => -1 };
                if (pct < 0 || pct > 100) continue;
                if (!o.Properties.TryGetValue(NameKey, out var nv) || nv is not string s) continue;
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return pct;
                if (best < 0 && (name.Contains(s, StringComparison.OrdinalIgnoreCase)
                    || s.Contains(name, StringComparison.OrdinalIgnoreCase))) best = pct;
            }
            return best;
        }
        catch { return -1; }
    }
}
