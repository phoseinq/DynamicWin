using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Halo.Notifications;

/// <summary>
/// A connected device as the coordinator knows it. <paramref name="Name"/> is raw OS text, never
/// translated: it is display material *and* the last-resort battery lookup key, so localizing it
/// here would change which device a reading is attributed to.
/// </summary>
/// <param name="Container">Windows container identity, when the association endpoint published one.
/// This is what ties an endpoint to its PnP battery object; the name is only a fallback.</param>
internal readonly record struct BtDevice(string Id, string Name, string? Container);

/// <summary>
/// The whole of what the pill should show, as one value. Widgets used to be driven by three
/// separate calls (connect / update / disconnect) whose ordering was whatever the thread pool
/// happened to choose, so the widget could end up holding a device the coordinator had already
/// dropped. A snapshot carries its own <paramref name="Revision"/> instead, and anything older
/// than what the widget already applied is discarded on arrival.
/// </summary>
/// <param name="Pct">Battery percentage, or -1 when no trustworthy reading exists.</param>
internal readonly record struct BtSnapshot(
    long Revision, bool Connected, string Id, string Name, int Pct, bool Flash)
{
    public static BtSnapshot Cleared(long revision) => new(revision, false, "", "", -1, false);
    public bool PctKnown => Pct >= 0;
}

/// <summary>
/// Owns which Bluetooth device is featured. Deliberately free of WinRT: battery reads and the
/// retry delay are injected, so every ordering rule below can be exercised without hardware.
///
/// Membership alone is not enough to decide whether an async read may still be published. A read
/// that started earlier can finish later, and by then a different device may legitimately own the
/// pill -- the older completion would then overwrite the newer one and leave a device on screen
/// that nothing will ever remove. So every operation that can change the selection takes a ticket
/// before it awaits anything, and may only commit if no later ticket has committed in the
/// meantime. Membership is still checked too: the ticket says "am I still the newest decision",
/// membership says "does this device still exist".
///
/// The lock is never held across an await or across a publish callback. Commit decisions are made
/// under the lock, the resulting snapshot is handed out after it is released.
/// </summary>
internal sealed class BtCoordinator
{
    private readonly Func<BtDevice, Task<int>> _readBattery;
    private readonly Action<BtSnapshot> _publish;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Action<string>? _log;
    private readonly TimeSpan _retryAfter;

    private readonly object _lock = new();
    private readonly Dictionary<string, BtDevice> _devices = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private string? _featuredId;

    /// <summary>Handed out to each selection operation before it awaits. Strictly increasing.</summary>
    private long _ticket;

    /// <summary>Ticket of the selection that currently owns the pill. Older tickets cannot commit.</summary>
    private long _committed;

    /// <summary>Stamped onto published snapshots so the widget can drop out-of-order deliveries.</summary>
    private long _revision;

    public BtCoordinator(
        Func<BtDevice, Task<int>> readBattery,
        Action<BtSnapshot> publish,
        TimeSpan retryAfter,
        Func<TimeSpan, Task>? delay = null,
        Action<string>? log = null)
    {
        _readBattery = readBattery;
        _publish = publish;
        _retryAfter = retryAfter;
        _delay = delay ?? Task.Delay;
        _log = log;
    }

    /// <summary>The device on the pill, or null. For diagnostics and tests, not for decisions.</summary>
    public string? FeaturedId { get { lock (_lock) return _featuredId; } }

    /// <summary>
    /// A device joined the connected set. <paramref name="flash"/> is false for devices that were
    /// already connected when Halo started: they are shown like any other, but they did not just
    /// arrive, so they must not grab focus.
    /// </summary>
    public async Task Added(BtDevice dev, bool flash)
    {
        long ticket;
        lock (_lock)
        {
            _devices[dev.Id] = dev;
            if (!_order.Contains(dev.Id)) _order.Add(dev.Id);
            ticket = ++_ticket;
        }
        _log?.Invoke(flash ? $"connected: {dev.Name}" : $"seed (already connected): {dev.Name}");

        int pct = await _readBattery(dev);
        if (pct < 0)
        {
            // Some devices publish nothing on the battery key for a moment after connecting.
            await _delay(_retryAfter);
            pct = await _readBattery(dev);
        }
        if (pct < 0) { _log?.Invoke($"no battery reading: {dev.Name}"); return; }

        if (Commit(ticket, dev.Id, pct, flash)) _log?.Invoke($"featured: {dev.Name} pct={pct}");
    }

    /// <summary>
    /// A device left the connected set. If it was the featured one, the most recently connected
    /// device with a readable battery takes over rather than the pill just going blank.
    /// </summary>
    public async Task Removed(string id)
    {
        long ticket;
        List<BtDevice> candidates;
        lock (_lock)
        {
            if (!_devices.TryGetValue(id, out var gone)) return;
            _devices.Remove(id);
            _order.Remove(id);
            ticket = ++_ticket;
            _log?.Invoke($"removed (disconnected): {gone.Name}");
            if (id != _featuredId) return;

            candidates = new List<BtDevice>(_order.Count);
            for (int i = _order.Count - 1; i >= 0; i--) candidates.Add(_devices[_order[i]]);
        }

        foreach (var cand in candidates)
        {
            // Bail out as soon as a newer selection has taken the pill: continuing would only
            // produce commits that are guaranteed to be rejected, and would delay the clear below.
            if (Superseded(ticket)) return;
            int pct = await _readBattery(cand);
            if (pct < 0) continue;
            // A handoff is a fallback, not an arrival: show it, but do not grab focus for it.
            if (Commit(ticket, cand.Id, pct, flash: false))
            {
                _log?.Invoke($"handoff: {cand.Name} pct={pct}");
                return;
            }
            // Rejected either because this handoff is stale, or because the candidate itself
            // vanished mid-read. Only the first one ends the search.
            if (Superseded(ticket)) return;
        }

        Clear(ticket, id);
    }

    /// <summary>
    /// Re-reads the battery of the device already on screen. This never changes the selection, so
    /// it takes no ticket -- it only has to prove the same device is still featured when it lands.
    /// </summary>
    public async Task RefreshFeatured()
    {
        BtDevice dev;
        lock (_lock)
        {
            if (_featuredId is null || !_devices.TryGetValue(_featuredId, out dev)) return;
        }
        int pct = await _readBattery(dev);
        if (pct < 0) return;

        BtSnapshot snap;
        lock (_lock)
        {
            if (_featuredId != dev.Id || !_devices.ContainsKey(dev.Id)) return;
            snap = new BtSnapshot(++_revision, true, dev.Id, dev.Name, pct, Flash: false);
        }
        _publish(snap);
    }

    /// <summary>
    /// Shows the bt-test.txt preview device. It is a real member of the connected set for as long
    /// as it is shown, so a genuine device connecting can supersede it by the same rules as any
    /// other, and <see cref="RemovePreview"/> can take it back -- a fake device never produces a
    /// Removed event, and the widget no longer clears itself on a timer.
    /// </summary>
    public void Preview(BtDevice dev, int pct)
    {
        long ticket;
        lock (_lock)
        {
            _devices[dev.Id] = dev;
            if (!_order.Contains(dev.Id)) _order.Add(dev.Id);
            ticket = ++_ticket;
        }
        Commit(ticket, dev.Id, pct, flash: true);
    }

    public void RemovePreview(string id)
    {
        long ticket;
        lock (_lock)
        {
            if (!_devices.Remove(id)) return;
            _order.Remove(id);
            ticket = ++_ticket;
        }
        Clear(ticket, id);
    }

    private bool Superseded(long ticket) { lock (_lock) return ticket < _committed; }

    /// <summary>
    /// Publishes <paramref name="id"/> as featured, unless a newer selection got there first or the
    /// device stopped existing while its battery was being read. Returns whether it was published.
    /// </summary>
    private bool Commit(long ticket, string id, int pct, bool flash)
    {
        BtSnapshot snap;
        lock (_lock)
        {
            if (ticket < _committed) { _log?.Invoke($"superseded during read: {id}"); return false; }
            if (!_devices.TryGetValue(id, out var dev)) { _log?.Invoke($"vanished during read: {id}"); return false; }
            _featuredId = id;
            _committed = ticket;
            snap = new BtSnapshot(++_revision, true, id, dev.Name, pct, flash);
        }
        _publish(snap);
        return true;
    }

    /// <summary>
    /// Empties the pill after a disconnect that nothing could take over from. Guarded by the same
    /// ticket rule: an old handoff that ran out of candidates must not blank a device that has
    /// since been featured by a newer connection.
    /// </summary>
    private void Clear(long ticket, string removedId)
    {
        BtSnapshot snap;
        lock (_lock)
        {
            if (ticket < _committed) return;
            if (_featuredId is not null && _featuredId != removedId) return;
            _featuredId = null;
            _committed = ticket;
            snap = BtSnapshot.Cleared(++_revision);
        }
        _publish(snap);
    }
}
