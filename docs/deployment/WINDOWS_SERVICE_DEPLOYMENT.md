# Windows Service Hosting, Service Account & Firewall Deployment

## 1. Hosting Architecture

`RetailPOS.Server` is engineered as a standalone, enterprise Windows Service built on ASP.NET Core and `Microsoft.Extensions.Hosting.WindowsServices` (`builder.Host.UseWindowsService()`).
The canonical service identity is **`GrozetPOSServer`** (Display Name: **`Grozet Retail POS Server`**).

The host runs in dual-mode:
1. **Interactive Console Mode**: During development, testing, and troubleshooting (`dotnet run`).
2. **Headless Windows Service Mode**: In production store deployments managed by Windows Service Control Manager (SCM) (`sc.exe`).

## 2. Windows Service Identity (Least Privilege)

In production, `GrozetPOSServer` runs under a dedicated virtual service account:
```
NT SERVICE\GrozetPOSServer
```
Virtual service accounts provide:
* **Zero credential maintenance**: Automatically created and managed by Windows SCM; no embedded passwords.
* **Least privilege**: Runs without local Administrator privileges and is isolated from user sessions.
* **Granular auditing**: Logged distinctly in Windows Security logs.

## 3. Directory Layout & NTFS Permissions Matrix

The production filesystem strictly enforces separation between immutable application binaries (`Program Files`) and mutable operational state (`ProgramData`):

### Application Binaries (Immutable)
* **Path**: `C:\Program Files\GROZET\RetailPOS\Server\`
* **Permissions**:
  * `Administrators`: Full Control `(OI)(CI)F`
  * `SYSTEM`: Full Control `(OI)(CI)F`
  * `Users`: Read & Execute `(OI)(CI)RX`
  * `NT SERVICE\GrozetPOSServer`: Read & Execute `(OI)(CI)RX` (strictly **NO** write access)

### Operational State (Mutable)
* **Root**: `%ProgramData%\GROZET\RetailPOS\`
* **Inheritance**: Stripped of generic `Users` write/modify permissions; `SYSTEM` and `Administrators` retain Full Control.
* **Directory Permissions for `NT SERVICE\GrozetPOSServer`**:
  * `Database\`: Modify `(OI)(CI)M` (authoritative `grozet_pos.db` SQLite database with WAL and shared cache)
  * `Backups\`: Modify `(OI)(CI)M` (online VACUUM backup snapshots and HMAC manifests)
  * `Security\`: Modify `(OI)(CI)M` (`server_identity.key`, `store_identity.dat`, `backup_settings.json`, `security_settings.json`; generic Users access completely removed)
  * `Logs\`: Modify `(OI)(CI)M` (rolling Serilog log files `retailpos-server-yyyyMMdd.log`)
  * `Staging\`: Modify `(OI)(CI)M` (`Quarantine\` and `Recovery\` staging folders)

## 4. Service Lifecycle & SCM Failure Recovery

### Startup
* Directory structure validation and creation via `serverOptions.EnsureDirectoriesCreated()`.
* SQLite authoritative database initialization, migration, and pragma configuration.
* Crash reconciliation execution via `IFilesystemReconciliationEngine.ReconcileStartupAsync()`.
* Store identity and security settings initialization.
* UDP discovery listener binding on port `5105` (skipped in `Testing` environment).

### Graceful Shutdown
* SCM stop signal triggers cancellation of background hosted services (`ScheduledBackupBackgroundService`, `UdpServerDiscoveryListener`).
* Active HTTP connections complete within request timeouts.
* SQLite connections and maintenance gates are deterministically released.
* `Log.CloseAndFlush()` flushes all Serilog buffers.

### SCM Failure Recovery Policy
Configured via `sc.exe failure`:
* **Reset Period**: 86,400 seconds (24 hours)
* **Action Sequence**: Restart service after 60,000 ms (60 seconds) on 1st, 2nd, and subsequent unexpected failures.
```cmd
sc.exe failure GrozetPOSServer reset= 86400 actions= restart/60000/restart/60000/restart/60000
```

## 5. Windows Firewall Deployment

The POS server operates as a private store LAN server. Inbound firewall rules are scoped to **Domain** and **Private** network profiles only (public profiles are blocked):

| Rule Name | Protocol | Port | Direction | Action | Scope / Profile |
|:---|:---:|:---:|:---:|:---:|:---:|
| `Grozet-POS-Server-HTTP-In` | TCP | 5100 | Inbound | Allow | Domain, Private |
| `Grozet-POS-Server-HTTPS-In` | TCP | 5101 | Inbound | Allow | Domain, Private |
| `Grozet-POS-Server-Discovery-In` | UDP | 5105 | Inbound | Allow | Domain, Private |

## 6. Automation & Deployment Tooling

A production deployment script is provided at [`scripts/deployment/Deploy-WindowsService.ps1`](file:///c:/Users/moham/Documents/grozet%20software%20solutions/Billing%20software/scripts/deployment/Deploy-WindowsService.ps1):

### Installation
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deployment\Deploy-WindowsService.ps1 -Action Install
```

### Verification
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deployment\Deploy-WindowsService.ps1 -Action Verify
```

### Service Control
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deployment\Deploy-WindowsService.ps1 -Action Start
powershell -ExecutionPolicy Bypass -File scripts\deployment\Deploy-WindowsService.ps1 -Action Stop
powershell -ExecutionPolicy Bypass -File scripts\deployment\Deploy-WindowsService.ps1 -Action Restart
```

### Uninstallation
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deployment\Deploy-WindowsService.ps1 -Action Uninstall
```
*(Preserves all database and operational files in `%ProgramData%\GROZET\RetailPOS`.)*
