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
| Your device's location | the weather on the hourly banner | Windows' own location service — **only if location is switched on and Halo is allowed to use it**. If it is off, or Halo is denied, Halo never asks again and falls back to your timezone's city |

---

## What Halo writes to disk

Everything lives in `%LOCALAPPDATA%\Halo\`. Deleting that folder resets Halo completely.

- `offset`, `pinned`, `scale`, `capturable` — where you put the pill and how you like it
- `tray.txt` — the paths currently in the file tray
- `notif-seen.txt` — the id of the last notification shown, so restarts don't replay your Action Center
- `banner-orig.tsv` — **each app's original Windows banner setting before Halo changed it** (see below)
- `limit-fired.txt`, `usage-cache.json`, `codex-limits-cache.json` — which alerts have fired, and the last usage numbers
- `downloaders.tsv` — download bookkeeping
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
| `www.google.com/generate_204` | connectivity check, while a coding-session panel is live | nothing. This is the standard connectivity-check endpoint, chosen because it returns an empty response |
| `api.anthropic.com` — `/api/oauth/usage` and `/v1/messages` | your Claude Code usage limits and whether the API is reachable | **your own** Claude credentials, read from `~/.claude/.credentials.json` — the same token Claude Code itself uses, sent only to Anthropic |
| `chatgpt.com/backend-api/codex/responses` | whether Codex is reachable | a reachability probe |
| `ipwho.is` | to show which country your connection is leaving from | **your public IP address**, unavoidably. This is a third party |
| `api.ipapi.is` | only while you hover the exit block, to say whether that address looks like a datacenter, a known vpn, or a flagged one | **your public IP address**. This is a third party. Sent once per address and then cached, so hovering repeatedly costs no further requests |
| `bash.ws` — `/id`, six lookups of `<n>.<id>.bash.ws`, and `/dnsleak/test/<id>` | only while you hover the exit block, to test whether your DNS lookups leave by the same exit as your traffic | **which resolvers answer for you**, and your public IP. This is a third party, and it is a wider disclosure than the two above: the whole mechanism is that their nameserver watches which resolver comes asking. Once per address, then cached |
| `flagcdn.com` | the flag image for that country | the two-letter country code |
| `geocoding-api.open-meteo.com` and `api.open-meteo.com` | the weather on the hourly banner, refreshed every half hour | **coordinates.** If Windows location is on and Halo is allowed, those are **your device's own coordinates**, to about 11 m. Otherwise they are the coordinates of the city from your timezone — "Asia/Tehran" becomes "Tehran" — which is a whole city wide. The city name is also sent once, to look it up |
| `displaycatalog.mp.microsoft.com` | the name and art of a Microsoft Store install in progress | the Store product id |
| `127.0.0.1` | VLC playback controls | nothing — it never leaves your machine |

**`ipwho.is`, `api.ipapi.is` and `bash.ws` are the only requests that tell a third party anything about
you.** All three disclose your public IP the same way opening any web page does. `ipwho.is` runs only
while a coding-session panel is open, and at most once every five minutes. The other two run only when
you actually **hover the exit block**, once per address, cached until the address changes — they answer a
question you asked by pointing at it, so each costs exactly one lookup.

`api.ipapi.is` is asked over HTTPS deliberately: other providers serve the same flags over plaintext
HTTP, and asking "is my exit private" over a channel the local network can read and rewrite is the wrong
trade.

**`bash.ws` deserves its own paragraph**, because a DNS leak test cannot be done quietly. There is no way
to see which resolver actually answers for you from inside your own machine — the only way is to look up
names under a domain whose nameserver is watching, and read back which resolvers came asking. So the test
necessarily tells `bash.ws` who resolves your names. That is the entire point of it, and it is why it
never runs on its own: no hover, no test.

**Open-Meteo** needs no key and is sent nothing that identifies you — no name, no id, no account. What
it is sent is a point to fetch the weather for, and that point is as precise as you have allowed:

- **Location switched on and Halo allowed** — your device's own coordinates, at roughly 11 m. Halo asks
  Windows for a fix at most every ten minutes, and only on the half-hourly weather refresh.
- **Location off, or Halo denied** — the city from the timezone Windows is already set to, which is the
  coarsest location fact on the machine and one you chose yourself. Halo reads the system switch before
  asking, so a denied app never triggers a prompt and never asks again in that session.

**You control this in Windows, not in Halo**: Settings → Privacy & security → Location. Turn it off and
the banner keeps working, one city wide instead of one street. Halo never uses the exit-IP lookup above
for the weather.

If any of these trades isn't worth it to you, say so in an issue — all of them are good candidates for a
switch.

---

## What Halo never does

- No account and no sign-in — there is nothing to sign in to.
- No analytics, telemetry, usage statistics or crash reporting, in any build.
- Notification text, media titles, file names, download names and clipboard contents **never leave
  your machine**.
- **No update checks and no background downloads.** Halo does not phone home for new versions; it has
  no updater at all. You update it the way you installed it.
- Nothing is sent to the author or to `pvboy.dev`. There is no server behind Halo at all.

---

## Checking any of this yourself

The source is here and builds from it. Every outbound request in the table above is one `grep` away:

```
grep -rn "https\?://" --include="*.cs" src/
```

Something missing or wrong? [Open an issue](https://github.com/phoseinq/Halo/issues).
