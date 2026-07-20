<div align="center">

<img src="installer/halo.png" alt="Halo" width="112">

# Halo

**A smooth glass notch for Windows — a Dynamic Island for the desktop.**

<br />

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)
![.NET 9](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-100%25-239120?logo=csharp&logoColor=white)
[![Release](https://img.shields.io/github/v/release/phoseinq/DynamicWin?label=download&color=3fb950&logo=github&logoColor=white)](https://github.com/phoseinq/DynamicWin/releases/latest)

<br />

[Install](#-install) · [Build from source](#-build-from-source) · [How it works](#%EF%B8%8F-how-it-works) · [Report a bug](https://github.com/phoseinq/DynamicWin/issues)

</div>

<br />

Halo turns the top of your screen into a live glass pill — now-playing, downloads, volume, notifications, battery — plus first-class panels for **Claude Code** and **Codex**. It's written from scratch in C# on a Win32 layered window with GDI+, so motion is synced to your monitor's refresh rate and the glass is real: it samples the wallpaper or window behind the pill, not a canned blur. It runs unpackaged, needs no admin, and stays out of your way — solid over the desktop, frosted over apps, gone in fullscreen games.

<br />

## ✨ What you get

- 🔕 **Notifications, blocked and mirrored** — the headline feature. Halo intercepts Windows toasts, **kills the native banner and its sound**, and re-draws it inside the pill instead — one clean notification, on your terms. Windows exposes no official "suppress this toast" API, so Halo does it by writing the authoritative Do-Not-Disturb profile to the registry and nudging `WpnUserService` to re-read it, while still receiving every toast through `UserNotificationListener`. Clicking a banner opens the right app — or the exact chat, for Phone Link.
- 🎵 **Now Playing** — album art, a live seek bar, and transport, with per-source switching (Spotify and a browser video at the same time). A real WASAPI-loopback spectrum equalizer, plus video controls: ±10s, playback speed, and subtitle / picture-in-picture hotkeys. Classic VLC (which publishes no media session) gets its own widget.
- ⬇️ **Downloads** — a filling ring with the live percent for download managers, torrent clients, and Microsoft Store installs (real bytes, read from Delivery Optimization).
- 🤖 **Claude Code & Codex** — live activity and current tool, this-turn tokens, context left, real **5-hour / weekly usage bars** from the live rate-limit headers, an API + internet health ring, and a **Cancel that actually interrupts the running prompt**.
- 🔊 **Volume** · 🔋 **Battery** · 🕘 **Clock** · 🎙️ **Privacy dot** when the mic or camera is live · a language-switch banner · a screenshot & clipboard-image preview.
- 🪟 **Feels native** — real glass over apps, solid black over the desktop, hides on fullscreen games, pin-on-top, drag to move anywhere, and excluded from screen captures.

<br />

## 📦 Install

Grab the latest **[release](https://github.com/phoseinq/DynamicWin/releases/latest)** — pick one:

- **[DynamicWinSetup.exe](https://github.com/phoseinq/DynamicWin/releases/latest)** — installer: a small wizard, no admin needed. Self-contained (no .NET to install) and can start with Windows.
- **[DynamicWinPortable.zip](https://github.com/phoseinq/DynamicWin/releases/latest)** — no install: extract and run `Halo/Halo.App.exe`.

> The build is self-signed, so Windows SmartScreen shows an "unknown publisher" prompt — click **More info → Run anyway**.

<br />

## 🧩 Build from source

**Prerequisites:** .NET 9 SDK · Windows 10 (build 19041) or newer.

```bash
git clone -b Boy https://github.com/phoseinq/DynamicWin
cd DynamicWin
dotnet build Halo.sln -c Release
dotnet run --project src/Halo.App
```

The pill launches at the top-center of your primary monitor. Hover it to expand; drag to move; use the pushpin to keep it above fullscreen apps.

To roll your own release artifacts, run `pwsh installer/build.ps1` — it publishes self-contained, code-signs, and packages `DynamicWinSetup.exe` with Inno Setup. Pass `-Thumbprint <cert>` to sign with your own certificate.

<br />

## 🏗️ How it works

- **Layered-window render** — everything is drawn with GDI+ into a `CreateDIBSection` surface and pushed with `UpdateLayeredWindow` (true premultiplied alpha, 2× supersampled). No WPF, no WinUI — the animation loop is a dispatcher timer that adapts between 30, 60, and 120 fps based on CPU load.
- **Real glass** — the region behind the pill is captured (`BitBlt`, falling back to `PrintWindow` for GPU-composited windows), downscaled, blurred, and tinted under the shape. On the bare desktop it goes solid; in fullscreen it hides entirely.
- **Widgets** — each surface is an `IWidget` (icon, active state, expanded content, a status ring). The controller stacks the active ones into the pill and a side circle, with a liquid drop animation to swap the primary.
- **Agent panels** — a tiny hook binary writes each Claude Code / Codex session's state to a JSON file; the widget watches it live. Usage numbers come from a `max_tokens: 1` probe that reads the real rate-limit headers, so the bars match the CLI exactly.

Full design notes live in [`docs/`](docs/).

<br />

## 🙏 Credits

Halo is an independent, from-scratch take on the desktop-notch idea popularized by **[DynamicWin](https://github.com/FlorianButz/DynamicWin)** by Florian Butz.
