using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace Halo.Notifications;

// Bluetooth battery banner: when a device connects, the pill shows "<name> · <pct>% battery".
// Event-driven: a DeviceWatcher on the "connected" selector fires Added the instant a paired device
// actually connects (and Removed when it drops) — off the real BT-stack state, unlike the IsConnected
// PnP devkey which stays stale on a quick toggle so polling never sees the reconnect. Battery % is read
// from DEVPKEY_Bluetooth_Battery (verified to read the real value), a moment after connect so the HFP
// battery service has reported.
internal sealed class BtBattery
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery
    private const string NameKey = "System.ItemNameDisplay";

    private readonly Action<string, int> _onConnect;
    private DeviceWatcher? _watcher; // field: keep it alive, and it's the connect event source
    private volatile bool _live;     // initial enumeration done → Added now means a fresh connect, not startup state
    private System.Threading.Timer? _trigger; // field: demo trigger poll (see below)

    // demo/test hook: writing "<name>|<pct>" into this file fires the banner on command. A phone toggle
    // doesn't register as a Windows disconnect (verified), so the real watcher can't be driven from the
    // phone — this makes the banner demoable. ponytail: tiny file poll, delete-on-read.
    private static readonly string TriggerPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "bt-test.txt");

    private static readonly string DebugPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "bt-debug.txt");
    private static void Log(string m) { try { System.IO.File.AppendAllText(DebugPath, $"{DateTime.Now:HH:mm:ss} {m}\r\n"); } catch { } }

    public BtBattery(Action<string, int> onConnect)
    {
        _onConnect = onConnect;
        try
        {
            string sel = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _watcher = DeviceInformation.CreateWatcher(sel, new[] { NameKey }, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += OnAdded;
            _watcher.Removed += (_, u) => Log($"removed (disconnected): {u.Id}");
            _watcher.Updated += (_, u) => Log($"updated: {u.Id}");
            _watcher.EnumerationCompleted += (_, __) => { _live = true; Log("enumeration complete — live"); };
            _watcher.Start();
            Log("watcher started");
        }
        catch (Exception ex) { Log("start failed: " + ex.Message); }

        _trigger = new System.Threading.Timer(_ => PollTrigger(), null, 1000, 1000);
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
            if (!_live) { Log($"seed (already connected): {info.Name}"); return; } // startup state, don't banner
            string name = info.Name?.Length > 0 ? info.Name : "Bluetooth device";
            Log($"connected: {name}");
            int pct = await Battery(name);
            if (pct < 0) { await Task.Delay(2500); pct = await Battery(name); } // battery service reports a moment later
            Log($"banner: {name} pct={pct}");
            if (pct >= 0) _onConnect(name, pct);
        }
        catch (Exception ex) { Log("added failed: " + ex.Message); }
    }

    // battery % for a just-connected device: the connected AEP carries no battery, so match it by name to
    // the PnP device that holds DEVPKEY_Bluetooth_Battery. Returns -1 when Windows has no battery for it.
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
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return pct; // exact match wins
                if (best < 0 && (name.Contains(s, StringComparison.OrdinalIgnoreCase)
                    || s.Contains(name, StringComparison.OrdinalIgnoreCase))) best = pct;    // e.g. "Boy Hands-Free HF"
            }
            return best;
        }
        catch { return -1; }
    }
}
