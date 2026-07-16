param([string]$Repo = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'Halo\hooks'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "Publishing Halo.Hooks -> $dest"
dotnet publish (Join-Path $Repo 'src\Halo.Hooks\Halo.Hooks.csproj') -c Release -o $dest | Out-Null
$exe = Join-Path $dest 'Halo.Hooks.exe'
if (-not (Test-Path $exe)) { throw "publish failed: $exe not found" }

$settingsPath = Join-Path $env:USERPROFILE '.claude\settings.json'
if (Test-Path $settingsPath) {
    Copy-Item $settingsPath "$settingsPath.halo-bak" -Force
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
    $settings = @{}
}
if (-not $settings.ContainsKey('hooks')) { $settings['hooks'] = @{} }
$hooks = $settings['hooks']

$map = [ordered]@{
    SessionStart     = 'session-start'
    UserPromptSubmit = 'prompt'
    PreToolUse       = 'tool'
    PostToolUse      = 'tool-done'
    Notification     = 'notify'
    PreCompact       = 'pre-compact'
    Stop             = 'stop'
    SessionEnd       = 'session-end'
}

foreach ($evt in $map.Keys) {
    $cmd = '"{0}" {1}' -f $exe, $map[$evt]
    $entry = @{ hooks = @(@{ type = 'command'; command = $cmd }) }
    if (-not $hooks.ContainsKey($evt)) { $hooks[$evt] = @() }
    $hooks[$evt] = @($hooks[$evt] | Where-Object { -not (($_.hooks.command) -like '*Halo.Hooks*') })
    $hooks[$evt] += $entry
}
$settings['hooks'] = $hooks

$settings | ConvertTo-Json -Depth 30 | Set-Content $settingsPath -Encoding UTF8
Write-Host "Installed Halo hooks into $settingsPath (backup at .halo-bak)."
Write-Host "Start a new Claude Code session; the Halo notch will reflect it."
