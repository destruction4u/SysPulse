; Inno Setup 6 script for SysPulse Agent (the Windows service installed on PCs you want to monitor).
; Build with:  powershell -ExecutionPolicy Bypass -File build.ps1

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#define AppName "SysPulse Agent"
#define AppExe "SysPulse.Agent.exe"
#define AppUrl "https://github.com/destruction4u/SysPulse"

[Setup]
AppId={{BC0AFE45-2A0D-41F3-8F82-D5065BA8F0B7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=destruction4u
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
VersionInfoVersion={#AppVersion}

; A Windows service and a firewall rule need administrator rights
PrivilegesRequired=admin
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
; The service is stopped by our own code below, not by Restart Manager
CloseApplications=no

OutputDir=..\dist
OutputBaseFilename=SysPulse-Agent-Setup-{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on this computer.%n%nThe agent runs in the background as a Windows service and lets SysPulse on other PCs in your local network see this PC's health and resource use (read-only).%n%nAfter installing, you'll get a pairing code to enter in SysPulse.

[Files]
Source: "..\dist\agent\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SysPulse Agent - Show pairing code"; Filename: "{app}\{#AppExe}"; Parameters: "--pairing --pause"

[UninstallRun]
; Removes the service, the firewall rule and the stored pairing code/certificate
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAgent"

[Code]
var
  PairingInfo: String;

// Before replacing files on an upgrade, stop the running service.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Exec(ExpandConstant('{app}\{#AppExe}'), '--stop-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// After copying files: register + start the service and open the firewall (the agent does this
// itself so the same logic is used for installs and upgrades), then capture the pairing details.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  OutFile: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  OutFile := ExpandConstant('{tmp}\pairing.txt');
  WizardForm.StatusLabel.Caption := 'Starting the SysPulse Agent service...';
  if not Exec(ExpandConstant('{app}\{#AppExe}'), '--install --out "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    PairingInfo := 'The service could not be set up (error ' + IntToStr(ResultCode) + ').' + #13#10 +
      'Try running "' + ExpandConstant('{app}\{#AppExe}') + ' --install" from an administrator command prompt to see the details.';
    Log(PairingInfo);
    Exit;
  end;
  if LoadStringsFromFile(OutFile, Lines) then
  begin
    PairingInfo := '';
    for I := 0 to GetArrayLength(Lines) - 1 do
      PairingInfo := PairingInfo + Lines[I] + #13#10;
    Log(PairingInfo);
  end;
end;

// Show the pairing details on the last page, in a box the user can select and copy from.
procedure CurPageChanged(CurPageID: Integer);
var
  Memo: TNewMemo;
begin
  if (CurPageID <> wpFinished) or (PairingInfo = '') then Exit;
  WizardForm.FinishedLabel.Caption := 'SysPulse Agent is installed and running.' + #13#10 +
    'You can show these details again from Start > "SysPulse Agent - Show pairing code".';
  WizardForm.FinishedLabel.AutoSize := True;
  Memo := TNewMemo.Create(WizardForm);
  Memo.Parent := WizardForm.FinishedPage;
  Memo.Left := WizardForm.FinishedLabel.Left;
  Memo.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
  Memo.Width := WizardForm.FinishedPage.ClientWidth - Memo.Left - ScaleX(8);
  Memo.Height := WizardForm.FinishedPage.ClientHeight - Memo.Top - ScaleY(8);
  Memo.ReadOnly := True;
  Memo.ScrollBars := ssVertical;
  Memo.Font.Name := 'Consolas';
  Memo.Text := PairingInfo;
end;
