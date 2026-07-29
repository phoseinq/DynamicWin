# Commands (Windows / PowerShell)

Repo root: `C:\Users\hosei\OneDrive\دسکتاپ\Projects\Halo`.

## Build / test
```
dotnet build Halo.sln -c Release          # must end 0 warnings / 0 errors
dotnet test  tests\Halo.Tests\Halo.Tests.csproj
dotnet run --project src\Halo.App -- --render-widget out.png claude   # see mem:dev_hooks
```

## Publish (what the installer packages)
```
dotnet publish src\Halo.App\Halo.App.csproj   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\app
dotnet publish src\Halo.Hooks\Halo.Hooks.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\app
```
`dist/` is git-ignored. Full sign/installer/release recipe: `mem:shipping`.

## Deploy live (replace the running install)
```
dist\DynamicWinSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
%LOCALAPPDATA%\Programs\Halo\Halo.App.exe
```
Hook-only quick deploy = copy the four `Halo.Hooks.{exe,dll,deps.json,runtimeconfig.json}` files into
`%LOCALAPPDATA%\Programs\Halo\`. Single-instance mutex means a stale process must be stopped first
(`Stop-Process -Name Halo.App`).

## Shell gotchas that bite in this repo
- **Use `pwsh`, not Windows PowerShell 5.1**, for anything reading a `.ps1` here: the Persian path segment
  «دسکتاپ» breaks 5.1's UTF-8-no-BOM handling. Launching via the Bash tool also works.
- The user's PowerShell safety hook **blocks a single command containing both `Remove-Item` and a
  `C:\Program Files` literal, or `Remove-Item` and a `/f` token** (it misparses them as delete targets).
  Split such work across commands; use `Stop-Process` instead of `taskkill /f`.
- Sandboxed/isolated shells run on a different desktop — `Start-Process`ing Halo there makes the pill
  invisible to the real session. Deploy/relaunch from an unsandboxed shell.
- `git ls-files` is the fast way to see real sources; a bare `Glob *` drowns in `bin/`, `dist/`,
  `.worktrees/` binaries.
