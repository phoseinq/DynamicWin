# 08 — Config, autostart, packaging

## Config
- One JSON file at `%APPDATA%\Halo\config.json`. Small.
```json
{
  "monitor": "primary",
  "collapsedBar": "context",      // "context" | "fiveHourLimit"
  "widgets": ["claudeCode", "nowPlaying", "volume", "battery"],
  "notchWidth": 220,
  "accent": "#000000"
}
```
- Load on start, sensible defaults if missing. No settings UI in v1 beyond maybe a tray menu.
  `ponytail: hand-edit JSON is fine for v1; build a settings window only if asked.`

## Autostart
- Startup shortcut in the user's Startup folder (matches how the user runs other tools), or a
  `HKCU\...\Run` entry. One or the other, not both.

## Single instance
- Named mutex on launch; second instance exits (or signals the first to show).

## Packaging
- Self-contained WinUI 3 app (Windows App SDK). Ship a folder or a simple installer.
- `Halo.Hooks.exe` ships alongside and is put on PATH (or referenced by absolute path in the hook
  settings) by a small install step that also merges the hooks block into `~/.claude/settings.json`.

## Before any GitHub push (mandatory)
- **Strip all comments** from shipped source, including every `ponytail:` marker.
- Keep a pre-push script that does the strip so it can't be forgotten (mirrors the user's other
  repos that strip comments before publishing).
- Commits authored as **phoseinq** (per machine policy).
