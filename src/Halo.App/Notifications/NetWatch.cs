using System;
using System.Net.NetworkInformation;
using System.Threading;
using Halo.Text;
using WinRt = Windows.Networking.Connectivity;

namespace Halo.Notifications;

/// <summary>What the machine is attached to right now.</summary>
/// <param name="Wifi">Wi-Fi rather than Ethernet or mobile, which changes the wording.</param>
/// <param name="Name">SSID when known, otherwise the connection profile name.</param>
internal readonly record struct NetLink(bool Online, bool Constrained, bool Wifi, string Name)
{
    public static NetLink Offline => new(false, false, false, "");

    /// <summary>Same network, in the only sense the user cares about.</summary>
    public bool Same(NetLink o) => Online == o.Online && Constrained == o.Constrained
        && Wifi == o.Wifi && string.Equals(Name, o.Name, StringComparison.Ordinal);
}

/// <summary>
/// Reports the network transitions worth interrupting someone for: the connection dropped,
/// a new one was joined, or it silently moved to a different network.
///
/// Deliberately not built on latency probes. NetMon infers trouble by timing HTTP requests,
/// which cannot tell a Wi-Fi drop from a slow server, takes two ten-second rounds to notice,
/// and stays silent when the machine moves to a different network that also works. This
/// watches the connection itself, so a drop is a fact rather than an inference.
/// </summary>
internal sealed class NetWatch : IDisposable
{
    /// <summary>
    /// Windows reports a change as a burst, and a switch arrives as a drop followed by a join.
    /// Waiting for the dust to settle turns that into the single transition it really was.
    /// </summary>
    private const int SettleMs = 2000;

    /// <summary>
    /// Events are the fast path, not the only path: they can be missed, and a profile can change
    /// without one firing. Re-reading occasionally costs a local API call, unlike a network probe.
    /// </summary>
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(20);

    private readonly Action<string, string, bool> _onNotice;
    private readonly object _lock = new();
    private readonly Timer _settle;
    private readonly Timer _reconcile;
    private NetLink _current;
    private bool _disposed;

    /// <param name="onNotice">title, body, and whether this is a loss (picks the badge).</param>
    public NetWatch(Action<string, string, bool> onNotice)
    {
        _onNotice = onNotice;
        // Seeded, not announced: the network already in use when Halo starts is the status quo,
        // and the app launches with Windows, so announcing it would fire on every single login.
        _current = Read();
        _settle = new Timer(_ => Settle(), null, Timeout.Infinite, Timeout.Infinite);
        _reconcile = new Timer(_ => Settle(), null, ReconcileEvery, ReconcileEvery);
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
        NetworkChange.NetworkAddressChanged += OnChanged;
    }

    /// <summary>The network currently being reported, for callers that want to show it.</summary>
    public NetLink Current { get { lock (_lock) return _current; } }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try { _settle.Change(SettleMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Settle()
    {
        if (_disposed) return;
        NetLink now = Read(), was;
        lock (_lock)
        {
            if (now.Same(_current)) return;
            was = _current;
            _current = now;
        }
        var (title, body, lost) = Describe(was, now);
        if (title.Length > 0) _onNotice(title, body, lost);
    }

    /// <summary>Turns a transition into the sentence a person would say about it.</summary>
    private static (string title, string body, bool lost) Describe(NetLink was, NetLink now)
    {
        if (!now.Online)
            return (was.Wifi ? Loc.T("Wi-Fi disconnected") : Loc.T("Network disconnected"),
                was.Name.Length > 0 ? Loc.T("Was on {0}", was.Name) : Loc.T("No internet"), true);

        string name = now.Name.Length > 0 ? now.Name : Loc.T("this network");

        // A captive portal answers requests, so a latency probe reads it as a healthy connection.
        // Saying so is the whole point: the user is online with nothing working.
        if (now.Constrained)
            return (Loc.T("{0} needs you to sign in", name), Loc.T("Connected, but not online yet"), true);

        if (!was.Online)
            return (Loc.T("Connected to {0}", name), "", false);

        return (Loc.T("Switched to {0}", name),
            was.Name.Length > 0 ? Loc.T("Was on {0}", was.Name) : "", false);
    }

    private static NetLink Read()
    {
        try
        {
            var profile = WinRt.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return NetLink.Offline;

            var level = profile.GetNetworkConnectivityLevel();
            // LocalAccess means attached to a network that goes nowhere, which is a drop as far
            // as anyone using the machine is concerned.
            bool online = level is WinRt.NetworkConnectivityLevel.InternetAccess
                or WinRt.NetworkConnectivityLevel.ConstrainedInternetAccess;
            if (!online) return NetLink.Offline;

            bool wifi = profile.IsWlanConnectionProfile;
            string name = "";
            if (wifi)
                try { name = profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? ""; }
                catch { }
            if (name.Length == 0) name = profile.ProfileName ?? "";

            return new NetLink(true,
                level == WinRt.NetworkConnectivityLevel.ConstrainedInternetAccess, wifi, name);
        }
        catch { return NetLink.Offline; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnChanged;
        NetworkChange.NetworkAddressChanged -= OnChanged;
        _settle.Dispose();
        _reconcile.Dispose();
    }
}
