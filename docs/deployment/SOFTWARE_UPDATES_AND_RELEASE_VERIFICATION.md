# Software Updates & Release Verification Guide

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

This guide specifies software release distribution standards, cryptographic verification procedures, in-store upgrade workflows, and Windows Defender SmartScreen trust expectations for **GROZET RetailPOS**.

---

## 1. Release Engineering & Versioning Baseline

RetailPOS coordinates releases across four distinct, synchronized version dimensions:

| Version Dimension | Current Baseline | Description |
|---|---|---|
| **Product Semantic Version** | `1.0.0` | User-visible commercial application version. |
| **Assembly & File Version** | `1.0.0.0` | Windows PE executable file metadata embedded in binary headers. |
| **Client-Server Protocol Version** | `1` | Protocol handshake integer transmitted in `X-Client-Protocol-Version` HTTP headers. |
| **Authoritative Database Schema** | `15` | Relational SQLite schema version registered in `SchemaMigrationRegistry.cs`. |

All production releases originate from an immutable, clean Git release tag matching `refs/tags/v<Version>` pointing directly to `HEAD`.

---

## 2. Release Artifact Verification Pipeline

Before deploying any installation package on a customer server or counter terminal, the deployment operator or automated CI pipeline must verify the package using the independent release verifier:
`scripts\release\Test-ReleaseArtifacts.ps1`

### Release Package Contents (`artifacts/releases/v1.0.0/`)
- `GrozetRetailPOS_Setup.exe` (Unified Single-Station Installer)
- `GrozetRetailPOS_Server_Setup.exe` (Dedicated Server Installer)
- `GrozetRetailPOS_Client_Setup.exe` (Dedicated Counter Terminal Installer)
- `release_manifest.json` (Machine-readable release inventory & metadata)
- `release_manifest.json.p7s` (Detached Cryptographic Message Syntax CMS / PKCS#7 signature)
- `SHA256SUMS` (Standard SHA-256 hash list for rapid integrity checking)

### The 4-Step Verification Sequence

```mermaid
flowchart TD
    A["Release Package (artifacts/releases/v1.0.0)"] --> B["Step 1: Authenticate Manifest CMS Signature"]
    B -->|SignedCms.CheckSignature(true)| C{"Valid Grozet Signature on release_manifest.json.p7s?"}
    C -->|No| D["ABORT: Manifest untrusted. Reject entire release package."]
    C -->|Yes| E["Step 2: Audit Manifest Metadata"]
    E -->|Check AppVersion, Protocol, Schema, Git Tag, Commit| F{"Metadata matches expected release?"}
    F -->|No| G["ABORT: Version mismatch."]
    F -->|Yes| H["Step 3: Audit Artifact Hashes"]
    H -->|Recompute SHA-256 on all files| I{"All artifact hashes match authenticated manifest?"}
    I -->|No| J["ABORT: Artifact hash mismatch or unexpected file detected."]
    I -->|Yes| K["Step 4: Verify PE Authenticode Policy"]
    K -->|Check Grozet binaries & Microsoft runtime signatures| L{"All binaries comply with 10-category policy?"}
    L -->|No| M["ABORT: PE signature policy violation."]
    L -->|Yes| N["RELEASE VERIFICATION PASSED"]
```

### Running the Independent Release Verifier
```powershell
# Execute the independent release verifier in production mode
.\scripts\release\Test-ReleaseArtifacts.ps1 `
    -ReleaseDirectory "artifacts\releases\v1.0.0" `
    -Production `
    -ExpectedVersion "1.0.0" `
    -ExpectedGitTag "v1.0.0"
```

### Cryptographic Verification Guarantees
1. **CMS Detached Manifest Signature**:
   `release_manifest.json.p7s` cryptographically signs the manifest byte content using the official Grozet Code Signing key. The verifier checks the signature **before** trusting any artifact checksums.
2. **RFC 3161 Timestamp Verification**:
   Validate the timestamp and signing-time evidence so signatures can remain verifiable after the signing certificate expires, subject to certificate-chain trust, revocation/status validation, algorithm validity, and platform verification policy.
3. **10-Category PE Policy Enforcement**:
   - First-party Grozet executables and DLLs: Valid Authenticode signature signed by `CN=Grozet Software Solutions Private Limited`.
   - Microsoft .NET runtime binaries (`coreclr.dll`, `createdump.exe`, `System.*.dll`): Audited to verify original Microsoft digital signatures (`CN=.NET, O=Microsoft Corporation`). **Never re-signed**, preserving Microsoft digital trust chains.
   - Third-party managed & native libraries (`e_sqlite3.dll`, `Serilog.dll`): Audited as intentionally unsigned; integrity enforced via the authenticated manifest SHA-256 hash.

---

## 3. Modern Windows Defender SmartScreen Expectations

Field technicians and customer administrators must understand how Windows Defender SmartScreen operates on Windows 10 and 11:

### Authenticode & Trust Representation
Authenticode signatures provide executable integrity and identify the signing publisher when Windows successfully validates the signature and trust chain. UAC/SmartScreen presentation is determined by Windows security policy and trust/reputation signals and must not be described as guaranteed.

### SmartScreen Reputation Realities
- **No Automatic Bypass**: Extended Validation (EV) certificates no longer grant automatic, instant SmartScreen bypass for brand-new binary releases.
- **Dynamic Reputation Building**: Microsoft Defender SmartScreen evaluates reputation dynamically across both publisher certificates and unique binary file hashes based on telemetry from global installations.
- **Brand New Hashes**: Initial downloaders of a new release version may see:
  *"Windows protected your PC - Microsoft Defender SmartScreen prevented an unrecognized app from starting."*

### Customer Installation Runbook
When deploying a newly released version in a retail store:
1. When Windows Defender SmartScreen displays the unrecognized app dialog:
2. Instruct the store technician or customer to click **"More info"**.
3. Verify that the Publisher is explicitly listed as:
   **"Grozet Software Solutions Private Limited"** (or authorized Grozet entity).
4. Click **"Run anyway"** to complete installation.

---

## 4. In-Store Software Upgrade Procedure

Upgrading a live store installation preserves all sales transactions, stock history, customer accounts, and configuration settings:

### Step 1: Pre-Upgrade Verification
1. Ensure all cashier staff have closed their active cash sessions.
2. Close `RetailPOS.Client.exe` on all counter terminals.
3. On the Server PC, trigger a verified pre-upgrade backup:
   ```powershell
   # Trigger backup via REST API or Management Console
   Invoke-RestMethod -Uri "http://127.0.0.1:5100/api/v1/backups/create" -Method Post
   ```

### Step 2: Run Signed Installer
1. Run `GrozetRetailPOS_Server_Setup.exe` (or `GrozetRetailPOS_Setup.exe` for single-station setups).
2. The installer automatically stops the `GrozetPOSServer` Windows Service.
3. The installer replaces application binaries in `C:\Program Files\GROZET\RetailPOS\` with the new version.
4. **Customer Data Preservation**: The installer strictly leaves all customer data in `%ProgramData%\GROZET\RetailPOS\` untouched.

### Step 3: Automatic Database Schema Migration
1. The installer restarts the `GrozetPOSServer` Windows Service.
2. On startup, `SafeMigrationRunner.cs` automatically creates a pre-migration backup snapshot, acquires the maintenance gate, applies any new schema DDL updates (e.g. migrating schema 14 to 15), and releases the maintenance gate.

### Step 4: Upgrade Counter Terminals
1. Run `GrozetRetailPOS_Client_Setup.exe` on each counter terminal.
2. Launch `RetailPOS.Client.exe`. The client terminal verifies the protocol version handshake (`X-Client-Protocol-Version: 1`) and resumes normal cashier billing.

---

## 5. Rollback Procedures & Downgrade Prevention

To prevent catastrophic database corruption:
- **Installer Downgrade Blocking**: Inno Setup installers inspect registry `Uninstall\AppId` entries and warn operators against running an older installer over a newer installation.
- **Schema Protection**: Older application binaries cannot operate against a newer database schema. If an upgrade must be rolled back, the operator must execute the secured loopback rollback endpoint (`/api/v1/recovery/restore-database`) using Store Owner credentials before reinstalling the previous binary package.
