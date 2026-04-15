; Inno Setup script for Audio Monitor Router
; Builds a Windows installer that installs to Program Files, creates Start Menu
; shortcuts, registers an uninstaller, and launches the app after install.

#define MyAppName "Audio Monitor Router"
#define MyAppExeName "AudioMonitorRouter.exe"
#define MyAppPublisher "twibster"
#define MyAppURL "https://github.com/twibster/AudioMonitorRouter"

; Version comes from the APP_VERSION env var (set by CI). Fallback for local runs.
#define MyAppVersion GetEnv("APP_VERSION")
#if MyAppVersion == ""
  #define MyAppVersion "1.0.0"
#endif

[Setup]
; AppId is a GUID that uniquely identifies the app for uninstall. NEVER change this
; once a release has shipped or upgrades will install side-by-side instead of replacing.
AppId={{F4B8A24A-3C7E-4D88-9F1C-B1D0A8E3E921}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; x64-only — the app depends on Windows 10+ x64 audio APIs
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763

PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=Output
OutputBaseFilename=AudioMonitorRouter-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\AudioMonitorRouter\app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start {#MyAppName} when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\publish\AudioMonitorRouter.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Opt-in auto-start via Run key. App's own "Run at startup" setting can override this later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AudioMonitorRouter"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Gracefully close the app before uninstall
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; Remove the per-user settings folder on uninstall
Type: filesandordirs; Name: "{userappdata}\AudioMonitorRouter"
