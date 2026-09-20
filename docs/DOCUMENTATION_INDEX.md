# GROZET RetailPOS — Master Documentation Index & Source of Truth

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

Welcome to the authoritative documentation suite for **GROZET RetailPOS** (Offline-First Commercial Retail Billing & POS System). This index defines role-based documentation pathways, cross-version alignment, and the official source of truth across all system operational areas.

---

## 1. Role-Based Navigation Portals

Select your operational role to access the relevant guides, runbooks, and reference cards:

| Operational Role | Primary Manuals & Reference Guides |
|---|---|
| **Store Owner / Business Administrator** | • [Store User Operations Manual](file:///docs/operations/STORE_OPERATIONS_MANUAL.md)<br>• [Role Quick Reference Cards (Owner Card)](file:///docs/operations/ROLE_QUICK_REFERENCE_CARDS.md#card-3-store-owner-quick-reference-card)<br>• [Security & Access Control Guide](file:///docs/security/SECURITY_AND_ACCESS_CONTROL_GUIDE.md)<br>• [First-Run & Store Initialization](file:///docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md) |
| **POS Cashier** | • [Store User Operations Manual](file:///docs/operations/STORE_OPERATIONS_MANUAL.md)<br>• [Role Quick Reference Cards (Cashier Card)](file:///docs/operations/ROLE_QUICK_REFERENCE_CARDS.md#card-1-pos-cashier-quick-reference-card) |
| **Store Manager** | • [Store User Operations Manual](file:///docs/operations/STORE_OPERATIONS_MANUAL.md)<br>• [Role Quick Reference Cards (Manager Card)](file:///docs/operations/ROLE_QUICK_REFERENCE_CARDS.md#card-2-store-manager-quick-reference-card)<br>• [Hardware Integration & Troubleshooting](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md) |
| **Installation Technician** | • [Installation & Network Setup Guide](file:///docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md)<br>• [Windows Service Deployment Specification](file:///docs/deployment/WINDOWS_SERVICE_DEPLOYMENT.md)<br>• [Hardware Integration & Troubleshooting](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md) |
| **Network / Setup Technician** | • [Installation & Network Setup Guide](file:///docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md)<br>• [Security Boundaries & Network Model](file:///docs/security/SECURITY_BOUNDARIES.md)<br>• [Hardware Integration & Troubleshooting](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md) |
| **Support Technician** | • [Support Diagnostics & Troubleshooting Manual](file:///docs/operations/SUPPORT_DIAGNOSTICS_AND_TROUBLESHOOTING_MANUAL.md)<br>• [Hardware Integration & Troubleshooting](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md)<br>• [Emergency Operational Runbooks](file:///docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md) |
| **Disaster Recovery (DR) Technician** | • [Disaster Recovery & Backup Operations](file:///docs/operations/DISASTER_RECOVERY_AND_BACKUP_OPERATIONS.md)<br>• [Emergency Operational Runbooks](file:///docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md)<br>• [Database Administration & Schema Migrations](file:///docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md) |
| **Release & Deployment Operator** | • [Software Updates & Release Verification](file:///docs/deployment/SOFTWARE_UPDATES_AND_RELEASE_VERIFICATION.md)<br>• [Release Engineering & Code Signing Guide](file:///docs/deployment/RELEASE_ENGINEERING_GUIDE.md)<br>• [Installation & Network Setup Guide](file:///docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md) |
| **Developer / System Maintainer** | • [System Architecture & Offline Model](file:///docs/architecture/SYSTEM_ARCHITECTURE_AND_OFFLINE_MODEL.md)<br>• [Database Administration & Schema Migrations](file:///docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md)<br>• [Architectural Decision Records (ADRs)](file:///docs/decisions/) |

---

## 2. Complete Document Catalog

### Tier 1: System Architecture & Design
- [`docs/architecture/SYSTEM_ARCHITECTURE_AND_OFFLINE_MODEL.md`](file:///docs/architecture/SYSTEM_ARCHITECTURE_AND_OFFLINE_MODEL.md): Authoritative architectural principles, offline-first local server model, SQLite concurrency tuning, integer arithmetic, and network partition behavior.
- [`docs/decisions/`](file:///docs/decisions/): Architectural Decision Records (ADR-001 through ADR-010) documenting frozen historical architectural choices.

### Tier 2: Installation, Network & Deployment
- [`docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md`](file:///docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md): Field installation guide, prerequisites, installer variants, Windows Service account (`NT SERVICE\GrozetPOSServer`), directory ACL hardening, and firewall configuration.
- [`docs/deployment/WINDOWS_SERVICE_DEPLOYMENT.md`](file:///docs/deployment/WINDOWS_SERVICE_DEPLOYMENT.md): Low-level Windows Service hosting specification, SCM crash recovery policies, and NTFS permissions matrix.
- [`docs/deployment/SOFTWARE_UPDATES_AND_RELEASE_VERIFICATION.md`](file:///docs/deployment/SOFTWARE_UPDATES_AND_RELEASE_VERIFICATION.md): Upgrading deployments, detached CMS manifest verification (`release_manifest.json.p7s`), SHA-256 checks, SmartScreen reputation realities, and rollback prevention.
- [`docs/deployment/RELEASE_ENGINEERING_GUIDE.md`](file:///docs/deployment/RELEASE_ENGINEERING_GUIDE.md): Authoritative code signing, RFC 3161 timestamping, hardware HSM requirements, and release verification engine.

### Tier 3: Store Operations & Daily Procedures
- [`docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md`](file:///docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md): Clean-installation store provisioning, primary Owner setup, Master Recovery Key handling protocol, and counter SRP-6a pairing.
- [`docs/operations/STORE_OPERATIONS_MANUAL.md`](file:///docs/operations/STORE_OPERATIONS_MANUAL.md): Complete end-user guide for daily billing, cash sessions, barcode scanning, cart operations, discounts, GST calculation, payments, and returns.
- [`docs/operations/ROLE_QUICK_REFERENCE_CARDS.md`](file:///docs/operations/ROLE_QUICK_REFERENCE_CARDS.md): Condensed 1-page printable reference sheets for Cashier, Store Manager, and Store Owner.

### Tier 4: Hardware Integration
- [`docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md`](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md): Driver, wiring, and interface specifications for USB barcode scanners (Keyboard Wedge), thermal printers (ESC/POS RAW & TCP 9100), RJ-11 cash drawers, and RS-232 weighing scales.

### Tier 5: Security Governance & Access Control
- [`docs/security/SECURITY_AND_ACCESS_CONTROL_GUIDE.md`](file:///docs/security/SECURITY_AND_ACCESS_CONTROL_GUIDE.md): The 5 roles (`Owner`, `Admin`, `Manager`, `Cashier`, `Viewer`), 32 canonical permissions, brute-force lockout, supervisor authorization core, audit logging, and DPAPI store identity.
- [`docs/security/SECURITY_BOUNDARIES.md`](file:///docs/security/SECURITY_BOUNDARIES.md): Physical network topology, perimeter boundaries, and zero-trust LAN defense-in-depth model.

### Tier 6: Database Administration & Schema Migrations
- [`docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md`](file:///docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md): SQLite WAL mode, synchronous=NORMAL, 5000ms busy timeout, Schema Version 15 migrations, quiescence state machine, and loopback rollback engine.

### Tier 7: Disaster Recovery, Support & Emergency Procedures
- [`docs/operations/DISASTER_RECOVERY_AND_BACKUP_OPERATIONS.md`](file:///docs/operations/DISASTER_RECOVERY_AND_BACKUP_OPERATIONS.md): The A / B1 / B2 / C backup hierarchy, 10GB retention ceiling, 7-state DR state machine, clean Windows-machine DPAPI reconstruction runbook, and technician rules.
- [`docs/operations/SUPPORT_DIAGNOSTICS_AND_TROUBLESHOOTING_MANUAL.md`](file:///docs/operations/SUPPORT_DIAGNOSTICS_AND_TROUBLESHOOTING_MANUAL.md): 4-tier diagnostics collection, sensitive data redactor and auditor rules, log analysis, and root-cause solutions for all 17 field operational issues.
- [`docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md`](file:///docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md): High-severity catastrophic failure runbooks: server hardware loss, drive failure, OS reinstall, lost keys, compromised keys, counter network partitions, and interrupted migrations.

---

## 3. Source of Truth Matrix

| Operational Area / Domain | Authoritative Source Code in Repository | Authoritative Document |
|---|---|---|
| **System Topology & LAN Model** | `src/RetailPOS.Server/`, `ADR-001..010` | [`docs/architecture/SYSTEM_ARCHITECTURE_AND_OFFLINE_MODEL.md`](file:///docs/architecture/SYSTEM_ARCHITECTURE_AND_OFFLINE_MODEL.md) |
| **Windows Service Hosting & SCM** | `WindowsServiceDeploymentSpecification.cs` | [`docs/deployment/WINDOWS_SERVICE_DEPLOYMENT.md`](file:///docs/deployment/WINDOWS_SERVICE_DEPLOYMENT.md) |
| **Installation & Network Setup** | `InnoSetupInstallerSpecification.cs`, `Deploy-WindowsService.ps1` | [`docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md`](file:///docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md) |
| **First-Run Setup & Pairing** | `StoreIdentityService.cs`, `Srp6aPairingService.cs` | [`docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md`](file:///docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md) |
| **Store Operations & Billing** | `src/RetailPOS.Application/Services/`, `SaleInvoice.cs` | [`docs/operations/STORE_OPERATIONS_MANUAL.md`](file:///docs/operations/STORE_OPERATIONS_MANUAL.md) |
| **Role Quick References** | `RolePermissions.cs`, `SecuritySettings.cs` | [`docs/operations/ROLE_QUICK_REFERENCE_CARDS.md`](file:///docs/operations/ROLE_QUICK_REFERENCE_CARDS.md) |
| **Hardware Devices & Drivers** | `src/RetailPOS.Infrastructure/Hardware/` | [`docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md`](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md) |
| **Security, RBAC & Audit** | `RolePermissions.cs`, `SupervisorAuthorizationCore.cs` | [`docs/security/SECURITY_AND_ACCESS_CONTROL_GUIDE.md`](file:///docs/security/SECURITY_AND_ACCESS_CONTROL_GUIDE.md) |
| **Backup, Restore & DR** | `BackupService.cs`, `FilesystemReconciliationEngine.cs` | [`docs/operations/DISASTER_RECOVERY_AND_BACKUP_OPERATIONS.md`](file:///docs/operations/DISASTER_RECOVERY_AND_BACKUP_OPERATIONS.md) |
| **Database & Schema Migrations** | `SafeMigrationRunner.cs`, `SchemaMigrationRegistry.cs` | [`docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md`](file:///docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md) |
| **Software Updates & Releases** | `RELEASE_ENGINEERING_GUIDE.md`, `New-ReleaseBuild.ps1` | [`docs/deployment/SOFTWARE_UPDATES_AND_RELEASE_VERIFICATION.md`](file:///docs/deployment/SOFTWARE_UPDATES_AND_RELEASE_VERIFICATION.md) |
| **Support Diagnostics & Redaction** | `SupportDiagnosticsService.cs`, `DiagnosticRedactor.cs` | [`docs/operations/SUPPORT_DIAGNOSTICS_AND_TROUBLESHOOTING_MANUAL.md`](file:///docs/operations/SUPPORT_DIAGNOSTICS_AND_TROUBLESHOOTING_MANUAL.md) |
| **Emergency Incident Procedures** | `FilesystemReconciliationEngine.cs`, `StoreIdentityService.cs` | [`docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md`](file:///docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md) |

---

## 4. System Version Alignment Matrix

All documentation across this repository corresponds to the following frozen architectural baseline:

| Version Dimension | Authoritative Constant | Source File in Codebase |
|---|---|---|
| **Product / Application Version** | `1.0.0` | [`Directory.Build.props`](file:///Directory.Build.props), [`InnoSetupInstallerSpecification.cs`](file:///src/RetailPOS.Contracts/Common/InnoSetupInstallerSpecification.cs) |
| **Assembly & File Version** | `1.0.0.0` | [`Directory.Build.props`](file:///Directory.Build.props), [`InnoSetupInstallerSpecification.cs`](file:///src/RetailPOS.Contracts/Common/InnoSetupInstallerSpecification.cs) |
| **Client/Server Protocol Version** | `1` | [`ProtocolVersionConstants.cs`](file:///src/RetailPOS.Contracts/Common/ProtocolVersionConstants.cs) (`X-Client-Protocol-Version: 1`) |
| **Database Schema Version** | `15` | [`ProtocolVersionConstants.cs`](file:///src/RetailPOS.Contracts/Common/ProtocolVersionConstants.cs), [`SchemaMigrationRegistry.cs`](file:///src/RetailPOS.Infrastructure/Deployment/SchemaMigrationRegistry.cs) |
| **Baseline Schema Version** | `14` | [`ProtocolVersionConstants.cs`](file:///src/RetailPOS.Contracts/Common/ProtocolVersionConstants.cs) |
| **Release Tag** | `v1.0.0` | [`New-ReleaseBuild.ps1`](file:///scripts/release/New-ReleaseBuild.ps1) |
| **Target Platform** | Windows 10 (1809+) & Windows 11 (64-bit) | Operating System Baseline |
| **Target Framework** | .NET 10 (SDK 10.0.401, self-contained `win-x64`) | [`global.json`](file:///global.json), [`Directory.Build.props`](file:///Directory.Build.props) |

---

## 5. Commercial Handover Checklist

For technicians certifying a new production store installation, all items must be verified before handing over to store staff:

- [ ] **Hardware Installation**: Server PC and counter terminals connected to an isolated physical Ethernet switch.
- [ ] **Service Verification**: `GrozetPOSServer` Windows Service running under `NT SERVICE\GrozetPOSServer` with automatic startup and recovery actions configured.
- [ ] **Port Listeners**: HTTP API (`5100`), HTTPS API (`5101`), and UDP Discovery (`5105`) listening.
- [ ] **Firewall Profile**: Domain and Private profiles enabled; inbound rules active; Public profile blocked.
- [ ] **Filesystem Hardening**: NTFS permissions verified on `%ProgramFiles%\GROZET\RetailPOS\` (Read & Execute) and `%ProgramData%\GROZET\RetailPOS\` (Inheritance stripped, Service Modify, Users denied).
- [ ] **First-Run Initialization**: Store initialized; primary Owner account provisioned.
- [ ] **Master Recovery Key**: 32-byte recovery key printed, placed in sealed envelope, and secured in store fireproof safe.
- [ ] **Counter Pairing**: Counter terminals paired via 16-character `HumanPairingCode` and 8-character SAS verification, and approved by Owner/Manager.
- [ ] **Hardware Verification**: Thermal receipt printer tested (RAW Spooler/TCP), cash drawer opens on pulse, barcode scanner reads in Keyboard Wedge mode.
- [ ] **Backup Verification**: Baseline B1 internal backup and B2 USB backup successfully taken and cryptographically verified.
- [ ] **Staff Handover**: Cashier and Manager trained using the [Role Quick Reference Cards](file:///docs/operations/ROLE_QUICK_REFERENCE_CARDS.md).
