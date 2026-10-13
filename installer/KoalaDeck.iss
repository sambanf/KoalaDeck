; Koala Deck installer (Inno Setup 6).
; Build everything with:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
; The payload is the self-contained single-file exe from the win-x64 publish profile.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Koala Deck"
#define AppExe "KoalaDeck.exe"
#define RepoRoot ".."

[Setup]
; Never change AppId: it links upgrades and the uninstaller to earlier versions.
AppId={{3775A06A-D4FB-47B0-8EEA-A33025322655}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Koala Deck
VersionInfoVersion={#AppVersion}
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

; Close a running Koala Deck before replacing it (same mutex the app uses for single-instance).
AppMutex=KoalaDeck.SingleInstance
CloseApplications=yes
RestartApplications=no

OutputDir={#RepoRoot}\artifacts\installer
OutputBaseFilename=KoalaDeck-Setup-{#AppVersion}
SetupIconFile={#RepoRoot}\src\KoalaDeck\Assets\KoalaDeck.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start Koala Deck in the tray when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#RepoRoot}\artifacts\publish\win-x64\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

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

; Settings (%AppData%\KoalaDeck) and logs (%LocalAppData%\KoalaDeck) are kept on uninstall, so a reinstall keeps the groups.
