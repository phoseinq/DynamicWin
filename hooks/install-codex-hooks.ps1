param([string]$Repo = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'Halo\hooks'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "Publishing Halo.Hooks -> $dest"
dotnet publish (Join-Path $Repo 'src\Halo.Hooks\Halo.Hooks.csproj') -c Release -o $dest | Out-Null
$exe = Join-Path $dest 'Halo.Hooks.exe'
if (-not (Test-Path $exe)) { throw "publish failed: $exe not found" }

$settingsPath = Join-Path $env:USERPROFILE '.codex\hooks.json'
if (Test-Path $settingsPath) {
    Copy-Item $settingsPath "$settingsPath.halo-bak" -Force
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
    $settings = [ordered]@{}
}
if ($null -eq $settings['hooks']) { $settings['hooks'] = [ordered]@{} }
$hooks = $settings['hooks']

$events = [ordered]@{
    SessionStart     = 'session-start'
    UserPromptSubmit = 'prompt'
    PreToolUse       = 'tool'
    PostToolUse      = 'tool-done'
    PreCompact       = 'pre-compact'
    PostCompact      = 'post-compact'
    Stop             = 'stop'
}

foreach ($hookEvent in $events.Keys) {
    $keptEntries = foreach ($entry in @($hooks[$hookEvent])) {
        if ($null -eq $entry) { continue }
        $keptHandlers = @($entry['hooks'] | Where-Object {
            $command = [string]$_['command']
            $command -notlike '*Halo.Hooks.exe" codex *' -and
            $command -notmatch 'Halo\.Hooks\.exe"\s+(session-start|prompt|tool|tool-done|pre-compact|post-compact|stop)$'
        })
        $entry['hooks'] = $keptHandlers
        if ($keptHandlers.Count -gt 0) { $entry }
    }

    $command = '"{0}" codex {1}' -f $exe, $events[$hookEvent]
    $newEntry = @{ hooks = @(@{ type = 'command'; command = $command }) }
    if ($hookEvent -in 'PreCompact', 'PostCompact') { $newEntry['matcher'] = 'manual|auto' }
    $hooks[$hookEvent] = @($keptEntries) + @($newEntry)
}
$settings['hooks'] = $hooks

$settings | ConvertTo-Json -Depth 30 | Set-Content $settingsPath -Encoding UTF8
Write-Host "Installed Halo Codex hooks into $settingsPath (backup at .halo-bak)."
Write-Host 'Review the changed hook definitions and trust them through /hooks.'
