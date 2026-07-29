# Notifications — mirroring toasts and killing the native banner

## Mirror pipeline
`Notifications/NotifSource.cs` polls WinRT `UserNotificationListener` (~250ms). It works **unpackaged**;
no MSIX needed. Last-seen notification Id is persisted to `%LOCALAPPDATA%\Halo\notif-seen.txt` and resumed
on start — never baseline at 0, because the platform can be empty/down for the first seconds after a
`WpnUserService` restart and the old code then dumped the whole action center as "new" (52-toast flood).
Icon chain: `ShellIcon.ForAumid`/`ForAppName` → `AppIcon` (running exe) → the toast's own `Logo`.
The banner UI is `Widgets/NotifBanner.cs`; the pill morph + queueing + auto-dismiss deadline live in
`NotchController` (`_notif*`).

`Notifications/WpnDb.cs` reads the toast's `launch`/`activationType` straight out of the locked WAL
SQLite `wpndatabase.db` via the **system `winsqlite3.dll`** (P/Invoke, no NuGet), keyed by the same Id the
listener reports. That's what makes a banner click open the exact message/photo
(`NotifItem.Activate`: protocol → `Process.Start`; else `IApplicationActivationManager.ActivateApplication(aumid, launch)`).

## Suppressing the OS banner — current approach: `BannerGate` (in `Notifications/DndGate.cs`)
Per-app, learned, and reversible: the first time an app's toast is mirrored, `SuppressApp(aumid)` records
the app's **original** `ShowBanner` value into `%LOCALAPPDATA%\Halo\` state, writes the silence keys to 0
under `HKCU\...\Notifications\Settings\<AUMID>`, then debounces a `WpnUserService` restart so the platform
re-reads. `Restore`/`Uninstall` put every learned app back; `Halo.App --restore-notifications` is the
uninstall hook that runs it. Always persist the original before writing — that's the only thing that makes
uninstall faithful.

## Dead ends — do not retry (all verified live on Win11 26200)
- **Global DND / Quiet Hours via registry is dead on 26200.** The CloudStore
  `windows.data.donotdisturb.quiethourssettings` blob can be written correctly (profile reads back as
  `Microsoft.QuietHoursProfile.AlarmsOnly`), yet `SHQueryUserNotificationState` never leaves
  `QUNS_ACCEPTS_NOTIFICATIONS(5)` → DND never actually engages. There is no registry "DND enabled" flag;
  live DND state is WNF / in-memory. Only `NtUpdateWnfStateData` on `WNF_SHEL_QUIETHOURS_*` would flip it
  (risky, needs user consent, not built).
- Restarting `WpnUserService` in a loop is self-harm: it kills Halo's own `UserNotificationListener`
  ("Class not registered") until relaunch. Restart at most once, debounced.
- `PushNotifications\ToastEnabled=0` kills listener *delivery* too. Dead end.
- `RemoveNotification` always flashes ~0.5s and can't un-ring the sound.
- The Snipping Tool "snip saved" toast is a system toast that even AlarmsOnly won't silence — Halo instead
  mirrors snips from the clipboard (`LayeredNotch.ClipboardImage` → `NotchController.OnClipboardImage`).

## Verification gotcha
Notification banners can't be screenshot-verified: the pill is capture-excluded, and synthetic PowerShell
toasts are **not** delivered to `UserNotificationListener` the way real app toasts are. Use
`Halo.App --render-notif <png>` (real shape path, colourful backdrop, mixed FA+EN text) plus a real toast
eyeballed by the user. `%LOCALAPPDATA%\Halo\notif-debug.txt` carries `[dnd]`/gate logging.
