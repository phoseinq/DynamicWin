# Privacy

**English** · [فارسی](PRIVACY.fa.md)

Halo runs entirely on your machine. There is no Halo account, no Halo server, no analytics, no
telemetry and no crash reporting. Nothing that appears in the pill is uploaded anywhere.

This page exists so you can check that claim rather than take it. It lists every kind of data Halo
reads, everything it writes to disk, and **every network request it is capable of making**.

---

## What Halo reads from your machine

All of it stays on your machine.

| What | What it is for | Where it comes from |
| :-- | :-- | :-- |
| Track title, artist, artwork, position | the media panel | Windows' own media session (the same one the volume flyout uses) |
| Notification title, body, app icon | mirroring toasts into the pill | Windows' `UserNotificationListener` |
| A toast's launch arguments | so clicking a banner opens the exact message | Windows' own notification database (`wpndatabase.db`) |
| A verification code inside a notification | the one-click **Copy** button | matched in memory from the notification's text. It reaches your clipboard only when you press the button |
| Download name, size and progress | the download panel | your browser's own local download database |
| Bluetooth device battery level | the battery panel | Windows Bluetooth APIs |
| Coding-session state | the Claude Code / Codex panels | JSON files those tools' own hooks write under `~/.claude/notch`, `~/.codex/notch` and `~/.halo/agents` |
| Paths of files you drag onto the pill | the file tray | you dragged them there. Only the paths are kept, never the contents |
| Which window is in front | so the pill can follow the app you're using | Windows' foreground-window API. Only the process id is used |

---

## What Halo writes to disk

Everything lives in `%LOCALAPPDATA%\Halo\`. Deleting that folder resets Halo completely.

- `offset`, `pinned`, `scale`, `capturable` — where you put the pill and how you like it
- `tray.txt` — the paths currently in the file tray
- `notif-seen.txt` — the id of the last notification shown, so restarts don't replay your Action Center
- `banner-orig.tsv` — **each app's original Windows banner setting before Halo changed it** (see below)
- `limit-fired.txt`, `usage-cache.json`, `codex-limits-cache.json` — which alerts have fired, and the last usage numbers
- `downloaders.tsv`, `update-check`, `update-log.txt` — download bookkeeping and the last update check
- `*-debug.txt` — local diagnostics

The diagnostics are worth being specific about, because they concern your notifications:
`notif-debug.txt` records **which app** sent a notification and **how many characters** its title and
body had — never the text. A line looks like this, and that is the whole of it:

```
15:37:26 toast 67750: aumid='Logi.GHUB.Systray' app='Logitech G HUB' t=14 b=22
```

None of these files are ever transmitted anywhere.

---

## The one registry change Halo makes

When Halo mirrors a toast, it silences Windows' own banner for that app so you aren't told the same
thing twice. It does that by setting `ShowBanner` to `0` for that app under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings`.

It writes down each app's **original** value first, and the change is fully reversible:

```
Halo.App.exe --restore-notifications
```

The uninstaller runs that for you, so removing Halo puts every app's notifications back the way it
found them.

---

## Every network request Halo can make

Halo makes no request that is not on this list.

| Endpoint | When | What it discloses |
| :-- | :-- | :-- |
| `api.github.com` (this repo's latest release) and the release asset | update check, and the download when there is one | nothing beyond the request itself |
| `www.google.com/generate_204` | connectivity check, while a coding-session panel is live | nothing. This is the standard connectivity-check endpoint, chosen because it returns an empty response |
| `api.anthropic.com` — `/api/oauth/usage` and `/v1/messages` | your Claude Code usage limits and whether the API is reachable | **your own** Claude credentials, read from `~/.claude/.credentials.json` — the same token Claude Code itself uses, sent only to Anthropic |
| `chatgpt.com/backend-api/codex/responses` | whether Codex is reachable | a reachability probe |
| `ipwho.is` | to show which country your connection is leaving from | **your public IP address**, unavoidably. This is a third party |
| `flagcdn.com` | the flag image for that country | the two-letter country code |
| `displaycatalog.mp.microsoft.com` | the name and art of a Microsoft Store install in progress | the Store product id |
| `127.0.0.1` | VLC playback controls | nothing — it never leaves your machine |

**The `ipwho.is` request is the only one that tells a third party anything about you.** It discloses
your public IP the same way opening any web page does, it runs only while a coding-session panel is
open, and at most once every five minutes. If that trade isn't worth a small flag to you, say so in
an issue — it is a good candidate for a switch.

---

## What Halo never does

- No account and no sign-in — there is nothing to sign in to.
- No analytics, telemetry, usage statistics or crash reporting, in any build.
- Notification text, media titles, file names, download names and clipboard contents **never leave
  your machine**.
- Nothing is sent to the author or to `pvboy.dev`. Updates come from GitHub; there is no server
  behind Halo at all.

---

## Checking any of this yourself

The source is here and builds from it. Every outbound request in the table above is one `grep` away:

```
grep -rn "https\?://" --include="*.cs" src/
```

Something missing or wrong? [Open an issue](https://github.com/phoseinq/DynamicWin/issues).
