; Grozet Retail POS - Unified Single-Station Packaging Script
; Authoritative Architecture v5.2.9.6 Compliant - Sub-Phase 16.4 Production Packaging

#include "retailpos-common.iss"

#define AppName "Grozet Retail POS"

[Setup]
; New deterministic Unified Single-Station product identity
AppId={{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\GROZET\RetailPOS
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=GrozetRetailPOS_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Server\{#AppServerExe}
CloseApplications=yes
RestartApplications=no

[Types]
Name: "full"; Description: "Full Station (Server Service & POS Terminal - Recommended for Single Counter or Server PC)"
Name: "server"; Description: "Server Station (Windows Service, Central Database & Multi-Counter Sync)"
Name: "client"; Description: "Counter Terminal Station (POS Client Application only)"
Name: "custom"; Description: "Custom Installation"; Flags: iscustom

[Components]
Name: "server"; Description: "Grozet Retail POS Server Service (Authoritative local database, background services, counter sync)"; Types: full server
Name: "client"; Description: "Grozet Retail POS Counter Terminal (Cashier billing and store operations UI)"; Types: full client

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Components: client

[Dirs]
; Server Mutable Operational Directories (%ProgramData%\GROZET\RetailPOS)
Name: "{commonappdata}\GROZET\RetailPOS"; Components: server
Name: "{commonappdata}\GROZET\RetailPOS\Database"; Components: server
Name: "{commonappdata}\GROZET\RetailPOS\Backups"; Components: server
Name: "{commonappdata}\GROZET\RetailPOS\Security"; Components: server
Name: "{commonappdata}\GROZET\RetailPOS\Logs"; Components: server
Name: "{commonappdata}\GROZET\RetailPOS\Staging"; Components: server
Name: "{commonappdata}\GROZET\RetailPOS\Staging\Quarantine"; Components: server
Name: "{commonappdata}\GROZET\RetailPOS\Staging\Recovery"; Components: server

; Client Mutable Operational Directories (%ProgramData%\GROZET\RetailPOS\Client)
Name: "{commonappdata}\GROZET\RetailPOS\Client"; Components: client
Name: "{commonappdata}\GROZET\RetailPOS\Client\Config"; Components: client
Name: "{commonappdata}\GROZET\RetailPOS\Client\Security"; Components: client
Name: "{commonappdata}\GROZET\RetailPOS\Client\Logs"; Components: client

[Files]
; Server Published Binaries (Self-Contained win-x64 ReadyToRun)
Source: "..\src\RetailPOS.Server\bin\Release\net10.0\publish\win-x64\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: server

; Deployment Scripts packaged alongside Server
Source: "..\scripts\deployment\Deploy-WindowsService.ps1"; DestDir: "{app}\Server\scripts"; Flags: ignoreversion; Components: server

; Client Published Binaries (Self-Contained win-x64 ReadyToRun)
Source: "..\src\RetailPOS.Client\bin\Release\net10.0\publish\win-x64\*"; DestDir: "{app}\Client"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: client

[Icons]
; Client Shortcuts
Name: "{autoprograms}\{#AppName}\Grozet POS Terminal"; Filename: "{app}\Client\{#AppClientExe}"; Components: client
Name: "{autodesktop}\Grozet POS Terminal"; Filename: "{app}\Client\{#AppClientExe}"; Tasks: desktopicon; Components: client

[Run]
; Server Role: Automate Windows Service registration, ACL isolation, and Firewall configuration
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Server\scripts\Deploy-WindowsService.ps1"" -Action Install -BinaryPath ""{app}\Server\{#AppServerExe}"" -DataRoot ""{commonappdata}\GROZET\RetailPOS"" -HttpPort {#HttpPort} -HttpsPort {#HttpsPort} -DiscoveryPort {#DiscoveryPort}"; StatusMsg: "Configuring Windows Service, NTFS Permissions, and Firewall..."; Flags: runhidden; Components: server

; Client Role: Optional post-install launch
Filename: "{app}\Client\{#AppClientExe}"; Description: "{cm:LaunchProgram,Grozet POS Terminal}"; Flags: nowait postinstall skipifsilent; Components: client

[UninstallRun]
; Server Role: Gracefully stop service, remove service from SCM, and remove firewall rules
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Server\scripts\Deploy-WindowsService.ps1"" -Action Uninstall -BinaryPath ""{app}\Server\{#AppServerExe}"" -DataRoot ""{commonappdata}\GROZET\RetailPOS"""; Flags: runhidden; Components: server

[UninstallDelete]
; CRITICAL OWNERSHIP RULE: Inno Setup uninstaller deletes only files listed in unins000.dat.
; Recursive deletions (filesandordirs or {app}\*) are STRICTLY PROHIBITED.
; Only remove directories if they are genuinely empty.
; NEVER delete {commonappdata}\GROZET\RetailPOS! All customer/store data is permanently preserved.
Type: dirifempty; Name: "{app}\Server\scripts"
Type: dirifempty; Name: "{app}\Server"
Type: dirifempty; Name: "{app}\Client"
Type: dirifempty; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
var
  ServerInstalled: Boolean;
  ClientInstalled: Boolean;
begin
  // Environment validation: Ensure 64-bit Windows
  if not Is64BitInstallMode then
  begin
    SuppressibleMsgBox('Grozet Retail POS requires a 64-bit Windows operating system (x64). Installation cannot proceed.', mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;

  // Collision detection: Prohibit installation if Dedicated Server or Dedicated Client is already registered
  ServerInstalled := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{D37D15A2-3482-4E9A-963C-09B926F14920}_is1') or
                     RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{D37D15A2-3482-4E9A-963C-09B926F14920}_is1');

  ClientInstalled := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E5A4D3B1-5693-4F8B-A12C-18E2938B7401}_is1') or
                     RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E5A4D3B1-5693-4F8B-A12C-18E2938B7401}_is1');

  if ServerInstalled or ClientInstalled then
  begin
    SuppressibleMsgBox('A standalone Server or Client installation of Grozet Retail POS was detected on this computer.' + #13#10 +
                       'Please uninstall the standalone package before installing the Unified edition.',
                       mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectComponents then
  begin
    if not IsComponentSelected('server') and not IsComponentSelected('client') then
    begin
      SuppressibleMsgBox('You must select at least one component to install: Server or Counter Terminal.', mbError, MB_OK, MB_OK);
      Result := False;
    end;
  end;
end;

function StopServerServiceWithBoundedPolling(): String;
var
  ResultCode: Integer;
  LoopCount: Integer;
  QueryOutput: AnsiString;
  TempFilePath: String;
begin
  Result := '';
  TempFilePath := ExpandConstant('{tmp}\scm_query.txt');

  // Check if service exists
  if Exec('cmd.exe', '/C sc.exe query GrozetPOSServer > "' + TempFilePath + '" 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringFromFile(TempFilePath, QueryOutput) then
    begin
      // 1060 indicates ERROR_SERVICE_DOES_NOT_EXIST
      if Pos('1060', QueryOutput) > 0 then
      begin
        DeleteFile(TempFilePath);
        Exit;
      end;
      // If already stopped, proceed
      if Pos('STOPPED', QueryOutput) > 0 then
      begin
        DeleteFile(TempFilePath);
        Exit;
      end;
    end;
  end;

  // Signal service stop
  Exec('sc.exe', 'stop GrozetPOSServer', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Bounded polling loop: up to 60 iterations with 500ms sleep = 30s timeout
  for LoopCount := 1 to 60 do
  begin
    Sleep(500);
    if Exec('cmd.exe', '/C sc.exe query GrozetPOSServer > "' + TempFilePath + '" 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if LoadStringFromFile(TempFilePath, QueryOutput) then
      begin
        if Pos('STOPPED', QueryOutput) > 0 then
        begin
          DeleteFile(TempFilePath);
          Exit;
        end;
      end;
    end;
  end;

  DeleteFile(TempFilePath);
  Result := 'Grozet POS Server service failed to stop within 30 seconds. Upgrade aborted safely.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := StopServerServiceWithBoundedPolling();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Log('Uninstallation completed. Store databases, backups, and security material in %ProgramData% were strictly preserved.');
  end;
end;
