# GROZET RetailPOS — Commercial Retail Billing & POS System

> [!NOTE]
> **Commercial Windows Retail POS System** | Target OS: Windows 10/11 (64-bit) | Runtime: .NET 10 | Authoritative Database: SQLite (WAL)

**GROZET RetailPOS** is a high-reliability, offline-first point-of-sale and retail billing system engineered specifically for commercial single-station and multi-counter retail environments on Windows 10 and 11.

---

## 1. Architectural Highlights

- **Offline-First Resilience**: Designed for 100% operational uptime without internet dependency. All sales, inventory updates, tax calculations, and customer accounting execute against an authoritative local server and database on the local area network (LAN).
- **Authoritative Local Server**: Hosted as a hardened Windows Service (`GrozetPOSServer`) running ASP.NET Core Minimal APIs under a dedicated virtual service account (`NT SERVICE\GrozetPOSServer`).
- **Authoritative SQLite Engine**: Uses a single-file SQLite database (`grozet_pos.db`) configured with Write-Ahead Logging (WAL mode), synchronous=NORMAL, 5000ms busy timeout, and strict foreign keys. Remote counters communicate exclusively via HTTP/HTTPS APIs and never access database files directly.
- **Deterministic Currency & Sequential Invoicing**: Financial calculations use 64-bit integer paise arithmetic to eliminate floating-point rounding errors (ADR-006). Invoicing enforces gap-free monotonic sequence numbers.
- **Zero-Knowledge Counter Pairing**: LAN counter terminals pair with the server using the SRP-6a protocol with an 80-bit CSPRNG `HumanPairingCode` (16 Crockford Base32 characters formatted as `XXXX-XXXX-XXXX-XXXX`) and an independently computed 40-bit HMAC Short Authentication String (SAS, 8 Crockford Base32 characters formatted as `XXXX-XXXX`).
- **Robust Backup & Bare-Metal Recovery**: Tiered backup architecture:
  - **A**: Local primary SQLite snapshot + HMAC companion manifest.
  - **B1**: Trusted internal secondary storage mirror + HMAC companion manifest.
  - **B2**: Removable USB encrypted `.gdrexport` container at rest.
  - **C**: Portable off-site encrypted `GDR1` container (AES-256-GCM + HKDF key derivation).
  - Clean-Machine Reconstruction: Windows DPAPI identities are reconstructed safely on replacement hardware using the physical Master Recovery Key without copying old DPAPI files.
- **Commercial Release Engineering**: First-party executables and installers are signed with Microsoft Authenticode and RFC 3161 SHA-256 timestamped. Release artifacts are verified against a cryptographically signed CMS detached manifest (`release_manifest.json.p7s`).

---

## 2. High-Level Architecture

```mermaid
graph TD
    subgraph LAN["Store Local Area Network (Isolated Wired Ethernet)"]
        subgraph ServerPC["Server PC (Back Office / Main Station)"]
            SCM["Windows Service Control Manager"] -->|Runs as NT SERVICE| SVC["GrozetPOSServer Windows Service (TCP 5100/5101, UDP 5105)"]
            SVC -->|In-Process Engine| BIZ["Sales, Inventory, Auth & Tax Engines"]
            BIZ -->|Direct Connection WAL Mode| DB[("Authoritative SQLite Database (grozet_pos.db)")]
            BIZ -->|DPAPI LocalMachine| SEC["Store Identity (store_identity.dat)"]
            SVC -->|Automated Scheduled Jobs| BAK["Backup Engine (A / B1 / B2 / C GDR1)"]
        end

        subgraph Counter1["Counter Terminal 1"]
            UI1["RetailPOS Client (Desktop UI)"] -->|HTTP / JSON (TCP 5100)| SVC
            UI1 -->|USB HID| SCAN1["Barcode Scanner"]
            UI1 -->|ESC/POS RAW| PRN1["Thermal Printer"]
            PRN1 -->|RJ-11 Pulse| DRW1["Cash Drawer"]
        end

        subgraph Counter2["Counter Terminal 2"]
            UI2["RetailPOS Client (Desktop UI)"] -->|HTTP / JSON (TCP 5100)| SVC
            UI2 -->|USB HID| SCAN2["Barcode Scanner"]
            UI2 -->|ESC/POS RAW| PRN2["Thermal Printer"]
            PRN2 -->|RJ-11 Pulse| DRW2["Cash Drawer"]
        end
    end
```

---

## 3. Repository Structure

```text
Billing software/
├── Directory.Build.props      # Centrally pinned .NET 10 version and assembly metadata
├── RetailPOS.sln              # Master solution file
├── src/                       # Core application source code
│   ├── RetailPOS.Domain/      # Domain models, entities, enums, 32-permission matrix
│   ├── RetailPOS.Contracts/   # Shared DTOs, paths, deployment & installer specifications
│   ├── RetailPOS.Application/ # Business logic, sales, inventory, pairing, authentication
│   ├── RetailPOS.Infrastructure/ # SQLite EF Core, DPAPI identity, hardware drivers, backups
│   ├── RetailPOS.Server/      # ASP.NET Core Windows Service host & Minimal API endpoints
│   └── RetailPOS.Client/      # Counter desktop client UI application
├── tests/                     # Deterministic automated test suites (747 tests passing)
│   ├── RetailPOS.Domain.Tests/
│   ├── RetailPOS.Application.Tests/
│   ├── RetailPOS.Infrastructure.Tests/
│   ├── RetailPOS.Server.Tests/
│   ├── RetailPOS.Client.Tests/
│   └── RetailPOS.E2E.Tests/
├── packaging/                 # Inno Setup 6 packaging scripts & specifications
├── scripts/                   # Deployment and release automation scripts
│   ├── deployment/            # Build-Installer.ps1, Deploy-WindowsService.ps1
│   └── release/               # New-ReleaseBuild.ps1, Test-ReleaseArtifacts.ps1, New-TestCertificate.ps1
└── docs/                      # Authoritative commercial documentation suite
    ├── DOCUMENTATION_INDEX.md # Master index, role portals, source-of-truth mapping
    ├── architecture/          # System architecture and offline LAN models
    ├── deployment/            # Installation, service deployment, and release engineering
    ├── operations/            # Store operations manual, role quick cards, hardware, DR, support
    ├── security/              # Security governance, RBAC matrix, and network boundaries
    ├── database/              # SQLite administration and schema migration engineering
    └── decisions/             # Architectural Decision Records (ADR-001 through ADR-010)
```

---

## 4. Quick Start for Developers & Maintainers

### Prerequisites
- Windows 10 (Version 1809+, 64-bit) or Windows 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Version 10.0.401 pinned in `global.json`)
- Inno Setup 6 (optional, required only for compiling `.exe` installer packages)

### Building the Solution
```powershell
# Restore dependencies and build entire solution in Release configuration
dotnet build RetailPOS.sln -c Release
```

### Running Automated Test Suite
```powershell
# Execute all 747 deterministic unit, integration, and security tests
dotnet test RetailPOS.sln -c Release
```

### Running Server Locally in Interactive Mode
```powershell
# Starts the server in interactive console mode on http://127.0.0.1:5100
dotnet run --project src/RetailPOS.Server/RetailPOS.Server.csproj
```

---

## 5. Master Documentation Sitemap

For complete technical and operational documentation, see **[`docs/DOCUMENTATION_INDEX.md`](file:///docs/DOCUMENTATION_INDEX.md)**.

| Domain | Key Documentation Files |
|---|---|
| **Master Hub** | • [`docs/DOCUMENTATION_INDEX.md`](file:///docs/DOCUMENTATION_INDEX.md) — Master Index & Source of Truth |
| **Architecture** | • [`docs/architecture/SYSTEM_ARCHITECTURE_AND_OFFLINE_MODEL.md`](file:///docs/architecture/SYSTEM_ARCHITECTURE_AND_OFFLINE_MODEL.md)<br>• [`docs/decisions/`](file:///docs/decisions/) — ADR-001 through ADR-010 |
| **Installation & Setup** | • [`docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md`](file:///docs/deployment/INSTALLATION_AND_NETWORK_SETUP_GUIDE.md)<br>• [`docs/deployment/WINDOWS_SERVICE_DEPLOYMENT.md`](file:///docs/deployment/WINDOWS_SERVICE_DEPLOYMENT.md)<br>• [`docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md`](file:///docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md) |
| **Store Operations** | • [`docs/operations/STORE_OPERATIONS_MANUAL.md`](file:///docs/operations/STORE_OPERATIONS_MANUAL.md)<br>• [`docs/operations/ROLE_QUICK_REFERENCE_CARDS.md`](file:///docs/operations/ROLE_QUICK_REFERENCE_CARDS.md) |
| **Hardware** | • [`docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md`](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md) |
| **Security & Access** | • [`docs/security/SECURITY_AND_ACCESS_CONTROL_GUIDE.md`](file:///docs/security/SECURITY_AND_ACCESS_CONTROL_GUIDE.md)<br>• [`docs/security/SECURITY_BOUNDARIES.md`](file:///docs/security/SECURITY_BOUNDARIES.md) |
| **Disaster Recovery** | • [`docs/operations/DISASTER_RECOVERY_AND_BACKUP_OPERATIONS.md`](file:///docs/operations/DISASTER_RECOVERY_AND_BACKUP_OPERATIONS.md)<br>• [`docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md`](file:///docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md) |
| **Database & Schema** | • [`docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md`](file:///docs/database/DATABASE_ADMINISTRATION_AND_SCHEMA_MIGRATIONS.md) |
| **Updates & Releases** | • [`docs/deployment/SOFTWARE_UPDATES_AND_RELEASE_VERIFICATION.md`](file:///docs/deployment/SOFTWARE_UPDATES_AND_RELEASE_VERIFICATION.md)<br>• [`docs/deployment/RELEASE_ENGINEERING_GUIDE.md`](file:///docs/deployment/RELEASE_ENGINEERING_GUIDE.md) |
| **Support & Diagnostics** | • [`docs/operations/SUPPORT_DIAGNOSTICS_AND_TROUBLESHOOTING_MANUAL.md`](file:///docs/operations/SUPPORT_DIAGNOSTICS_AND_TROUBLESHOOTING_MANUAL.md) |
