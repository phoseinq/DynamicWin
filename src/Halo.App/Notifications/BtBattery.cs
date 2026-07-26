using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace Halo.Notifications;

/// <summary>Reports the device the pill should feature.</summary>
/// <param name="id">Device identity. Names are not unique; ids are.</param>
/// <param name="flash">True only for a device that genuinely just arrived, which may briefly
/// take focus. False for one seeded at startup or promoted by a handoff.</param>
internal delegate void BtConnected(string id, string name, int pct, bool flash);

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

    /// <summary>
    /// Battery() runs PnpObject.FindAllAsync, which enumerates every device on the machine.
    /// The timer still ticks every minute, but the work behind it is gated on the widget being
    /// genuinely on screen (see the shouldRefresh callback), so an idle desktop pays nothing at
    /// all and the enumeration only happens while somebody is actually reading the number.
    /// </summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(60);

    /// <summary>Synthetic id for the bt-test.txt preview device, so it can be cleared like a real one.</summary>
    private const string TriggerId = "halo:bt-test";

    private readonly BtConnected _onConnect;
    private readonly Action<string> _onDisconnect;
    private readonly Action<string, int> _onBatteryRefresh;
    private readonly Func<bool> _shouldRefresh;
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

    public BtBattery(BtConnected onConnect, Action<string> onDisconnect,
        Action<string, int> onBatteryRefresh, Func<bool> shouldRefresh)
    {
        _onConnect = onConnect;
        _onDisconnect = onDisconnect;
        _onBatteryRefresh = onBatteryRefresh;
        _shouldRefresh = shouldRefresh;
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

    /// <summary>
    /// bt-test.txt shows a fake device for the README preview image. A fake device never
    /// produces a Removed event, so with the widget no longer clearing itself on a timer the
    /// trigger has to be able to take it back: writing an empty file (or "clear") removes it.
    /// </summary>
    private void PollTrigger()
    {
        try
        {
            if (!System.IO.File.Exists(TriggerPath)) return;
            var line = System.IO.File.ReadAllText(TriggerPath).Trim();
            System.IO.File.Delete(TriggerPath);
            var parts = line.Split('|');
            string name = parts[0].Trim();
            if (name.Length == 0 || string.Equals(name, "clear", StringComparison.OrdinalIgnoreCase))
            {
                Log("trigger: clear");
                lock (_lock) { _names.Remove(TriggerId); _order.Remove(TriggerId); }
                _onDisconnect(TriggerId);
                return;
            }
            int pct = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var p) ? p : 80;
            Log($"trigger: {name} {pct}%");
            TrackConnected(TriggerId, name);
            lock (_lock) { _featuredId = TriggerId; _featuredName = name; }
            _onConnect(TriggerId, name, pct, flash: true);
        }
        catch { }
    }

    private async void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        try
        {
            string name = info.Name?.Length > 0 ? info.Name : "Bluetooth device";
            // Devices already connected when Halo started are seeded rather than announced: they
            // are shown like any other, but they did not just arrive, so they must not grab focus.
            // The old code returned here instead, which is why a device connected before login
            // never appeared at all -- and the app starts with Windows, so that was the normal case.
            bool seeded = !_live;
            TrackConnected(info.Id, name);
            Log(seeded ? $"seed (already connected): {name}" : $"connected: {name}");

            int pct = await Battery(name);
            if (pct < 0) { await Task.Delay(2500); pct = await Battery(name); }

            // That read can take seconds. If the device disconnected while we waited, OnRemoved has
            // already run, found _featuredId still unset, and decided this was not the featured
            // device -- so nothing cleared it. Re-check the connected set before featuring, or the
            // pill would show a device that is gone with no event left to remove it.
            lock (_lock)
            {
                if (!_names.ContainsKey(info.Id)) { Log($"vanished during read: {name}"); return; }
            }
            if (pct < 0) { Log($"no battery reading: {name}"); return; }

            lock (_lock) { _featuredId = info.Id; _featuredName = name; }
            Log($"featured: {name} pct={pct}");
            _onConnect(info.Id, name, pct, flash: !seeded);
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
                // Same race as OnAdded: the candidate may have gone while its battery was read.
                lock (_lock)
                {
                    if (!_names.ContainsKey(id)) continue;
                    _featuredId = id; _featuredName = candName;
                }
                Log($"handoff: {candName} pct={pct}");
                // A handoff is a fallback, not an arrival: show it, but do not grab focus for it.
                _onConnect(id, candName, pct, flash: false);
                return;
            }

            lock (_lock) { _featuredId = null; _featuredName = null; }
            _onDisconnect(update.Id);
        }
        catch (Exception ex) { Log("removed handler failed: " + ex.Message); }
    }

    private async Task RefreshFeatured()
    {
        // Skip the whole-machine device enumeration when nobody is looking at the widget.
        if (!_shouldRefresh()) return;
        string? id, name;
        lock (_lock) { id = _featuredId; name = _featuredName; }
        if (id is null || name is null || id == TriggerId) return;
        int pct = await Battery(name);
        if (pct < 0) return;
        // The featured device may have changed while the read was in flight.
        lock (_lock) { if (_featuredId != id) return; }
        _onBatteryRefresh(id, pct);
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
