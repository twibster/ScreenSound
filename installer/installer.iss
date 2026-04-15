; Inno Setup script for Audio Monitor Router
; Builds a Windows installer that installs to Program Files, creates Start Menu
; shortcuts, registers an uninstaller, and launches the app after install.
;
; This installer ships a framework-dependent .NET 8 build (~5-8 MB) and
; downloads + installs the .NET 8 Desktop Runtime on-demand if missing,
; keeping our installer ~10x smaller than a self-contained build.

#define MyAppName "Audio Monitor Router"
#define MyAppExeName "AudioMonitorRouter.exe"
#define MyAppPublisher "twibster"
#define MyAppURL "https://github.com/twibster/AudioMonitorRouter"

; Version comes from the APP_VERSION env var (set by CI). Fallback for local runs.
#define MyAppVersion GetEnv("APP_VERSION")
#if MyAppVersion == ""
  #define MyAppVersion "1.0.0"
#endif

; Pinned .NET 8 Desktop Runtime download. Bump this on major .NET 8 patch releases.
; The installed-version check only requires 8.0.x so end users running an older
; 8.0 patch won't be forced to re-download.
#define DotNet8RuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.15/windowsdesktop-runtime-8.0.15-win-x64.exe"

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
; Ship the whole framework-dependent publish folder (exe + dlls + runtimeconfig)
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb,*.zip"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Opt-in auto-start via Run key. App's own "Run at startup" setting can override this later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AudioMonitorRouter"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
; Silent-install the .NET 8 Desktop Runtime if we had to download it.
; Runs before our app is launched.
Filename: "{tmp}\windowsdesktop-runtime-8-x64.exe"; Parameters: "/install /quiet /norestart"; \
  StatusMsg: "Installing .NET 8 Desktop Runtime..."; \
  Check: NeedsDotNet8Install; Flags: waituntilterminated
; Nudge Explorer to re-read icon metadata for this exe. Without this,
; users who pinned the app to the taskbar before updating keep seeing the
; previous icon until Explorer's cache rolls over naturally (often a
; reboot away). ie4uinit -show is the documented, silent way to trigger a
; refresh; runhidden avoids flashing a console window at the user.
; runasoriginaluser matters for over-the-shoulder UAC installs: without it
; this inherits the installer's elevated admin token and refreshes the
; admin's icon cache instead of the signed-in user's Explorer.
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden skipifdoesntexist runasoriginaluser
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Gracefully close the app before uninstall
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; Remove the per-user settings folder on uninstall
Type: filesandordirs; Name: "{userappdata}\AudioMonitorRouter"

[Code]
var
  DownloadPage: TDownloadWizardPage;

// Check if any 8.0.x .NET Desktop Runtime is installed.
// Path: C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*
function IsDotNet8DesktopInstalled: Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if FindFirst(BasePath + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Used by [Run] Check: clause — only runs the runtime installer if we downloaded it.
function NeedsDotNet8Install: Boolean;
begin
  Result := not IsDotNet8DesktopInstalled;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
                                     'Downloading .NET 8 Desktop Runtime...',
                                     nil);
end;

// Queue the .NET 8 runtime download right before file copy starts, so the user
// sees a progress bar and we don't block the whole installer if they're offline.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not IsDotNet8DesktopInstalled then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#DotNet8RuntimeUrl}', 'windowsdesktop-runtime-8-x64.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        Result := True;
      except
        SuppressibleMsgBox(
          'Failed to download the .NET 8 Desktop Runtime.' + #13#10 +
          'Please install it manually from:' + #13#10 +
          'https://dotnet.microsoft.com/download/dotnet/8.0' + #13#10 + #13#10 +
          'Error: ' + GetExceptionMessage,
          mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
