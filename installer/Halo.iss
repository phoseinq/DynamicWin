; Halo installer — public .exe with a wizard UI.
; Build: ISCC.exe installer\Halo.iss  (expects a self-contained publish in dist\app)
; ponytail: per-user install (no UAC), Inno's stock wizard = the UI, one optional autostart task.

#define AppName "Halo"
#define AppVersion "3.2.0"
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
; an upgrade otherwise restores the *previous* run's ticks, so autostart stayed off forever
; for anyone who unticked it once. the defaults below win on every install.
UsePreviousTasks=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; halo.ico's small sizes must stay BMP/DIB entries — a fully PNG-compressed .ico left this .exe with no
; icon at all in Explorer and in browser download lists. installer/make_icon.py writes it.
SetupIconFile=halo.ico
WizardSmallImageFile=wizard-small.bmp
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
; both default to checked, and stay checked on upgrades — `checkedonce` used to drop the Codex
; task on the second install, and UsePreviousTasks would have carried a one-off untick forever.
Name: "codexhooks"; Description: "Integrate with Codex"; GroupDescription: "Integrations:"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
; NOT a Startup-folder shortcut any more. Explorer holds those back until the desktop has settled and
; then releases them one at a time at reduced priority, so the pill was arriving last on every boot.
; install-autostart registers a logon-triggered scheduled task instead (see Halo.Hooks\Autostart.cs) and
; deletes the old shortcut on upgrade, so this is fixed for everyone, not just on the machine it was
; noticed on.

[Run]
Filename: "{app}\Halo.Hooks.exe"; Parameters: "install-autostart ""{app}\{#AppExe}"""; StatusMsg: "Setting Halo to start with Windows..."; Tasks: startup; Flags: runhidden waituntilterminated
; unticking the box on an upgrade has to actually take the task away — otherwise the old one keeps
; launching Halo and the tick looks like it did nothing
Filename: "{app}\Halo.Hooks.exe"; Parameters: "uninstall-autostart"; Tasks: not startup; Flags: runhidden waituntilterminated
Filename: "{app}\Halo.Hooks.exe"; Parameters: "install-codex-hooks ""{app}\Halo.Hooks.exe"""; StatusMsg: "Configuring Codex integration..."; Tasks: codexhooks; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Halo.Hooks.exe"; Parameters: "uninstall-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "HaloAutostart"
Filename: "{app}\Halo.Hooks.exe"; Parameters: "uninstall-codex-hooks"; Flags: runhidden waituntilterminated; RunOnceId: "HaloCodexHooks"

[Code]
// Halo is a layered-window tool with no normal window, so the Restart Manager can't close it.
// Kill any running instance before copying files (so updates don't hit locked exes).
// This named Halo.exe, which is not a process this product has ever had — the pill is Halo.App.exe — so
// upgrades were copying over a live, locked executable. Halo.Settings.exe holds the same lock when the
// panel happens to be open.
procedure KillHalo;
var rc: Integer;
begin
  Exec('taskkill.exe', '/f /im {#AppExe}',         '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec('taskkill.exe', '/f /im Halo.Hooks.exe',    '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec('taskkill.exe', '/f /im Halo.Settings.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
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
