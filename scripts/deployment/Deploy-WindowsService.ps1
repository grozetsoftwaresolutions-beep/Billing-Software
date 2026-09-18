<#
.SYNOPSIS
    Deploy-WindowsService.ps1 - Production Windows Service, Service Account & Firewall Deployment Script
.DESCRIPTION
    Automates the installation, configuration, verification, lifecycle, NTFS permissions,
    and Windows Firewall rules for the Grozet Retail POS Server (GrozetPOSServer).
    Preserves Architecture v5.2.9.6, Sub-Phase 16.2 filesystem layout, and Sub-Phase 16.3 invariants:
    - Runs under virtual service account: NT SERVICE\GrozetPOSServer (least privilege)
    - Program Files: Read & Execute ONLY (immutable binaries)
    - ProgramData: Modify permissions across Database, Backups, Security, Logs, Staging
    - Inbound firewall rules: TCP 5100 (HTTP), TCP 5101 (HTTPS), UDP 5105 (Discovery)
    - Scoped strictly to Domain and Private network profiles (no public Internet exposure)
.PARAMETER Action
    Install, Uninstall, Verify, Start, Stop, Restart (default: Verify)
.PARAMETER ServiceName
    Canonical Windows Service name (default: GrozetPOSServer)
.PARAMETER DisplayName
    User-visible display name (default: Grozet Retail POS Server)
.PARAMETER Description
    Service description in SCM
.PARAMETER ServiceAccount
    Service identity account (default: NT SERVICE\GrozetPOSServer)
.PARAMETER BinaryPath
    Target executable path (default: C:\Program Files\GROZET\RetailPOS\Server\RetailPOS.Server.exe)
.PARAMETER DataRoot
    Target mutable data root (default: %ProgramData%\GROZET\RetailPOS)
.PARAMETER HttpPort
    HTTP port (default: 5100)
.PARAMETER HttpsPort
    HTTPS port (default: 5101)
.PARAMETER DiscoveryPort
    UDP discovery port (default: 5105)
#>

[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Verify', 'Start', 'Stop', 'Restart')]
    [string]$Action = 'Verify',

    [string]$ServiceName = 'GrozetPOSServer',
    [string]$DisplayName = 'Grozet Retail POS Server',
    [string]$Description = 'Authoritative local server for Grozet Retail POS billing, inventory, and counter terminal synchronization.',
    [string]$ServiceAccount = 'NT SERVICE\GrozetPOSServer',
    [string]$BinaryPath = 'C:\Program Files\GROZET\RetailPOS\Server\RetailPOS.Server.exe',
    [string]$DataRoot = "$env:ProgramData\GROZET\RetailPOS",
    [int]$HttpPort = 5100,
    [int]$HttpsPort = 5101,
    [int]$DiscoveryPort = 5105
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Administrator {
    if (-not (Test-IsAdministrator)) {
        throw "Administrative privileges required. Please re-run this script in an elevated PowerShell session."
    }
}

function Install-GrozetService {
    Assert-Administrator
    Write-Host "=== Installing $DisplayName ($ServiceName) ===" -ForegroundColor Cyan

    # 1. Ensure operational directories exist
    Write-Host "Creating operational directories under: $DataRoot" -ForegroundColor Yellow
    $dirs = @(
        "$DataRoot\Database",
        "$DataRoot\Backups",
        "$DataRoot\Security",
        "$DataRoot\Logs",
        "$DataRoot\Staging",
        "$DataRoot\Staging\Quarantine",
        "$DataRoot\Staging\Recovery"
    )

    foreach ($d in $dirs) {
        if (-not (Test-Path $d)) {
            New-Item -Path $d -ItemType Directory -Force | Out-Null
            Write-Host "  Created: $d"
        }
    }

    # 2. Register Windows Service with SCM
    Write-Host "Registering Windows Service: $ServiceName with account $ServiceAccount" -ForegroundColor Yellow
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($null -eq $existing) {
        $createArgs = @(
            "create", $ServiceName,
            "binPath=", "`"$BinaryPath`"",
            "start=", "auto",
            "obj=", $ServiceAccount,
            "DisplayName=", "`"$DisplayName`""
        )
        & sc.exe @createArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create service $ServiceName (ExitCode: $LASTEXITCODE)"
        }
        Write-Host "  Service created successfully."
    } else {
        Write-Host "  Service $ServiceName already exists. Updating configuration."
        $configArgs = @(
            "config", $ServiceName,
            "binPath=", "`"$BinaryPath`"",
            "start=", "auto",
            "obj=", $ServiceAccount,
            "DisplayName=", "`"$DisplayName`""
        )
        & sc.exe @configArgs
    }

    # Set service description
    & sc.exe description $ServiceName "$Description"

    # 3. Configure Failure Recovery Policy (reset=1 day, restart after 60s for 1st, 2nd, subsequent)
    Write-Host "Configuring failure recovery actions (restart after 60s)" -ForegroundColor Yellow
    $failArgs = @(
        "failure", $ServiceName,
        "reset=", "86400",
        "actions=", "restart/60000/restart/60000/restart/60000"
    )
    & sc.exe @failArgs

    # 4. Configure NTFS Permissions (Least Privilege)
    Write-Host "Applying least-privilege NTFS permissions for $ServiceAccount" -ForegroundColor Yellow

    # Program Files (Immutable Binaries): Read & Execute ONLY
    $binDir = Split-Path $BinaryPath
    if (Test-Path $binDir) {
        Write-Host "  Granting Read & Execute to $binDir"
        & icacls.exe "$binDir" /grant "${ServiceAccount}:(OI)(CI)RX" /Q
    }

    # ProgramData Root: Remove inherited permissions, grant SYSTEM & Administrators Full Control, grant Users RX (read-only, no write/create)
    Write-Host "  Configuring ProgramData root ACLs on: $DataRoot"
    & icacls.exe "$DataRoot" /inheritance:r
    & icacls.exe "$DataRoot" /grant "NT AUTHORITY\SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "BUILTIN\Users:(RX)" /Q

    # Operational State: Database, Backups, Security, Logs, Staging
    # Strip ALL inherited permissions (/inheritance:r) to completely isolate from ordinary Users.
    # Grant SYSTEM Full Control, Administrators Full Control, Service Account Modify.
    # NO permissions granted to Users / BUILTIN\Users (Access Denied by default).
    $operationalSubdirs = @("Database", "Backups", "Security", "Logs", "Staging")
    foreach ($sub in $operationalSubdirs) {
        $subPath = "$DataRoot\$sub"
        Write-Host "  Securing operational directory: $subPath (Inheritance stripped, restricted to Service & Admins)"
        & icacls.exe "$subPath" /inheritance:r
        & icacls.exe "$subPath" /grant "NT AUTHORITY\SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "${ServiceAccount}:(OI)(CI)M" /T /Q
    }

    # 5. Configure Windows Firewall Rules (TCP 5100, TCP 5101, UDP 5105; Domain & Private only)
    Write-Host "Configuring Windows Firewall rules for approved ports" -ForegroundColor Yellow

    # Rule 1: HTTP
    $ruleHttp = Get-NetFirewallRule -Name "Grozet-POS-Server-HTTP-In" -ErrorAction SilentlyContinue
    if ($null -ne $ruleHttp) {
        Remove-NetFirewallRule -Name "Grozet-POS-Server-HTTP-In"
    }
    New-NetFirewallRule `
        -Name "Grozet-POS-Server-HTTP-In" `
        -DisplayName "Grozet Retail POS Server (HTTP Inbound)" `
        -Description "Allows inbound HTTP traffic from local counter terminals to Grozet POS Server." `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $HttpPort `
        -Profile Domain, Private `
        -Program $BinaryPath | Out-Null
    Write-Host "  Created firewall rule: Grozet-POS-Server-HTTP-In (TCP $HttpPort)"

    # Rule 2: HTTPS
    $ruleHttps = Get-NetFirewallRule -Name "Grozet-POS-Server-HTTPS-In" -ErrorAction SilentlyContinue
    if ($null -ne $ruleHttps) {
        Remove-NetFirewallRule -Name "Grozet-POS-Server-HTTPS-In"
    }
    New-NetFirewallRule `
        -Name "Grozet-POS-Server-HTTPS-In" `
        -DisplayName "Grozet Retail POS Server (HTTPS Inbound)" `
        -Description "Allows inbound HTTPS traffic from local counter terminals to Grozet POS Server." `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $HttpsPort `
        -Profile Domain, Private `
        -Program $BinaryPath | Out-Null
    Write-Host "  Created firewall rule: Grozet-POS-Server-HTTPS-In (TCP $HttpsPort)"

    # Rule 3: UDP Discovery
    $ruleDisc = Get-NetFirewallRule -Name "Grozet-POS-Server-Discovery-In" -ErrorAction SilentlyContinue
    if ($null -ne $ruleDisc) {
        Remove-NetFirewallRule -Name "Grozet-POS-Server-Discovery-In"
    }
    New-NetFirewallRule `
        -Name "Grozet-POS-Server-Discovery-In" `
        -DisplayName "Grozet Retail POS Server (UDP Discovery Inbound)" `
        -Description "Allows inbound UDP broadcast discovery from local counter terminals to Grozet POS Server." `
        -Direction Inbound `
        -Action Allow `
        -Protocol UDP `
        -LocalPort $DiscoveryPort `
        -Profile Domain, Private `
        -Program $BinaryPath | Out-Null
    Write-Host "  Created firewall rule: Grozet-POS-Server-Discovery-In (UDP $DiscoveryPort)"

    Write-Host "=== Installation Complete ===" -ForegroundColor Green
}

function Uninstall-GrozetService {
    Assert-Administrator
    Write-Host "=== Uninstalling $DisplayName ($ServiceName) ===" -ForegroundColor Cyan

    # 1. Stop service if running
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $svc) {
        if ($svc.Status -eq 'Running') {
            Write-Host "Stopping service $ServiceName..."
            Stop-Service -Name $ServiceName -Force
        }
        Write-Host "Deleting service $ServiceName..."
        & sc.exe delete $ServiceName
    }

    # 2. Remove firewall rules
    Write-Host "Removing firewall rules..."
    Remove-NetFirewallRule -Name "Grozet-POS-Server-HTTP-In" -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -Name "Grozet-POS-Server-HTTPS-In" -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -Name "Grozet-POS-Server-Discovery-In" -ErrorAction SilentlyContinue

    Write-Host "Note: Operational data at $DataRoot was preserved." -ForegroundColor Yellow
    Write-Host "=== Uninstall Complete ===" -ForegroundColor Green
}

function Verify-GrozetService {
    Write-Host "=== Verifying $DisplayName Deployment ===" -ForegroundColor Cyan

    # 1. Check Service
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "[NOT INSTALLED] Service '$ServiceName' does not exist in SCM." -ForegroundColor Yellow
    } else {
        Write-Host "[FOUND] Service '$ServiceName'" -ForegroundColor Green
        Write-Host "  Status:     $($svc.Status)"
        Write-Host "  StartType:  $($svc.StartType)"

        # Check account
        try {
            $cim = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
            Write-Host "  Account:    $($cim.StartName)"
            if ($cim.StartName -eq $ServiceAccount) {
                Write-Host "  [OK] Service account matches expected: $ServiceAccount" -ForegroundColor Green
            } else {
                Write-Host "  [WARN] Service account is $($cim.StartName), expected $ServiceAccount" -ForegroundColor Yellow
            }
        } catch {
            Write-Host "  Account check failed: $_"
        }
    }

    # 2. Check Directories and NTFS Permissions
    Write-Host "`nChecking ProgramData Directories & NTFS Isolation:" -ForegroundColor Cyan
    $expectedDirs = @("Database", "Backups", "Security", "Logs", "Staging")
    foreach ($sub in $expectedDirs) {
        $p = "$DataRoot\$sub"
        if (Test-Path $p) {
            Write-Host "  [EXISTS] $p" -ForegroundColor Green
            try {
                $acl = Get-Acl -Path $p -ErrorAction SilentlyContinue
                if ($null -ne $acl) {
                    if ($acl.AreAccessRulesProtected) {
                        Write-Host "    [OK] Inheritance disabled (protected from inherited Users permissions)" -ForegroundColor Green
                    } else {
                        Write-Host "    [WARN] Inheritance ENABLED - may inherit unprivileged Users permissions" -ForegroundColor Yellow
                    }

                    $usersRules = @($acl.Access | Where-Object { $_.IdentityReference.Value -match 'Users' })
                    if ($usersRules.Count -eq 0) {
                        Write-Host "    [OK] BUILTIN\Users has NO access rules" -ForegroundColor Green
                    } else {
                        Write-Host "    [WARN] BUILTIN\Users has access rule: $($usersRules[0].FileSystemRights)" -ForegroundColor Yellow
                    }

                    $svcRules = @($acl.Access | Where-Object { $_.IdentityReference.Value -eq $ServiceAccount })
                    if ($svcRules.Count -gt 0) {
                        Write-Host "    [OK] Service account ($ServiceAccount) granted: $($svcRules[0].FileSystemRights)" -ForegroundColor Green
                    }
                }
            } catch {
                Write-Host "    [INFO] ACL inspection not available: $_"
            }
        } else {
            Write-Host "  [MISSING] $p" -ForegroundColor Yellow
        }
    }

    # 3. Check Firewall Rules
    Write-Host "`nChecking Windows Firewall Rules:" -ForegroundColor Cyan
    $rules = @("Grozet-POS-Server-HTTP-In", "Grozet-POS-Server-HTTPS-In", "Grozet-POS-Server-Discovery-In")
    foreach ($r in $rules) {
        $fw = Get-NetFirewallRule -Name $r -ErrorAction SilentlyContinue
        if ($null -ne $fw) {
            $portFilter = Get-NetFirewallPortFilter -Rule $fw
            Write-Host "  [OK] $r (Enabled: $($fw.Enabled), Port: $($portFilter.LocalPort), Protocol: $($portFilter.Protocol), Profile: $($fw.Profile))" -ForegroundColor Green
        } else {
            Write-Host "  [NOT FOUND] Firewall rule '$r'" -ForegroundColor Yellow
        }
    }
}

function Start-GrozetService {
    Assert-Administrator
    Write-Host "Starting $ServiceName..."
    Start-Service -Name $ServiceName
    Get-Service -Name $ServiceName
}

function Stop-GrozetService {
    Assert-Administrator
    Write-Host "Stopping $ServiceName..."
    Stop-Service -Name $ServiceName
    Get-Service -Name $ServiceName
}

function Restart-GrozetService {
    Assert-Administrator
    Write-Host "Restarting $ServiceName..."
    Restart-Service -Name $ServiceName
    Get-Service -Name $ServiceName
}

# Main Execution Switch
switch ($Action) {
    'Install'   { Install-GrozetService }
    'Uninstall' { Uninstall-GrozetService }
    'Verify'    { Verify-GrozetService }
    'Start'     { Start-GrozetService }
    'Stop'      { Stop-GrozetService }
    'Restart'   { Restart-GrozetService }
}
