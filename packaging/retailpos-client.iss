; Grozet Retail POS Client - Dedicated Counter Terminal Packaging Script
; Authoritative Architecture v5.2.9.6 Compliant - Sub-Phase 16.4 Production Packaging

#include "retailpos-common.iss"

#define AppName "Grozet Retail POS Client"

[Setup]
; Deterministic Client Terminal product identity
AppId={{E5A4D3B1-5693-4F8B-A12C-18E2938B7401}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\GROZET\RetailPOS\Client
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=GrozetRetailPOS_Client_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppClientExe}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; Client Mutable Operational Directories (%ProgramData%\GROZET\RetailPOS\Client)
Name: "{commonappdata}\GROZET\RetailPOS\Client"
Name: "{commonappdata}\GROZET\RetailPOS\Client\Config"
Name: "{commonappdata}\GROZET\RetailPOS\Client\Security"
Name: "{commonappdata}\GROZET\RetailPOS\Client\Logs"

[Files]
; Client Published Binaries (Self-Contained win-x64 ReadyToRun)
Source: "..\src\RetailPOS.Client\bin\Release\net10.0\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Cashier / POS Operator shortcuts only (No administrative PowerShell scripts)
Name: "{autoprograms}\{#AppName}\Grozet POS Terminal"; Filename: "{app}\{#AppClientExe}"
Name: "{autodesktop}\Grozet POS Terminal"; Filename: "{app}\{#AppClientExe}"; Tasks: desktopicon

[Run]
; Client Role: Optional post-install application launch
Filename: "{app}\{#AppClientExe}"; Description: "{cm:LaunchProgram,Grozet POS Terminal}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; CRITICAL OWNERSHIP RULE: Inno Setup uninstaller deletes only files listed in unins000.dat.
; Recursive deletions (filesandordirs or {app}\*) are STRICTLY PROHIBITED.
; Only remove directories if they are genuinely empty.
; NEVER delete {commonappdata}\GROZET\RetailPOS\Client! Terminal keys and config are permanently preserved.
Type: dirifempty; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
var
  UnifiedInstalled: Boolean;
begin
  // Environment validation: Ensure 64-bit Windows
  if not Is64BitInstallMode then
  begin
    SuppressibleMsgBox('Grozet Retail POS Client requires a 64-bit Windows operating system (x64). Installation cannot proceed.', mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;

  // Collision detection: Prohibit installation if Unified edition is already registered
  UnifiedInstalled := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}_is1') or
                      RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}_is1');

  if UnifiedInstalled then
  begin
    SuppressibleMsgBox('A Unified installation of Grozet Retail POS is already installed on this computer.' + #13#10 +
                       'Please use the Unified Installer or uninstall the existing edition before installing the standalone Client edition.',
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
    Log('Uninstallation of Client binaries completed. Terminal keys and counter logs in %ProgramData% were strictly preserved.');
  end;
end;
