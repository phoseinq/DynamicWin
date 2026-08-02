using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Halo.Notifications;

internal sealed class NotifItem
{
    // Clipboard/screenshot banner wording. Shared because the real path (NotchController.OnClipboardImage)
    // and the --render-notif dev hook in Program.cs each spelled these out, so the preview image could
    // drift away from what users actually see — and a pt-BR pull request did edit one copy only.
    public const string ScreenshotApp = "Screenshot";
    public const string ClipboardApp = "Clipboard";
    public const string ScreenshotTitle = "Screenshot captured";
    public const string ImageCopiedTitle = "Image copied";

    public uint Id;
    public DateTime Time = DateTime.Now; // arrival time — shown as the banner's eyebrow timestamp
    public string App = "";
    public string Title = "";
    public string Body = "";
    public string Aumid = "";           // source app id
    public System.Drawing.Bitmap? Icon; // app logo (null → banner falls back to the app initial)
    public System.Drawing.Bitmap? Preview; // wide capture thumbnail (screenshot banners)
    public string LaunchPath = "";      // click opens this file/URI directly (e.g. a saved screenshot)
    public string Kind = "";            // groups replaceable banners (e.g. "language") so rapid ones don't queue
    public double Duration = 6;         // seconds the banner lingers before auto-dismiss
    public Action? OnActivate;          // local banners (e.g. battery) run this on click instead of launching an app
    public string Code = "";              // 2FA / verification code detected in the text (6 or 8 digits) -> a Copy button
    public bool Copied;                  // set once the Copy button is clicked -> the button shows a check
    public int Stacked;                  // >0: this banner stands for itself plus N more like it

    // clicking the banner reproduces the real toast's click: WpnDb digs the toast's launch args out of
    // wpndatabase.db (the listener API hides them), so we jump to the exact message/photo — not just
    // the app. protocol toasts → open the URI; foreground toasts → hand the args to the app via
    // IApplicationActivationManager (the shell's own toast-activation path). Both degrade to just
    // opening the app when there are no args.
    public void Activate()
    {
        // a local action (turn on Power Saver) takes precedence over any app-launch path
        if (OnActivate != null) { try { OnActivate(); } catch { } return; }

        // a locally-attached target (a saved screenshot) opens directly
        if (LaunchPath.Length > 0)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = LaunchPath, UseShellExecute = true }); return; }
            catch { }
        }

        var (launch, actType) = WpnDb.LaunchFor(Id);
        // General auto-detect (no per-app hacks): if the toast says protocol, OR the launch is itself a
        // URI (ms-phone:…&threadid=9, https:…, mailto:…), open it through the shell so the app lands on the
        // exact target — what clicking the real toast does. Phone Link etc. use a URI launch with default
        // activation, so protocol-only matching missed them (app opened but not the message). Chrome's
        // pipe-delimited launch isn't a URI → falls through to app activation; a non-registered scheme
        // throws and also falls through.
        if (launch.Length > 0 && (actType.Equals("protocol", StringComparison.OrdinalIgnoreCase) || LooksLikeUri(launch)))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = launch, UseShellExecute = true }); return; }
            catch { }
        }
        if (Aumid.Length == 0) return;
        try { ((IApplicationActivationManager)new ApplicationActivationManager()).ActivateApplication(Aumid, launch, 0, out _); return; }
        catch { }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "explorer.exe", Arguments = "shell:AppsFolder\\" + Aumid, UseShellExecute = true });
        }
        catch { }
    }

    // "scheme:rest" with a valid URI scheme (letter then letters/digits/+-.), and no '|' (rules out
    // Chrome's "0|0|Default|…" launch format). Cheap stand-in for Uri.TryCreate without false positives.
    private static bool LooksLikeUri(string s)
    {
        int c = s.IndexOf(':');
        if (c <= 0 || s.IndexOf('|') >= 0 || !char.IsLetter(s[0])) return false;
        for (int i = 1; i < c; i++)
        {
            char ch = s[i];
            if (!char.IsLetterOrDigit(ch) && ch != '+' && ch != '-' && ch != '.') return false;
        }
        return true;
    }
}

// Shell's documented "activate a modern app with arguments" path — the same call Windows makes when
// you click a toast, so the app receives the toast's launch args and opens the right thing.
[System.Runtime.InteropServices.ComImport,
 System.Runtime.InteropServices.Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager { }

[System.Runtime.InteropServices.ComImport,
 System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
 System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    void ActivateApplication(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string appUserModelId,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string arguments,
        uint options, out uint processId);
}

// Reads Windows toasts via UserNotificationListener (works unpackaged). WinRT runs off-thread and
// only touches the locked queue + bumps Version; the UI thread polls Version and Dequeues.
internal sealed class NotifSource
{
    private readonly object _lock = new();
    private readonly Queue<NotifItem> _pending = new();
    private UserNotificationListener? _listener;
    private System.Threading.Timer? _poll; // field: a discarded Timer gets GC'd and stops firing
    private uint _seenMaxId;      // dedup baseline; only Id > this is "new"
    private bool _baselined;      // baseline established (startup platform races can stall this a few polls)
    private readonly long _startTick = Environment.TickCount64;
    private long _lastReheal;     // rate-limit listener re-acquire after a WpnUserService restart
    private int _version;

    public NotifSource() => _ = InitAsync();

    public int Version { get { lock (_lock) { return _version; } } }

    // Chrome held six claude.ai notifications while it was closed and released all of them in the same
    // second; the pill then played six banners one after another, which is the report "when I open the
    // browser it attacks". Faithful mirroring is right, but six copies of one thing is not six things.
    //
    // Folded into the banner already waiting rather than deduped away: the count is shown, so nothing is
    // hidden, and the newest body wins because that is the one worth reading. Same app AND same title
    // only - two different messages from one app are two messages.
    private bool Stack(NotifItem item)
    {
        foreach (var queued in _pending)
        {
            if (!string.Equals(queued.Aumid, item.Aumid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(queued.Title, item.Title, StringComparison.Ordinal)) continue;
            queued.Stacked++;
            queued.Body = item.Body;
            queued.Time = item.Time;
            // more to read means more time to read it, up to a point
            queued.Duration = Math.Min(12, queued.Duration + 1.5);
            return true;
        }
        return false;
    }

    public NotifItem? Dequeue()
    {
        lock (_lock) { return _pending.Count > 0 ? _pending.Dequeue() : null; }
    }

    public bool HasPending { get { lock (_lock) { return _pending.Count > 0; } } }

    // feed a locally-synthesized notification (e.g. a screenshot caught via the clipboard, which Windows
    // never delivers as a real toast) into the same banner pipeline as listener toasts
    public void EnqueueLocal(NotifItem item)
    {
        lock (_lock) { _pending.Enqueue(item); _version++; }
    }

    // drop queued banners of a kind (rapid language switches shouldn't pile up a backlog)
    public void DropPending(string kind)
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            var keep = new Queue<NotifItem>();
            foreach (var it in _pending) if (it.Kind != kind) keep.Enqueue(it);
            _pending.Clear();
            foreach (var it in keep) _pending.Enqueue(it);
        }
    }

    private static readonly string DebugPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "notif-debug.txt");

    // last-seen id, persisted across restarts. A WpnUserService restart (Windows' own, or a prior build's)
    // takes the platform down for a few seconds, so a fresh process can't trust an early fetch — remembering
    // where we were on disk makes restarts immune to the "dump the whole action center" flood.
    private static readonly string SeenPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "notif-seen.txt");

    private void LoadSeen()
    {
        try { if (uint.TryParse((System.IO.File.ReadAllText(SeenPath) ?? "").Trim(), out var v) && v > 0) { _seenMaxId = v; _baselined = true; } }
        catch { }
    }

    private static void SaveSeen(uint v) { try { System.IO.File.WriteAllText(SeenPath, v.ToString()); } catch { } }

    private static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(DebugPath, $"{DateTime.Now:HH:mm:ss} {msg}\r\n");
        }
        catch { }
    }

    private async Task InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            Log($"access = {access}");
            if (access != UserNotificationListenerAccessStatus.Allowed) return;
            LoadSeen(); // resume from the last-seen id so a restart never re-banners old toasts
            // Not unconditional any more: silencing the native banner is a setting, and it defaults to
            // OFF. Enabling it here regardless meant the pill quietly rewrote every app's ShowBanner on
            // first run while the panel's switch sat there saying it had not. NotchController drives it
            // from settings now, both ways, so the switch and the registry agree.
            await Refresh(); // tries to baseline existing toasts; if the platform isn't up yet, the poll baselines instead
            // NotificationChanged is unreliable for unpackaged apps — poll as the real driver
            try { _listener.NotificationChanged += (s, e) => { _ = Refresh(); }; }
            catch (Exception ex) { Log("event hook failed: " + ex.Message); }
            // NotificationChanged never attaches unpackaged on this build ("event hook failed" in the
            // log), so the poll IS the toast-suppression latency: it must beat the native banner's
            // slide-in or Windows and Halo double-show. 250ms keeps the flash to a blink.
            _poll = new System.Threading.Timer(_ => { _ = Refresh(); }, null, 250, 250);
        }
        catch (Exception ex) { Log("init failed: " + ex); }
    }

    private async Task Refresh()
    {
        if (_listener == null) return;
        try
        {
            var notes = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            lock (_lock)
            {
                uint maxId = _seenMaxId;
                foreach (var n in notes) if (n.Id > maxId) maxId = n.Id;

                // Baseline the current top once (banner nothing that was already there), but only once the
                // platform is actually up: for the first ~3s after launch it can return an EMPTY list (or
                // throw) while still warming up — baselining at 0 then would dump the whole action center
                // as "new" (the 52-toast flood). So wait for a non-empty result, or for the grace to pass
                // (after which an empty list really means an empty action center).
                if (!_baselined)
                {
                    if (notes.Count == 0 && Environment.TickCount64 - _startTick < 3000) return;
                    _seenMaxId = maxId;
                    _baselined = true;
                    SaveSeen(maxId);
                    Log($"baseline maxId = {maxId}");
                    return;
                }

                foreach (var n in notes)
                {
                    if (n.Id <= _seenMaxId) continue;
                    var item = Build(n);
                    if (item != null)
                    {
                        BannerGate.SuppressApp(item.Aumid); // kill this app's native banner going forward (we mirror it)
                        if (Stack(item)) continue;          // folded into the one already queued
                        _pending.Enqueue(item);
                        // "block": we own this toast now — yank it so Windows' banner/action center
                        // don't double-show it (best effort; the OS banner may flash briefly)
                        try { _listener.RemoveNotification(n.Id); } catch { }
                    }
                }
                if (maxId > _seenMaxId) { Log($"new toasts up to id {maxId}, queued {_pending.Count}"); _seenMaxId = maxId; SaveSeen(maxId); }
                if (_pending.Count > 0) _version++;
            }
        }
        catch (Exception ex) { Log("refresh failed: " + ex.Message); TryReheal(); }
    }

    // BannerGate restarts WpnUserService to apply per-app banner suppressions (and Windows restarts it on its
    // own too). That invalidates our listener COM object → GetNotificationsAsync throws "Class not registered"
    // and mirroring silently dies until relaunch. Re-acquire UserNotificationListener.Current so it resumes.
    // Rate-limited: the 250ms poll would otherwise hammer this while the platform is mid-restart.
    private void TryReheal()
    {
        if (Environment.TickCount64 - _lastReheal < 5_000) return;
        _lastReheal = Environment.TickCount64;
        _ = Reheal();
    }

    private async Task Reheal()
    {
        try
        {
            var l = UserNotificationListener.Current;
            if (await l.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed) return;
            _listener = l; // dedup baseline (_seenMaxId) is preserved, so no re-banner flood on reconnect
            Log("listener re-acquired after service restart");
        }
        catch (Exception ex) { Log("reheal failed: " + ex.Message); }
    }

    // every part is best-effort on its own — a toast with a broken AppInfo (e.g. script-sent)
    // must still banner with whatever text it has
    private static NotifItem? Build(UserNotification n)
    {
        string app = "", title = "", body = "", aumid = "";
        try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; }
        catch (Exception ex) { Log("appinfo failed: " + ex.Message); }
        try { aumid = n.AppInfo?.AppUserModelId ?? ""; } catch { }
        try
        {
            var bind = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (bind != null)
            {
                var texts = bind.GetTextElements();
                if (texts.Count > 0) title = texts[0].Text ?? "";
                for (int i = 1; i < texts.Count; i++)
                    body += (body.Length > 0 ? "  " : "") + texts[i].Text;
            }
        }
        catch (Exception ex) { Log("texts failed: " + ex.Message); }
        Log($"toast {n.Id}: aumid='{aumid}' app='{app}' t={title.Length} b={body.Length}"); // ponytail: temp diag, trim once screenshot path confirmed
        if (title.Length == 0 && body.Length == 0)
        {
            // image/progress-only toasts (a saved screenshot, a download bar) ship no <text>; show the
            // app name instead of dropping them, so e.g. the Win+Shift+S "snip saved" toast still mirrors.
            if (app.Length == 0) return null;
            title = app;
        }
        // real Start-menu icon first: it's a clean transparent silhouette for both packaged and classic
        // apps. GetLogo (the UWP toast logo) often bakes the tile's background plate in — a small mark on
        // a white/coloured square — which is the "white box around the icon", so it's the last resort.
        // Middle tier: some apps toast under a custom AUMID that isn't in the Start menu (PowerToys.Run,
        // …) → ShellIcon returns nothing → pull the running exe's icon instead of showing a broken mark.
        System.Drawing.Bitmap? icon = ShellIcon.ForAumid(aumid);
        icon ??= Halo.Widgets.AppIcon.ForAumid(aumid);
        if (icon == null) { try { icon = Logo(n); } catch { } }
        icon ??= ShellIcon.ForAppName(app); // tray-balloon toasts (WireGuard/Amnezia): match the Start-menu shortcut by name
        return new NotifItem { Id = n.Id, App = app, Title = title, Body = body, Aumid = aumid, Icon = icon, Code = DetectCode(title, body) };
    }

    // find a one-time / 2FA code in the toast text: a standalone run of exactly 6 or 8 digits (the common
    // OTP lengths), not glued to other digits, and skipping anything that reads like a year/price/phone by
    // requiring it to stand alone. First match wins; empty string = no code, so no Copy button is shown.
    private static readonly System.Text.RegularExpressions.Regex CodeRx =
        new(@"(?<![\d])(\d{6}|\d{8})(?![\d])", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static string DetectCode(string title, string body)
    {
        foreach (var text in new[] { title, body })
        {
            if (string.IsNullOrEmpty(text)) continue;
            var m = CodeRx.Match(text);
            if (m.Success) return m.Groups[1].Value;
        }
        return "";
    }
    private static System.Drawing.Bitmap? Logo(UserNotification n)
    {
        try
        {
            var logoRef = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(256, 256));
            if (logoRef == null) return null;
            using var ras = logoRef.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            using var s = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(ras);
            using var tmp = new System.Drawing.Bitmap(s);
            return new System.Drawing.Bitmap(tmp); // detach from the stream (GDI+ keeps it open otherwise)
        }
        catch { return null; }
    }
}
