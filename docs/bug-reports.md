# Bug report delivery — design

Status: **design agreed, not implemented.** Supersedes unit D's "no automatic upload", which was written
before the user asked for delivery at all. Reported 2026-08-02, redone 2026-08-03.

## What was asked

Crash reports *and* user-triggered ones should reach somewhere — email or the user's own server — instead
of dying in `%TEMP%\halo-crash.log`. Stated constraint: proper security, and within Microsoft's rules.
Spelled out by the user, and these are hard requirements, not preferences:

- nothing is sent without the user pressing something
- the user sees **exactly** what is being sent, before it goes
- no credentials or tokens in the payload
- no silent background upload

## The constraint that actually shapes this

Halo is not an ordinary app to collect diagnostics from. It mirrors **other people's** notifications, the
title of whatever is playing, the names of files dropped in the tray, and live Claude/Codex session text.
A conventional "attach the log and the app state" report would put a stranger's message body, a chat name,
a filename, or a prompt into a file the user then uploads. That is the whole design problem here; the
transport is the easy half.

So the payload is an **allowlist**. Fields are added one at a time, by name, and anything not on the list
is absent by construction. A blacklist ("strip notification bodies") is rejected: it fails open, and every
new widget silently becomes a new leak.

### On the list

| Field | Why it is safe |
|-------|----------------|
| Halo version, commit id | ours |
| Windows build, DPI/scale, monitor resolution + refresh | machine shape, not content |
| .NET runtime version | ours |
| Exception type, message, stack | see the path note below |
| Which widget was primary; which surfaces were live (bool/enum) | shape, never titles |
| Frame rate tier, `_heavy`, whether the pill was expanded | shape |
| The user's own typed description | they wrote it, in front of them |

### Never on the list

Notification titles/bodies/app names; media title/artist/album; tray file names or paths; agent prompts,
transcripts or tool arguments; window titles; the local API token; the settings file verbatim; the
contents of any file.

**Stack traces get their paths reduced to the file name.** `C:\Users\<name>\OneDrive\...\MediaWidget.cs`
carries the user's account name and their folder layout; `MediaWidget.cs:1848` is just as useful for
debugging and carries neither.

## Shape

Two entry points, one pipeline, one preview, one press.

1. **Crash.** The existing handler in `Program.Main` keeps writing, but to
   `%LOCALAPPDATA%\Halo\reports\crash-<timestamp>.json` rather than `%TEMP%` — `%TEMP%` is swept, and a
   report that has been deleted before the user next opens the app cannot be offered to them. It does
   **not** send, and it does not send on next launch either: the next launch notices the file and offers a
   row in settings.
2. **Manual.** A "report a problem" action in the settings panel, which assembles the same structure minus
   the exception.
3. **Preview.** The report is written as indented JSON, human-readable on purpose, and the UI shows the
   file itself — with "open in Notepad" as the escape hatch. **The bytes previewed are the bytes sent.**
   No second envelope is assembled at send time, because then the preview would be a claim about the
   payload rather than the payload.
4. **Send** is a button. Never a timer, never a launch-time action, never a retry queue that drains in the
   background.

## Transport

**Email is rejected as a transport.** SMTP from a desktop client needs credentials in the binary, where
they are extractable by anyone who downloads the installer — which fails "no credentials" outright — and
`mailto:` cannot carry an attachment reliably across mail clients. "Email it to me" is served instead by
*Save as file* plus the user's own mail client, which is the same outcome with none of the secret.

What ships:

- **Always, and with no server involved:** *Copy report*, *Save as file…*, and *Open a GitHub issue*
  (opens the browser with the body prefilled). These need no endpoint, no key, and no network permission,
  and they are the entire feature when no endpoint is configured. This is the default.
- **Optionally, the user's own server:** an HTTPS `POST` to an endpoint set in settings. The client holds
  no shared secret. If the endpoint wants authentication, it issues a per-install key on first contact
  which is stored in the settings file and sent as a header — never in the report body, so it cannot
  survive into a report the user forwards to someone else.

Failure is reported to the user's face: a failed POST leaves the report on disk and says so. No silent
retry, because a retry queue is a background upload wearing a different hat.

## Microsoft's rules

Halo is unpackaged, so Store policy does not bind it. The parts worth honouring anyway, and which the
above already satisfies: consent before any transmission, a privacy statement the user can read, no
telemetry bundled with a bug report, and uninstall removing what the app stored. `BannerGate`'s
`--restore-notifications` already set the precedent that the uninstaller reverses what the app changed —
report files join that: capped at 10 files / 2 MB locally, and deleted on uninstall.

## Open

- Whether to ship a default endpoint at all, or leave it blank so the feature is local-only until the user
  fills it in. Blank is the safer default and is what this doc assumes.
- Whether the crash path may show a pill notice, or must wait until settings is opened. A notice is not a
  transmission, so it is probably fine, but it is the user's call.
