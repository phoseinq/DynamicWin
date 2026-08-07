using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace Halo.Notifications;

/// <summary>
/// Windows side of the Bluetooth pill: watches the connected set, reads battery levels out of PnP,
/// and hands both to <see cref="BtCoordinator"/>, which decides what is actually shown. Everything
/// with an ordering rule attached to it lives in the coordinator so it can be tested without
/// hardware; this class is deliberately only plumbing.
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

    /// <summary>Some devices publish nothing on the battery key for a moment after connecting.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMilliseconds(2500);

    /// <summary>Synthetic id for the bt-test.txt preview device, so it can be cleared like a real one.</summary>
    private const string TriggerId = "halo:bt-test";

    /// <summary>Display name for a device Windows gave us no name for. Never used as a lookup key.</summary>
    private const string UnnamedDevice = "Bluetooth device";

    private readonly BtCoordinator _coord;
    private readonly Func<bool> _shouldRefresh;
    private DeviceWatcher? _watcher;
    private volatile bool _live;
    private System.Threading.Timer? _trigger;
    private System.Threading.Timer? _refresh;

    private static readonly string TriggerPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "bt-test.txt");

    private static readonly string DebugPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "bt-debug.txt");
    private static void Log(string m) { try { System.IO.File.AppendAllText(DebugPath, $"{DateTime.Now:HH:mm:ss} {m}\r\n"); } catch { } }

    public BtBattery(Action<BtSnapshot> publish, Func<bool> shouldRefresh)
    {
        _shouldRefresh = shouldRefresh;
        _coord = new BtCoordinator(Battery, publish, RetryAfter, log: Log);
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
                _coord.RemovePreview(TriggerId);
                return;
            }
            int pct = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var p) ? p : 80;
            Log($"trigger: {name} {pct}%");
            _coord.Preview(new BtDevice(TriggerId, name, null), pct);
        }
        catch { }
    }

    private async void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        try
        {
            // Devices already connected when Halo started are seeded rather than announced: they
            // are shown like any other, but they did not just arrive, so they must not grab focus.
            // The old code returned here instead, which is why a device connected before login
            // never appeared at all -- and the app starts with Windows, so that was the normal case.
            bool seeded = !_live;
            string name = info.Name?.Length > 0 ? info.Name : UnnamedDevice;
            await _coord.Added(new BtDevice(info.Id, name, null), flash: !seeded);
        }
        catch (Exception ex) { Log("added failed: " + ex.Message); }
    }

    private async void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try { await _coord.Removed(update.Id); }
        catch (Exception ex) { Log("removed handler failed: " + ex.Message); }
    }

    private async Task RefreshFeatured()
    {
        // Skip the whole-machine device enumeration when nobody is looking at the widget.
        if (!_shouldRefresh()) return;
        // The preview device has no battery to re-read; its number came from the trigger file.
        if (_coord.FeaturedId == TriggerId) return;
        try { await _coord.RefreshFeatured(); }
        catch (Exception ex) { Log("refresh failed: " + ex.Message); }
    }

    private static async Task<int> Battery(BtDevice dev)
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
                if (string.Equals(s, dev.Name, StringComparison.OrdinalIgnoreCase)) return pct;
                if (best < 0 && (dev.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                    || s.Contains(dev.Name, StringComparison.OrdinalIgnoreCase))) best = pct;
            }
            return best;
        }
        catch { return -1; }
    }
}
