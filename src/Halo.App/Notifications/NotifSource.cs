using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Halo.Notifications;

internal sealed class NotifItem
{
    public uint Id;
    public string App = "";
    public string Title = "";
    public string Body = "";
}

// Reads Windows toasts via UserNotificationListener (works unpackaged). WinRT runs off-thread and
// only touches the locked queue + bumps Version; the UI thread polls Version and Dequeues.
internal sealed class NotifSource
{
    private readonly object _lock = new();
    private readonly Queue<NotifItem> _pending = new();
    private UserNotificationListener? _listener;
    private uint _seenMaxId;      // dedup baseline; only Id > this is "new"
    private bool _ready;
    private int _version;

    public NotifSource() => _ = InitAsync();

    public int Version { get { lock (_lock) { return _version; } } }

    public NotifItem? Dequeue()
    {
        lock (_lock) { return _pending.Count > 0 ? _pending.Dequeue() : null; }
    }

    private async Task InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed) return;
            await Refresh(initial: true); // baseline existing toasts, don't banner them
            _listener.NotificationChanged += (s, e) => { _ = Refresh(initial: false); };
            _ready = true;
        }
        catch { }
    }

    private async Task Refresh(bool initial)
    {
        if (_listener == null) return;
        try
        {
            var notes = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            lock (_lock)
            {
                uint maxId = _seenMaxId;
                foreach (var n in notes)
                {
                    if (n.Id <= _seenMaxId) continue;
                    if (n.Id > maxId) maxId = n.Id;
                    if (initial || !_ready) continue;
                    var item = Build(n);
                    if (item != null) _pending.Enqueue(item);
                }
                _seenMaxId = maxId;
                if (_pending.Count > 0) _version++;
            }
        }
        catch { }
    }

    private static NotifItem? Build(UserNotification n)
    {
        try
        {
            string app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            string title = "", body = "";
            var bind = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (bind != null)
            {
                var texts = bind.GetTextElements();
                if (texts.Count > 0) title = texts[0].Text ?? "";
                for (int i = 1; i < texts.Count; i++)
                    body += (body.Length > 0 ? "  " : "") + texts[i].Text;
            }
            if (title.Length == 0 && body.Length == 0) return null;
            return new NotifItem { Id = n.Id, App = app, Title = title, Body = body };
        }
        catch { return null; }
    }
}
