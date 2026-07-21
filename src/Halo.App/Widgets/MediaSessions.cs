using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Halo.Widgets;

// Maps the system's live media sessions to STABLE slots so we can show one MediaWidget per player
// (Spotify + a browser video = two strip circles). Same per-session slot idea the Claude widgets use.
// WinRT events fire off the UI thread; the slot list is guarded by _lock.
internal sealed class MediaSessions
{
    public const int MaxSlots = 3;

    private readonly object _lock = new();
    private readonly string[] _slotIds = new string[MaxSlots]; // slot -> SourceAppUserModelId ("" = free)
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;

    public event Action? Changed; // a slot's session appeared/disappeared → widgets re-hook

    public MediaSessions()
    {
        for (int i = 0; i < MaxSlots; i++) _slotIds[i] = "";
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (_, _) => Reassign();
            _mgr.CurrentSessionChanged += (_, _) => Reassign();
            Reassign();
        }
        catch { }
    }

    // pin each app to its slot across reassignments (a widget shouldn't jump players); free the slot
    // when its app's session is gone; drop a brand-new app into the first free slot.
    private void Reassign()
    {
        var mgr = _mgr;
        if (mgr == null) return;
        List<string> live;
        try
        {
            live = mgr.GetSessions().Select(s => s.SourceAppUserModelId ?? "")
                .Where(id => id.Length > 0).Distinct().ToList();
        }
        catch { return; }
        lock (_lock)
        {
            for (int i = 0; i < MaxSlots; i++)
                if (_slotIds[i].Length > 0 && !live.Contains(_slotIds[i])) _slotIds[i] = "";
            foreach (var id in live)
            {
                if (Array.IndexOf(_slotIds, id) >= 0) continue;
                int free = Array.IndexOf(_slotIds, "");
                if (free >= 0) _slotIds[free] = id; // else: more players than slots → not shown (rare)
            }
        }
        Changed?.Invoke();
    }

    public GlobalSystemMediaTransportControlsSession? Session(int slot)
    {
        var mgr = _mgr;
        if (mgr == null || slot < 0 || slot >= MaxSlots) return null;
        string id;
        lock (_lock) { id = _slotIds[slot]; }
        if (id.Length == 0) return null;
        try { foreach (var s in mgr.GetSessions()) if ((s.SourceAppUserModelId ?? "") == id) return s; }
        catch { }
        return null;
    }

    // process name for the app in a slot (e.g. "spotify", "chrome") — for the focus-hide rule
    public string SlotApp(int slot)
    {
        string id;
        lock (_lock) { id = slot >= 0 && slot < MaxSlots ? _slotIds[slot] : ""; }
        return id.Length == 0 ? "" : System.IO.Path.GetFileNameWithoutExtension(id).ToLowerInvariant();
    }
}
