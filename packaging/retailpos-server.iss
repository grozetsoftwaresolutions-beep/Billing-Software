; Grozet Retail POS Server - Dedicated Server Station Packaging Script
; Authoritative Architecture v5.2.9.6 Compliant - Sub-Phase 16.4 Production Packaging

#include "retailpos-common.iss"

#define AppName "Grozet Retail POS Server"

[Setup]
; Preserved official Phase 14 product identity
AppId={{D37D15A2-3482-4E9A-963C-09B926F14920}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\GROZET\RetailPOS\Server
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=GrozetRetailPOS_Server_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppServerExe}
CloseApplications=yes
RestartApplications=no

[Dirs]
; Server Mutable Operational Directories (%ProgramData%\GROZET\RetailPOS)
; Sub-Phase 16.3 NTFS ACLs are authoritatively applied during post-install by Deploy-WindowsService.ps1
Name: "{commonappdata}\GROZET\RetailPOS"
Name: "{commonappdata}\GROZET\RetailPOS\Database"
Name: "{commonappdata}\GROZET\RetailPOS\Backups"
Name: "{commonappdata}\GROZET\RetailPOS\Security"
Name: "{commonappdata}\GROZET\RetailPOS\Logs"
Name: "{commonappdata}\GROZET\RetailPOS\Staging"
Name: "{commonappdata}\GROZET\RetailPOS\Staging\Quarantine"
Name: "{commonappdata}\GROZET\RetailPOS\Staging\Recovery"

[Files]
; Server Published Binaries (Self-Contained win-x64 ReadyToRun)
Source: "..\src\RetailPOS.Server\bin\Release\net10.0\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Deployment Scripts packaged inside Server installation
Source: "..\scripts\deployment\Deploy-WindowsService.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Run]
; Server Service registration, NTFS ACL isolation, and Firewall configuration
; Reuses authoritative Deploy-WindowsService.ps1 from Sub-Phase 16.3
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Deploy-WindowsService.ps1"" -Action Install -BinaryPath ""{app}\{#AppServerExe}"" -DataRoot ""{commonappdata}\GROZET\RetailPOS"" -HttpPort {#HttpPort} -HttpsPort {#HttpsPort} -DiscoveryPort {#DiscoveryPort}"; StatusMsg: "Configuring Windows Service, NTFS Permissions, and Firewall..."; Flags: runhidden

[UninstallRun]
; Gracefully stop service, remove service from SCM, and remove firewall rules
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Deploy-WindowsService.ps1"" -Action Uninstall -BinaryPath ""{app}\{#AppServerExe}"" -DataRoot ""{commonappdata}\GROZET\RetailPOS"""; Flags: runhidden

[UninstallDelete]
; CRITICAL OWNERSHIP RULE: Inno Setup uninstaller deletes only files listed in unins000.dat.
; Recursive deletions (filesandordirs or {app}\*) are STRICTLY PROHIBITED.
; Only remove directories if they are genuinely empty.
; NEVER delete {commonappdata}\GROZET\RetailPOS! All customer/store data is permanently preserved.
Type: dirifempty; Name: "{app}\scripts"
Type: dirifempty; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
var
  UnifiedInstalled: Boolean;
begin
  // Environment validation: Ensure 64-bit Windows
  if not Is64BitInstallMode then
  begin
    SuppressibleMsgBox('Grozet Retail POS Server requires a 64-bit Windows operating system (x64). Installation cannot proceed.', mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;

  // Collision detection: Prohibit installation if Unified edition is already registered
  UnifiedInstalled := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}_is1') or
                      RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}_is1');

  if UnifiedInstalled then
  begin
    SuppressibleMsgBox('A Unified installation of Grozet Retail POS is already installed on this computer.' + #13#10 +
                       'Please use the Unified Installer or uninstall the existing edition before installing the standalone Server edition.',
                       mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Log('Uninstallation of Server binaries completed. Store databases, backups, and security material in %ProgramData% were strictly preserved.');
  end;
end;
