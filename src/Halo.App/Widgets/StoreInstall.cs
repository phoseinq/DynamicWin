using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Store.Preview.InstallControl;

namespace Halo.Widgets;

// Microsoft Store download/install via the Store's OWN install engine (AppInstallManager) instead of
// Delivery Optimization: accurate PercentComplete, real byte counts, the app's package family name, the
// install state, AND pause/resume/cancel. Works unpackaged on this build; all calls are synchronous and
// cheap, so the Downloads scan thread polls it directly (no subprocess, no DO poll).
//
// ponytail: only true MSIX/appx installs (the ones with pause/cancel in the Store) surface here. A few
// Store listings are Win32 apps merely *delivered* through the Store (CapCut, Adobe Reader) — they run a
// classic installer and never appear in AppInstallManager. Windows exposes no % or control for those to
// third parties (Delivery Optimization reports bytes but no total), so they're deliberately skipped —
// verified a rare exception, not worth the DO COM interop. Add a DO path only if they become common.
internal static class StoreInstall
{
    private static AppInstallManager? _mgr;
    private static AppInstallItem? _item;   // the item the pill's control buttons act on
    private static readonly object _lock = new();

    internal enum Phase { None, Waiting, Downloading, Installing, Paused }

    private static int _lastPct; private static long _lastDone, _lastTotal; // held across pauses (report 0)
    // a queued item that never starts downloading is an inert/phantom Store update (e.g. a Phone Link
    // update parked at ReadyToDownload) — Windows keeps it queued but it never runs. Show a real queued
    // install's brief "waiting", then stop haunting the pill once an item has sat queued past the grace.
    private static string? _waitPfn; private static long _waitSinceMs;
    private const long WaitGraceMs = 30_000;

    // pick the most relevant active install and report its live status.
    public static Phase Poll(out string name, out int pct, out long done, out long total)
    {
        name = "Store app"; pct = 0; done = 0; total = 0;
        try
        {
            _mgr ??= new AppInstallManager();
            AppInstallItem? active = null;
            AppInstallStatus? st = null;
            // the modern Store UI queues installs as GROUPED items — invisible in plain AppInstallItems
            // (winget/StartAppInstallAsync items show in both). WithGroupSupport sees them all.
            IReadOnlyList<AppInstallItem> list;
            try { list = _mgr.AppInstallItemsWithGroupSupport; }
            catch { list = _mgr.AppInstallItems; }
            // rank: live download/install > paused (user resumes from the pill) > queued
            int bestRank = -1;
            foreach (var it in list)
            {
                AppInstallStatus s;
                try { s = it.GetCurrentStatus(); } catch { continue; }
                var state = s.InstallState;
                if (state is AppInstallState.Completed or AppInstallState.Canceled or AppInstallState.Error) continue;
                int rank = state switch
                {
                    AppInstallState.Paused or AppInstallState.PausedLowBattery
                        or AppInstallState.PausedWiFiRecommended or AppInstallState.PausedWiFiRequired => 1,
                    AppInstallState.Pending or AppInstallState.ReadyToDownload => -1, // queued/stuck Store update -> never surface (the phantom "Waiting" pill)
                    _ => 2, // downloading/installing/starting/licensing — live
                };
                if (rank < 0) continue; // skip queued/stuck items entirely
                if (rank > bestRank) { bestRank = rank; active = it; st = s; }
                if (rank == 2) break;
            }
            if (active == null || st == null) { lock (_lock) _item = null; return Phase.None; }
            lock (_lock) _item = active;

            pct = (int)Math.Clamp(st.PercentComplete, 0, 100);
            done = (long)st.BytesDownloaded;
            total = (long)st.DownloadSizeInBytes;
            name = FriendlyName(active.PackageFamilyName);
            var phase = st.InstallState switch
            {
                AppInstallState.Paused or AppInstallState.PausedLowBattery
                    or AppInstallState.PausedWiFiRecommended or AppInstallState.PausedWiFiRequired => Phase.Paused,
                AppInstallState.Downloading => Phase.Downloading,
                AppInstallState.Pending or AppInstallState.ReadyToDownload => Phase.Waiting, // queued, download not started yet
                _ => Phase.Installing, // Starting/AcquiringLicense/RestoringData/Installing/Moving
            };
            // drop a perpetually-queued item so it doesn't sit "Waiting…" forever (see _waitPfn note)
            if (phase == Phase.Waiting)
            {
                long nowMs = Environment.TickCount64;
                if (_waitPfn != active.PackageFamilyName) { _waitPfn = active.PackageFamilyName; _waitSinceMs = nowMs; }
                else if (nowMs - _waitSinceMs > WaitGraceMs) { lock (_lock) _item = null; return Phase.None; }
            }
            else _waitPfn = null; // it moved on (downloading/installing/paused) → reset the grace clock
            // paused items report PercentComplete/BytesDownloaded = 0 — hold the last live values so the
            // pill keeps showing the real progress instead of snapping to 0%.
            if (phase == Phase.Paused && pct == 0 && _lastPct > 0) { pct = _lastPct; done = _lastDone; total = _lastTotal; }
            else if (phase != Phase.Waiting) { _lastPct = pct; _lastDone = done; _lastTotal = total; }
            return phase;
        }
        catch { lock (_lock) _item = null; return Phase.None; }
    }

    public static void Pause()  { try { lock (_lock) _item?.Pause(); } catch { } }
    public static void Resume() { try { lock (_lock) _item?.Restart(); } catch { } } // Restart resumes a paused item
    public static void Cancel() { try { lock (_lock) _item?.Cancel(); } catch { } }

    // "Microsoft.People_8wekyb3d8bbwe" → "People" (best-effort friendly label from the family name)
    private static string FriendlyName(string pfn)
    {
        if (string.IsNullOrEmpty(pfn)) return "Store app";
        int us = pfn.IndexOf('_');
        string s = us > 0 ? pfn[..us] : pfn;
        int dot = s.LastIndexOf('.');
        return dot >= 0 && dot < s.Length - 1 ? s[(dot + 1)..] : s;
    }
}
