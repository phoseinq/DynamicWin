; Halo installer — public .exe with a wizard UI.
; Build: ISCC.exe installer\Halo.iss  (expects a self-contained publish in dist\app)
; ponytail: per-user install (no UAC), Inno's stock wizard = the UI, one optional autostart task.

#define AppName "Halo"
#define AppVersion "3.1.0"
#define AppPublisher "phoseinq"
#define AppExe "Halo.App.exe"

[Setup]
AppId={{9DF33B04-E81D-443D-AC23-A24049170FD9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; per-user: installs to %LocalAppData%\Programs\Halo, no admin prompt
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=halo.ico
OutputDir=..\dist
OutputBaseFilename=DynamicWinSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} automatically when Windows starts"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "codexhooks"; Description: "Integrate with Codex"; GroupDescription: "Integrations:"; Flags: checkedonce

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
; autostart = a shortcut in the user's Startup folder (matches how the app expects to be launched)
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\Halo.Hooks.exe"; Parameters: "install-codex-hooks ""{app}\Halo.Hooks.exe"""; StatusMsg: "Configuring Codex integration..."; Tasks: codexhooks; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Halo.Hooks.exe"; Parameters: "uninstall-codex-hooks"; Flags: runhidden waituntilterminated; RunOnceId: "HaloCodexHooks"

[Code]
// Halo is a layered-window tool with no normal window, so the Restart Manager can't close it.
// Kill any running instance before copying files (so updates don't hit locked exes).
procedure KillHalo;
var rc: Integer;
begin
  Exec('taskkill.exe', '/f /im Halo.exe',      '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec('taskkill.exe', '/f /im Halo.Hooks.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillHalo;
  Result := '';
end;

procedure CurUninstallStepChanged(CurStep: TUninstallStep);
begin
  if CurStep = usUninstall then
    KillHalo;
end;
