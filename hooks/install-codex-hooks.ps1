param([string]$Repo = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$installedExe = Join-Path $env:LOCALAPPDATA 'Programs\Halo\Halo.Hooks.exe'
if (Test-Path $installedExe) {
    $exe = $installedExe
    Write-Host "Using installed Halo.Hooks -> $exe"
} else {
    $dest = Join-Path $env:LOCALAPPDATA 'Halo\hooks'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Write-Host "Publishing Halo.Hooks -> $dest"
    dotnet publish (Join-Path $Repo 'src\Halo.Hooks\Halo.Hooks.csproj') -c Release -o $dest | Out-Null
    $exe = Join-Path $dest 'Halo.Hooks.exe'
    if (-not (Test-Path $exe)) { throw "publish failed: $exe not found" }
}

$settingsPath = Join-Path $env:USERPROFILE '.codex\hooks.json'
$previousSettingsPath = $env:HALO_CODEX_HOOKS_PATH
try {
    $env:HALO_CODEX_HOOKS_PATH = $settingsPath
    & $exe install-codex-hooks $exe
    if ($LASTEXITCODE -ne 0) { throw "Halo.Hooks setup failed: $LASTEXITCODE" }
} finally {
    $env:HALO_CODEX_HOOKS_PATH = $previousSettingsPath
}

Write-Host "Installed Halo Codex hooks into $settingsPath (backup at .halo-bak)."
Write-Host 'Review the changed hook definitions and trust them through /hooks.'
