# installer/build.ps1 — publish self-contained, code-sign, package DynamicWinSetup.exe + DynamicWinPortable.zip.
# Run from anywhere. Swap -Thumbprint for a REAL purchased cert to make signatures trusted on
# other people's PCs (self-signed only clears "Unknown Publisher" where the cert is trusted).
param([string]$Thumbprint = '2EB268F09FEA535E92FB395FA2FAB4409EC22E1D')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$signtool = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' | Sort-Object FullName)[-1].FullName
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"

function Sign([string[]]$files) {
    # timestamp so signatures survive cert expiry; fall back through servers, then unsigned-time
    foreach ($ts in @('http://timestamp.digicert.com', 'http://timestamp.sectigo.com', '')) {
        if ($ts) { & $signtool sign /sha1 $Thumbprint /fd SHA256 /tr $ts /td SHA256 $files }
        else     { & $signtool sign /sha1 $Thumbprint /fd SHA256 $files }
        if ($LASTEXITCODE -eq 0) { return }
    }
    throw "signtool failed for: $files"
}

# Halo is a layered-window tool the Restart Manager can't close — free the locked Release exes first
taskkill /f /im Halo.App.exe      2>$null | Out-Null
taskkill /f /im Halo.Hooks.exe    2>$null | Out-Null
taskkill /f /im Halo.Settings.exe 2>$null | Out-Null

Remove-Item "$root\dist\app" -Recurse -Force -ErrorAction SilentlyContinue
# Halo.Settings belongs in this list, and its absence was invisible: clicking the shortcut while Halo is
# already running calls OpenSettingsPanel, which looks for Halo.Settings.exe beside Halo.App.exe and
# returns without a word when it is not there. So the panel simply never opened, on any install.
foreach ($proj in 'src\Halo.App\Halo.App.csproj', 'src\Halo.Hooks\Halo.Hooks.csproj', 'src\Halo.Settings\Halo.Settings.csproj') {
    dotnet publish "$root\$proj" -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o "$root\dist\app" -v q -nologo
    if ($LASTEXITCODE) { throw "publish failed: $proj" }
}

Sign @("$root\dist\app\Halo.App.exe", "$root\dist\app\Halo.Hooks.exe", "$root\dist\app\Halo.Settings.exe")
& $iscc "$root\installer\Halo.iss"; if ($LASTEXITCODE) { throw 'ISCC failed' }
Sign @("$root\dist\DynamicWinSetup.exe")
& $signtool verify /pa "$root\dist\DynamicWinSetup.exe"

# portable zip = the self-contained app under a Halo\ folder (extract and run Halo\Halo.App.exe)
Remove-Item "$root\dist\Halo", "$root\dist\DynamicWinPortable.zip" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$root\dist\app" "$root\dist\Halo" -Recurse
Compress-Archive -Path "$root\dist\Halo" -DestinationPath "$root\dist\DynamicWinPortable.zip" -CompressionLevel Optimal
Remove-Item "$root\dist\Halo" -Recurse -Force

Write-Host "`nBuilt + signed: dist\DynamicWinSetup.exe + dist\DynamicWinPortable.zip" -ForegroundColor Green
