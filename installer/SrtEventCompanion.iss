; SRT Event Companion installer (Inno Setup 6.3 or newer: https://jrsoftware.org/isdl.php)
; Built by installer\build-installer.ps1, which publishes the app and passes /DAppVersion=x.y.z.
;
; - Installs per user (no admin rights) to %LOCALAPPDATA%\Programs\SRT Event Companion
; - Start menu + desktop shortcut, "Start with Windows" (on by default)
; - Silent updates: the app downloads a newer setup, verifies its SHA-256, runs it with
;   /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1 and exits; setup installs and reopens the app.
; - Rig data (pairing key, unsent laps, favourites) lives in %LOCALAPPDATA%\SrtEventCompanion and is kept on uninstall.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "SRT Event Companion"
#define AppPublisher "SRT Labs"
#define AppExe "SRTEventCompanion.exe"
#define AppMutex "SRT.EventCompanion.SingleInstance"
#define PublishDir "..\artifacts\publish"

[Setup]
AppId={{6F3C2A71-5B8E-4D2A-9E7C-3B1F0A9D4E52}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://www.simracingtharavadu.in
AppSupportURL=https://www.simracingtharavadu.in
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=SRT-Event-Companion-Setup-{#AppVersion}
SetupIconFile=..\src\Srt.EventCompanion.Desktop\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UsePreviousTasks=yes
#ifdef Sign
SignTool=srtsign
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start {#AppName} automatically when Windows starts (recommended for rigs)"; GroupDescription: "Rig setup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; First install: offer to open the app.
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent
; Silent update started by the app: reopen it.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ShouldRelaunch

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopCompanion"

[Code]
function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ During an update the app exits right after starting setup. Wait for it rather than failing. }
function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  if WizardSilent() then
    while CheckForMutexes('{#AppMutex}') and (Waited < 60) do
    begin
      Sleep(500);
      Waited := Waited + 1;
    end;

  if CheckForMutexes('{#AppMutex}') then
  begin
    if not WizardSilent() then
      MsgBox('{#AppName} is running. Close it (end any session first), then run setup again.', mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;
