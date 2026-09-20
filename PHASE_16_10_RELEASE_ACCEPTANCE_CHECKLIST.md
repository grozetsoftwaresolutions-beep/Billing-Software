# PHASE 16.10 — RELEASE ACCEPTANCE CHECKLIST

| Document Metadata | Value |
| :--- | :--- |
| **Document ID** | `GROZET-ACC-1610-005` |
| **System Version** | `1.0.0` |
| **Release Pipeline** | Phase 16.8 Commercial Release Engineering Specification |
| **Execution Date** | `2026-09-20` |
| **Classification** | Commercial Release Verification & Signing Audit Checklist |

---

## 1. Executive Summary

This checklist documents the final pre-release commercial verification for GROZET RetailPOS Version `1.0.0`. It audits digital code signing, RFC 3161 timestamping, the empirical 10-category PE classification policy, detached CMS manifest signatures, Inno Setup installer parameters, and secret hygiene.

All release acceptance gates have been evaluated and verified:

```
================================================================================
                    COMMERCIAL RELEASE ACCEPTANCE GATE
================================================================================
  1. Authenticode & Timestamp Verification:              PASS ✅
  2. Empirical PE Classification Policy:                 PASS ✅
  3. Authenticated Release Manifest (CMS .p7s):          PASS ✅
  4. Git Provenance & Clean Tree Gate:                   PASS ✅
  5. Inno Setup Package & Silent Switches:               PASS ✅
  6. Windows Service Deployment Signature Gate:          PASS ✅
  7. Automated Repository Secret & PII Scan:             PASS ✅
  Overall Release Status:                                APPROVED FOR RELEASE ✅
================================================================================
```

---

## 2. Release Acceptance Checklist

| # | Acceptance Check Item | Verification Standard | Target Specification | Actual Empirical Evidence | Result |
|---|---|---|---|---|---|
| **1** | **Grozet PE Authenticode Signatures** | All Grozet-authored EXEs and DLLs must be digitally signed | Valid Authenticode signature from authorized publisher | `RetailPOS.Server.exe`, `RetailPOS.Client.exe`, and Grozet DLLs signed; trust chain valid | PASS ✅ |
| **2** | **RFC 3161 Timestamping** | Validate timestamp evidence so signatures remain verifiable past cert expiry | RFC 3161 SHA-256 Authenticode timestamp token embedded | Timestamps present on all Grozet PE binaries; RFC 3161 countersignature verified | PASS ✅ |
| **3** | **Microsoft Runtime Preservation** | Microsoft .NET runtime binaries retain original Microsoft signatures; **NEVER re-signed** | Signed by `CN=.NET, O=Microsoft Corporation` | Microsoft runtime DLLs and `createdump.exe` audited; original MS signatures intact | PASS ✅ |
| **4** | **Approved Third-Party OSS Binaries** | Third-party OSS DLLs intentionally unsigned; integrity bound to signed release manifest | Verified as un-altered upstream binaries | `e_sqlite3.dll`, `Serilog.dll`, `BouncyCastle.Cryptography.dll` verified via manifest SHA-256 | PASS ✅ |
| **5** | **Authenticated Release Manifest** | Manifest contains SHA-256 hashes of all release files; signed via detached CMS signature | `release_manifest.json` + `release_manifest.json.p7s` | Detached PKCS#7 CMS signature verified with `SignedCms.CheckSignature(true)` | PASS ✅ |
| **6** | **Git Release Provenance** | Build strictly bound to Git release tag and commit from a 100% clean working tree | Zero uncommitted files (`git status --porcelain`); tag = HEAD | Verified clean Git working tree; commit and tag recorded in signed manifest | PASS ✅ |
| **7** | **Inno Setup Silent Installation** | Installer supports standard Inno Setup silent execution parameters | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | Silent installation executes cleanly; no MSI `/qn` switches used; return code 0 | PASS ✅ |
| **8** | **Windows Service SCM Registration** | Service registered as `GrozetPOSServer` under `NT SERVICE\GrozetPOSServer` | Dedicated virtual service account; automatic startup | `sc.exe qc GrozetPOSServer` verifies virtual service account and restart recovery | PASS ✅ |
| **9** | **NTFS Directory ACL Separation** | Immutable binaries in Program Files (RX); operational mutable state in ProgramData | Stripped inheritance; least-privilege NTFS ACLs | `icacls` verifies `NT SERVICE\GrozetPOSServer` restricted; no write access to Program Files | PASS ✅ |
| **10** | **Firewall Profile Restrictions** | Windows Firewall rules permit only TCP 5100, 5101, UDP 5105 on `Private` profile | No exposure on `Public` profile; zero direct DB exposure | `Get-NetFirewallRule` verifies 3 rules bound strictly to Private network profile | PASS ✅ |
| **11** | **Zero Production Secrets** | Zero private keys, 64-hex recovery keys, production passwords, or PII in repo | Clean secret scan across all source and config files | Repository scan confirms 0 private keys, 0 PFX files, and zero hardcoded credentials | PASS ✅ |

---

## 3. Empirical 10-Category PE Classification Audit

| Category | Component Scope | Signing & Classification Policy | Verification Result |
|---|---|---|---|
| **Cat 1 & 2** | Grozet Server & Client Executables / DLLs | Signed with Grozet Code Signing Certificate + RFC 3161 SHA-256 timestamp | Valid Authenticode Signature ✅ |
| **Cat 3 & 4** | Microsoft .NET Runtime Binaries & `createdump.exe` | Original Microsoft Authenticode signatures preserved; **NOT re-signed** | Original Microsoft Digital Trust Intact ✅ |
| **Cat 5 & 6** | Upstream OSS Managed & Native DLLs (`e_sqlite3.dll`, etc.) | Intentionally unsigned; integrity secured via authenticated CMS manifest SHA-256 | SHA-256 Matched Against Signed Manifest ✅ |
| **Cat 7** | Uncataloged Third-Party Binaries | Strictly forbidden; fail-closed abort triggered if encountered | Zero Uncataloged Binaries Found ✅ |
| **Cat 8** | Deployment Scripts (`Deploy-WindowsService.ps1`, etc.) | Signed with PowerShell Authenticode for `AllSigned` policy compliance | Script Signature Valid ✅ |
| **Cat 9** | Production Configurations (`appsettings.Production.json`, etc.) | Validated against authenticated release manifest SHA-256 hashes | Hashes Verified ✅ |
| **Cat 10** | Debug & Temporary Artifacts (`*.pdb`, `*.tmp`, test configs) | Strictly excluded from production release packages | 100% Excluded from Release Payload ✅ |

---

## 4. SmartScreen, Authenticode & UAC Standards

As established in the locked architecture:
> *"Authenticode signatures provide executable integrity and identify the signing publisher when Windows successfully validates the signature and trust chain. UAC/SmartScreen presentation is determined by Windows security policy and trust/reputation signals and must not be described as guaranteed."*
>
> *"RFC 3161 timestamp verification: Validate the timestamp and signing-time evidence so signatures can remain verifiable after the signing certificate expires, subject to certificate-chain trust, revocation/status validation, algorithm validity, and platform verification policy."*

---

## 5. Release Verification Gate Sign-Off

* **Release Artifacts Location**: `artifacts/releases/v1.0.0/`
* **Release Manifest**: `release_manifest.json` (SHA-256 Digest Verified)
* **Release Signature**: `release_manifest.json.p7s` (CMS / PKCS#7 Detached Signature Verified)
* **Installer Executable**: `GrozetRetailPOS_Setup.exe` (Authenticode Verified)

**Commercial Release Status: APPROVED FOR RELEASE ✅**
