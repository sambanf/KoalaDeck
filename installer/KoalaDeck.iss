; Koala Deck installer (Inno Setup 6).
; Build everything with:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
; Payload: the self-contained app folder from the "installer" publish profile (no .NET install needed).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Koala Deck"
#define AppExe "KoalaDeck.exe"
#define RepoRoot ".."
#define PayloadDir RepoRoot + "\artifacts\publish\installer"

[Setup]
; Never change AppId: it links upgrades and the uninstaller to earlier versions.
AppId={{3775A06A-D4FB-47B0-8EEA-A33025322655}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Koala Deck
AppCopyright=Copyright (C) 2026 Koala Deck
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; Per-user install into %LocalAppData%\Programs — no admin rights, like the app itself.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 or newer (per-app audio routing needs 1803+).
MinVersion=10.0.17763

; A running Koala Deck is asked to exit by [Code] below; Restart Manager is only the fallback.
CloseApplications=yes
RestartApplications=no

OutputDir={#RepoRoot}\artifacts\installer
OutputBaseFilename=KoalaDeck-Setup-{#AppVersion}
SetupIconFile={#RepoRoot}\src\KoalaDeck\Assets\KoalaDeck.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSmallImageFile=wizard-small-100.bmp,wizard-small-150.bmp,wizard-small-200.bmp
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start Koala Deck in the tray when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Version 1.0.0 shipped a single self-extracting exe; clear the app folder so no stale runtime files mix with the
; new ones. Only the uninstaller (unins*) is kept.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The same Run entry the app's "Start with Windows" toggle writes, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "KoalaDeck"; \
    ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart
; Always remove that entry on uninstall, also when it was switched on from inside the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "KoalaDeck"; \
    Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  { Shared with src\KoalaDeck\Services\SingleInstance.cs (session-local "Local\" names). }
  AppMutexName = 'KoalaDeck.SingleInstance';
  ExitEventName = 'KoalaDeck.Exit';
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(DesiredAccess: Cardinal; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ Asks a running Koala Deck to exit (it saves its settings and releases the COM port), then waits up to 10 s. }
function AskAppToExit(): Boolean;
var
  Event: THandle;
  Waited: Integer;
begin
  Result := True;
  if not CheckForMutexes(AppMutexName) then
    Exit;

  Log('Koala Deck is running; asking it to exit');
  Event := OpenEvent(EVENT_MODIFY_STATE, False, ExitEventName);
  if Event <> 0 then
  begin
    SetEvent(Event);
    CloseHandle(Event);
  end;

  Waited := 0;
  while CheckForMutexes(AppMutexName) and (Waited < 10000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not CheckForMutexes(AppMutexName);
  if Result then
    Log('Koala Deck exited');
end;

{ Closes Koala Deck; if it does not react (e.g. a build without the exit signal) the user closes it from the tray. }
function EnsureAppClosed(): Boolean;
begin
  Result := AskAppToExit();
  while not Result do
  begin
    if SuppressibleMsgBox('Koala Deck is still running.' + #13#10#13#10 +
        'Right-click the koala icon in the system tray, choose Exit, then click Retry.',
        mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
      Exit;
    Result := AskAppToExit();
  end;
end;

{ Runs after the user clicks Install, so merely opening Setup never closes the app. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if EnsureAppClosed() then
    Result := ''
  else
    Result := 'Koala Deck is still running. Exit it from the system tray and run Setup again.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    EnsureAppClosed();

  if (CurUninstallStep = usPostUninstall) and
     (SuppressibleMsgBox('Also remove your Koala Deck settings (channel groups, COM port) and logs?' + #13#10#13#10 +
        'Choose No to keep them for a later reinstall.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
  begin
    DelTree(ExpandConstant('{userappdata}\KoalaDeck'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\KoalaDeck'), True, True, True);
  end;
end;
