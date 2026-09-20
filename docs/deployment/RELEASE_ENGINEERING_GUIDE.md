# GROZET RetailPOS — Release Engineering & Code Signing Guide

> [!IMPORTANT]
> **Authoritative Specification — Phase 16.8**
> This document defines the commercial-grade release engineering, Windows Authenticode signing, RFC 3161 timestamping, release artifact integrity, and Windows Defender SmartScreen trust model for **GROZET RetailPOS**.

---

## 1. Release Architecture Overview

GROZET RetailPOS distributes software for Windows 10 and Windows 11 point-of-sale environments. To establish operating system trust, eliminate alarming "Unknown Publisher" security warnings, and guarantee end-to-end cryptographic provenance, all distributed executables, deployment scripts, installers, and release manifests are secured under a fail-closed release engineering pipeline.

### Core Release Components
1. **First-Party Binaries**: `RetailPOS.Server.exe`, `RetailPOS.Client.exe`, and managed first-party DLLs (`RetailPOS.*.dll`) signed with Authenticode SHA-256 and RFC 3161 timestamped.
2. **Runtime Binary Classification**: Microsoft .NET runtime binaries (`coreclr.dll`, `createdump.exe`, `System.*.dll`, `Microsoft.*.dll`) are audited to verify original Microsoft digital signatures (`CN=.NET, O=Microsoft Corporation`) and are **never re-signed**.
3. **Third-Party Libraries**: Upstream dependencies (`e_sqlite3.dll`, `Serilog.dll`, `BouncyCastle.Cryptography.dll`) are verified via SHA-256 hashes against the cryptographically authenticated manifest.
4. **Installer Packages**: Three Inno Setup installers (`GrozetRetailPOS_Setup.exe`, `GrozetRetailPOS_Server_Setup.exe`, `GrozetRetailPOS_Client_Setup.exe`) packaging pre-signed payloads, with the installer executables themselves Authenticode signed and timestamped.
5. **Authenticated Release Manifest**: `release_manifest.json` authenticated via a detached CMS/PKCS#7 signature (`release_manifest.json.p7s`). Hashes in the manifest are only trusted after cryptographic signature validation.
6. **Git Release Provenance**: Release builds enforce a clean Git working tree and verify that the specified release tag exists and matches current `HEAD`.

---

## 2. Certificate Architecture & Acquisition

### A. Production Code Signing Certificates
Official production releases require an Extended Validation (EV) or Organization Validation (OV) Code Signing Certificate issued by a Microsoft-trusted commercial Certificate Authority (DigiCert, Sectigo, GlobalSign):

- **Subject Identity**: `CN=Grozet Software Solutions Private Limited, O=Grozet Software Solutions Private Limited, C=IN`
- **Key Storage**: High-security hardware security module (FIPS 140-2 Level 2+ USB token, e.g., SafeNet eToken 5110) or Microsoft Azure Artifact Signing HSM.
- **Key Protection**: Private keys are non-exportable and protected by physical token hardware with PIN entry.
- **Zero Secrets in Git**: Private keys, `.pfx` files, or token passwords must **NEVER** be committed to source control or stored in plain CI/CD repository secrets.

### B. Development & Test Signing
For local developer testing and CI test suite validation, ephemeral self-signed certificates are generated via `scripts/release/New-TestCertificate.ps1`:
```powershell
# Generate an isolated development test certificate in Cert:\CurrentUser\My
.\scripts\release\New-TestCertificate.ps1
```
> [!WARNING]
> Test certificates are strictly rejected by the production release verification gate (`Test-ReleaseArtifacts.ps1 -Production`). Production builds cannot proceed with a development test certificate.

---

## 3. RFC 3161 Secure Timestamping

### A. Timestamping Purpose & Legal/Platform Model
> [!IMPORTANT]
> **Platform Trust Invariant**:
> Timestamping preserves signature validation after certificate expiration, subject to the applicable certificate chain, timestamp trust, revocation policy, and platform validation rules.

When an Authenticode signature lacks an RFC 3161 timestamp, Windows evaluates signature validity against the **current system clock**. Once the certificate expires, Windows treats the application as untrusted. 

With an RFC 3161 timestamp:
1. The signing utility computes the cryptographic digest of the signature.
2. The digest is transmitted to an accredited Time Stamping Authority (TSA).
3. The TSA binds the digest to an authoritative atomic clock timestamp and returns a Time Stamp Token (TST).
4. Windows validates that the signing certificate was active at the exact time the signature was created.

### B. Timestamp Authority Endpoints & Fallback
The release pipeline configures dual redundant RFC 3161 endpoints with fail-closed enforcement:
- **Primary TSA**: `http://timestamp.digicert.com` (RFC 3161 SHA-256)
- **Backup TSA**: `http://timestamp.sectigo.com` (RFC 3161 SHA-256)
- **Fail-Closed Rule**: If timestamping fails across both TSAs, the production build **terminates immediately**. An untimestamped production release is never published.

---

## 4. Modern SmartScreen & Windows Trust Reality

Understanding Windows Defender SmartScreen heuristics is critical for customer support and commercial deployment:

### What Authenticode Guarantees
- **Signature Integrity**: Mathematical proof that binaries have not been altered or corrupted since signing.
- **Publisher Authenticity**: Verification of the legal identity of Grozet Software Solutions Private Limited.
- **UAC Interface Elevation**: Converts Windows User Account Control (UAC) prompts from alarming yellow/orange "Unknown Publisher" dialogs to blue, authenticated publisher prompts.

### What Authenticode Does NOT Guarantee (SmartScreen Reality)
- **No Automatic Bypass**: Modern Windows 10 and 11 Defender SmartScreen does NOT automatically suppress warnings simply because a binary is signed.
- **EV Certificate Updates**: EV certificates no longer grant automatic, instant SmartScreen bypass for brand new binary hashes.
- **Reputation Accumulation**: Microsoft Defender SmartScreen evaluates reputation dynamically across both publisher certificates and unique file hashes based on telemetry from millions of endpoints.
- **No Guaranteed Threshold**: Reputation accumulation is governed exclusively by Microsoft algorithms; there is no fixed number of downloads that guarantees warning suppression.
- **Microsoft Security Intelligence Submission**: Submitting newly signed binaries to [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission) is recommended for enterprise security review and false-positive resolution, but is **not** a guaranteed consumer reputation-seeding mechanism.

### Customer Deployment Guidance
During initial deployment rollouts of a new version, store technicians and administrators should follow standard Windows verification:
1. When Windows displays *"Windows protected your PC - Microsoft Defender SmartScreen prevented an unrecognized app from starting"*:
2. Click **"More info"**.
3. Verify that the Publisher is explicitly listed as **"Grozet Software Solutions Private Limited"** (or authorized Grozet entity).
4. Click **"Run anyway"** to complete installation.

---

## 5. Step-by-Step Production Release Procedure

### Stage 1: Git Release Preparation
1. Ensure all code changes are committed and pushed.
2. Create and push a Git release tag matching the application version:
   ```bash
   git tag -a v1.0.0 -m "Release v1.0.0"
   git push origin v1.0.0
   ```
3. Confirm working tree is 100% clean:
   ```bash
   git status
   ```

### Stage 2: Execute Master Release Build
Insert the hardware security token, open an elevated PowerShell prompt, and execute:

```powershell
# Production Release Build
.\scripts\release\New-ReleaseBuild.ps1 `
    -ReleaseTag "v1.0.0" `
    -Configuration "Release" `
    -CertificateThumbprint "<PRODUCTION_CERT_THUMBPRINT>" `
    -Production
```

For developer pipeline testing without a hardware token:
```powershell
# Developer / Test-Signed Release Build
.\scripts\release\New-ReleaseBuild.ps1 `
    -ReleaseTag "v1.0.0" `
    -Configuration "Release" `
    -TestSign `
    -SkipGitCheck
```

### Stage 3: Release Artifact Staging
Upon completion, the build script generates the verified release package in:
`artifacts\releases\v1.0.0\`

Package contents:
- `GrozetRetailPOS_Setup.exe` (Unified Installer)
- `GrozetRetailPOS_Server_Setup.exe` (Dedicated Server Installer)
- `GrozetRetailPOS_Client_Setup.exe` (Dedicated Client Terminal Installer)
- `release_manifest.json` (Machine-readable release metadata & hashes)
- `release_manifest.json.p7s` (Detached CMS/PKCS#7 cryptographic signature)
- `SHA256SUMS` (Standard checksum manifest)

---

## 6. Independent Release Verification

The release verification engine (`scripts\release\Test-ReleaseArtifacts.ps1`) can be executed independently by QA engineers, store technicians, or automated audit scripts:

```powershell
.\scripts\release\Test-ReleaseArtifacts.ps1 `
    -ReleaseDirectory "artifacts\releases\v1.0.0" `
    -Production `
    -ExpectedVersion "1.0.0" `
    -ExpectedGitTag "v1.0.0"
```

### Verification Pipeline Sequence
1. **Manifest Authentication**: Decodes `release_manifest.json.p7s` and cryptographically validates the signature over `release_manifest.json`. Rejects release if signature is missing, invalid, or signed by an unauthorized identity.
2. **Metadata Audit**: Checks `appVersion == 1.0.0`, `protocolVersion == 1`, `databaseSchemaVersion == 15`, and Git tag/commit provenance.
3. **Artifact Integrity**: Recomputes SHA-256 for all package files and compares against authenticated manifest hashes.
4. **Tamper / Injection Detection**: Scans directory for any uncataloged, debug (`*.pdb`), or temporary (`*.tmp`) files.
5. **PE Policy Enforcement**: Checks Authenticode status on all executables and confirms Microsoft runtime binaries retain original Microsoft signatures.

---

## 7. Windows Service Deployment Signature Gate

`scripts\deployment\Deploy-WindowsService.ps1` protects the Windows Service Control Manager from registering untrusted binaries:

```powershell
# Standard Production Service Installation (enforces valid Authenticode signature)
.\scripts\deployment\Deploy-WindowsService.ps1 -Action Install

# Developer Override (only for development testing)
.\scripts\deployment\Deploy-WindowsService.ps1 -Action Install -AllowUnsignedBinaries
```

Before calling `sc.exe create`, the script queries `Get-AuthenticodeSignature` on `RetailPOS.Server.exe`. If the signature is invalid or missing, installation is aborted with an error.

---

## 8. Emergency Response & Key Compromise Protocol

In the unlikely event of a suspected or confirmed compromise of a code signing private key:

1. **Immediate Revocation**: The designated security officer contacts the issuing Certificate Authority (DigiCert / Sectigo) to revoke the certificate immediately via CRL and OCSP.
2. **Impact Assessment**: Binaries signed and timestamped **prior** to the revocation date retain cryptographic validity if the revocation reason is specified with an effective date.
3. **Emergency Certificate Provisioning**: Obtain a replacement commercial certificate on a new hardware token.
4. **Rapid Release Patch**:
   - Update repository `.gitignore` and release scripts if thumbprint pins are used.
   - Rebuild and sign the current release with the new certificate and a fresh timestamp.
   - Run `New-ReleaseBuild.ps1` and distribute the verified update.
5. **Customer Notification**: Notify store administrators to apply the emergency security patch.

---

## 9. Rollback & Version Compatibility

RetailPOS maintains strict backward compatibility and rollback guardrails:
- **Installer Downgrade Blocking**: Inno Setup installers inspect registry `Uninstall\AppId` entries and warn operators against running an older installer over a newer installation.
- **Database Schema Protection**: The SQLite database schema is at Version 15. Previous application binaries cannot run against a newer schema without executing the secured Phase 16.6 loopback rollback engine (`/api/v1/recovery/restore-database`).
- **Archived Signed Releases**: All historical releases (`v1.0.0`, etc.) along with their signed manifests (`release_manifest.json.p7s`) are preserved in release archives to enable verified rollbacks when authorized by store management.
