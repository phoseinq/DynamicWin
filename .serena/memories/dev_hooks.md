# Dev / CLI hooks on `Halo.App`

The pill is `WDA_EXCLUDEFROMCAPTURE`, so **screenshots of the running app are useless for verification**
(you get the window behind it → false "it works" reads). These argv hooks in `Program.Main` render the real
code paths to a PNG you can open instead. Add a new one whenever you build UI that needs eyeballing.

| Hook | Purpose |
|------|---------|
| `--render-widget <out.png> [media\|claude\|codex]` | one widget's expanded panel |
| `--render-notif <out.png>` | notification banner via the real shape path, colourful backdrop, mixed Persian+English (catches edge fringes + RTL) |
| `--render-pin <out.png>` | pushpin states in isolation |
| `--render-badges <out.png>` | the generated local-notification badges — catches tofu glyphs |
| `--probe-icon <aumid>` | what each notif icon resolver returns for an app id |
| `--probe-tree <pid>` | the process's ancestor chain via Toolhelp (agent/console detection) |
| `--probe-spectrum` | 6s of loopback audio band values |
| `--restore-notifications` | un-silences every app Halo learned; run from the uninstaller |

Env knobs: `HALO_CAPTURABLE=1` skips `WDA_EXCLUDEFROMCAPTURE` (needed to record the README gif — done with
ffmpeg `ddagrab`). `HALO_CLAUDE_SURFACE` overrides CLI-vs-desktop detection in `Halo.Hooks`.

Other verification levers: reflection tests against the built dll, standalone GDI+ harnesses, and
`tests/Halo.Tests` for anything that can be made pure. Crash log: `%TEMP%\halo-crash.log`.
