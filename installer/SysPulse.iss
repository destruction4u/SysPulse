; Inno Setup 6 script for SysPulse.
; Build with:  powershell -ExecutionPolicy Bypass -File build.ps1
; (build.ps1 publishes the app to dist\app and passes the version in.)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "SysPulse"
#define AppExe "SysPulse.exe"
#define AppUrl "https://github.com/destruction4u/SysPulse"

[Setup]
; Keep AppId the same forever: it is how upgrades and the uninstaller find the existing install.
AppId={{CD24E76E-5241-4F53-A500-6BE2A3059A3A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=destruction4u
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user install by default (no admin prompt); user can choose "all users" in a dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

SetupIconFile=..\src\SysPulse\Assets\SysPulse.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes

; Close a running SysPulse before upgrading/uninstalling. "force" because the app may be hidden in the tray
; (it exits cleanly on the session-end message, but force guarantees the files are released).
CloseApplications=force
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=SysPulse-Setup-{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start SysPulse when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\dist\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme skipifsourcedoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{autostartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// SysPulse can run hidden in the tray with no window, which Windows' "close app" request
// (Restart Manager) can't reach. So ask it to quit via its own "--exit" command, which
// signals the running copy and waits until it has exited. 1.0.x predates --exit (and
// always has a window, so Restart Manager handles it).
procedure CloseRunningSysPulse();
var
  Exe, Ver: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(Exe) then Exit;
  if GetVersionNumbersString(Exe, Ver) and (Pos('1.0.', Ver) = 1) then Exit;
  Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningSysPulse();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningSysPulse();
  Result := True;
end;
