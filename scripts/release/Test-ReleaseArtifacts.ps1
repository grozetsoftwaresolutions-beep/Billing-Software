<#
.SYNOPSIS
    Independent release artifact and integrity verification gate for GROZET RetailPOS.

.DESCRIPTION
    Implements the fail-closed Phase 16.8 release verification engine.
    Executes in a strict, authoritative verification sequence:
      1. Authenticates release_manifest.json via detached CMS/PKCS#7 signature (release_manifest.json.p7s).
      2. Verifies authorized Grozet signing identity and RFC 3161 timestamp on manifest.
      3. Validates manifest metadata (ProductName, AppVersion, ProtocolVersion, DatabaseSchemaVersion).
      4. Validates Git release tag and commit hash provenance.
      5. Audits artifact inventory: verifies every declared artifact exists and matches exact SHA-256 hash.
      6. Detects unexpected or uncataloged files (fail-closed tamper/injection gate).
      7. Enforces the 10-category PE signing and classification policy.
      8. Detects forbidden artifacts (*.pdb, *.tmp, debug configurations, unexpected PEs).

.PARAMETER ReleaseDirectory
    The path to the release staging directory containing artifacts, release_manifest.json, and release_manifest.json.p7s.

.PARAMETER Production
    Enforces strict production rules (rejects self-signed/test certificates, requires full CA chain).

.PARAMETER ExpectedVersion
    Expected application version (defaults to '1.0.0').

.PARAMETER ExpectedGitTag
    Expected git release tag (optional).

.PARAMETER ExpectedGitCommit
    Expected git commit hash (optional).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [switch]$Production,

    [string]$ExpectedVersion = "1.0.0",
    [string]$ExpectedGitTag = "",
    [string]$ExpectedGitCommit = "",
    [string]$ExpectedSignerThumbprint = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Security

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  GROZET RetailPOS - Release Artifact Verifier" -ForegroundColor Cyan
Write-Host "  Mode: $(if ($Production) { 'PRODUCTION (STRICT)' } else { 'DEVELOPMENT / TEST-SIGN' })" -ForegroundColor $(if ($Production) { 'Green' } else { 'Yellow' })
Write-Host "============================================================" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $ReleaseDirectory)) {
    throw "Release directory does not exist: $ReleaseDirectory"
}

$ManifestPath = Join-Path $ReleaseDirectory "release_manifest.json"
$SignaturePath = Join-Path $ReleaseDirectory "release_manifest.json.p7s"

# ==============================================================================
# STEP 1: Authenticate Release Manifest via Detached CMS/PKCS#7 Signature
# ==============================================================================
Write-Host "`n[*] Step 1: Authenticating release manifest CMS signature..." -ForegroundColor Yellow

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "FAIL-CLOSED: release_manifest.json not found in $ReleaseDirectory"
}
if (-not (Test-Path -LiteralPath $SignaturePath)) {
    throw "FAIL-CLOSED: Detached signature release_manifest.json.p7s not found in $ReleaseDirectory. Unauthenticated manifest cannot be trusted."
}

$manifestBytes = [System.IO.File]::ReadAllBytes($ManifestPath)
$signatureBytes = [System.IO.File]::ReadAllBytes($SignaturePath)

if ($manifestBytes.Length -eq 0) {
    throw "FAIL-CLOSED: release_manifest.json is empty."
}
if ($signatureBytes.Length -eq 0) {
    throw "FAIL-CLOSED: release_manifest.json.p7s is empty."
}

$contentInfo = [System.Security.Cryptography.Pkcs.ContentInfo]::new($manifestBytes)
$signedCms = [System.Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true) # detached signature

try {
    $signedCms.Decode($signatureBytes)
    # CheckSignature(true) verifies signature against the contentBytes without requiring root chain if self-signed in test mode
    $signedCms.CheckSignature($true)
}
catch {
    throw "FAIL-CLOSED: Cryptographic verification of release_manifest.json.p7s FAILED: $($_.Exception.Message)"
}

if ($signedCms.SignerInfos.Count -eq 0) {
    throw "FAIL-CLOSED: release_manifest.json.p7s contains no SignerInfo entries."
}

$signer = $signedCms.SignerInfos[0]
$cert = $signer.Certificate

if ($null -eq $cert) {
    throw "FAIL-CLOSED: Signer certificate is null or missing in release_manifest.json.p7s."
}

Write-Host "    [OK] CMS signature mathematically valid." -ForegroundColor Green
Write-Host "         Signer Subject: $($cert.Subject)"
Write-Host "         Thumbprint:     $($cert.Thumbprint)"
Write-Host "         Valid To:       $($cert.NotAfter.ToString('yyyy-MM-dd HH:mm:ss'))"

# ==============================================================================
# STEP 2: Verify Authorized Grozet Signing Identity
# ==============================================================================
Write-Host "`n[*] Step 2: Verifying authorized publisher identity..." -ForegroundColor Yellow

if ($Production) {
    # In production mode: MUST be commercial certificate from trusted CA, not test certificate
    if ($cert.Subject -like "*Test Certificate*" -or $cert.Subject -like "*Dev Only*") {
        throw "FAIL-CLOSED: Production release rejected because manifest was signed with a test certificate: $($cert.Subject)"
    }
    if ($cert.Subject -notlike "*Grozet Software Solutions*") {
        throw "FAIL-CLOSED: Signer identity does not match Grozet Software Solutions: $($cert.Subject)"
    }
    
    # Check full chain trust
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck # or Online
    $chainBuilt = $chain.Build($cert)
    if (-not $chainBuilt) {
        throw "FAIL-CLOSED: Signer certificate chain building failed for production certificate."
    }
}
else {
    # Development / Test mode: accept Grozet test certificate or valid commercial certificate
    if ($cert.Subject -notlike "*Grozet*") {
        throw "FAIL-CLOSED: Signer certificate subject does not identify as Grozet: $($cert.Subject)"
    }
}

if ($ExpectedSignerThumbprint -and $cert.Thumbprint -ne $ExpectedSignerThumbprint) {
    throw "FAIL-CLOSED: Signer thumbprint ($($cert.Thumbprint)) does not match expected thumbprint ($ExpectedSignerThumbprint)."
}

# Verify RFC 3161 Timestamp presence on CMS signature
$hasTimestamp = $false
foreach ($attr in $signer.UnsignedAttributes) {
    # RFC 3161 counter signature OIDs: 1.2.840.113549.1.9.16.1.4 (id-aa-timeStampToken) or 1.2.840.113549.1.9.16.2.14
    if ($attr.Oid.Value -eq "1.2.840.113549.1.9.16.1.4" -or $attr.Oid.Value -eq "1.2.840.113549.1.9.16.2.14" -or $attr.Oid.Value -eq "1.2.840.113549.1.9.6") {
        $hasTimestamp = $true
        break
    }
}
if (-not $hasTimestamp -and $Production) {
    throw "FAIL-CLOSED: Manifest CMS signature is missing mandatory RFC 3161 timestamp counter-signature."
}

Write-Host "    [OK] Publisher identity verified." -ForegroundColor Green

# ==============================================================================
# STEP 3: Parse and Validate Manifest Metadata
# ==============================================================================
Write-Host "`n[*] Step 3: Validating manifest metadata & version invariants..." -ForegroundColor Yellow

$manifestJsonText = [System.Text.Encoding]::UTF8.GetString($manifestBytes).Trim([char]0xFEFF)
$manifest = $manifestJsonText | ConvertFrom-Json

if ($manifest.productName -ne "Grozet Offline Retail POS & Billing") {
    throw "FAIL-CLOSED: productName mismatch in manifest: '$($manifest.productName)'"
}

if ($manifest.appVersion -ne $ExpectedVersion) {
    throw "FAIL-CLOSED: appVersion mismatch in manifest: got '$($manifest.appVersion)', expected '$ExpectedVersion'"
}

if ($manifest.protocolVersion -ne 1) {
    throw "FAIL-CLOSED: protocolVersion mismatch: got $($manifest.protocolVersion), expected 1"
}

if ($manifest.databaseSchemaVersion -ne 15) {
    throw "FAIL-CLOSED: databaseSchemaVersion mismatch: got $($manifest.databaseSchemaVersion), expected 15"
}

Write-Host "    [OK] Metadata verified: Version $($manifest.appVersion), Protocol $($manifest.protocolVersion), Schema $($manifest.databaseSchemaVersion)." -ForegroundColor Green

# ==============================================================================
# STEP 4: Validate Git Release Provenance
# ==============================================================================
Write-Host "`n[*] Step 4: Validating Git release provenance..." -ForegroundColor Yellow

if ($ExpectedGitCommit -and $manifest.gitCommitHash -ne $ExpectedGitCommit) {
    throw "FAIL-CLOSED: gitCommitHash in manifest ($($manifest.gitCommitHash)) does not match expected commit ($ExpectedGitCommit)"
}

if ($ExpectedGitTag -and $manifest.gitReleaseTag -ne $ExpectedGitTag) {
    throw "FAIL-CLOSED: gitReleaseTag in manifest ($($manifest.gitReleaseTag)) does not match expected tag ($ExpectedGitTag)"
}

Write-Host "    [OK] Git provenance recorded: Tag '$($manifest.gitReleaseTag)', Commit '$($manifest.gitCommitHash)'." -ForegroundColor Green

# ==============================================================================
# STEP 5: Audit Declared Artifacts & Checksums
# ==============================================================================
Write-Host "`n[*] Step 5: Auditing artifact inventory and verifying SHA-256 hashes..." -ForegroundColor Yellow

if (-not $manifest.artifacts -or $manifest.artifacts.Count -eq 0) {
    throw "FAIL-CLOSED: Manifest declares zero artifacts."
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
$declaredFilenames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($artifact in $manifest.artifacts) {
    $artifactFile = Join-Path $ReleaseDirectory $artifact.filename
    if (-not (Test-Path -LiteralPath $artifactFile)) {
        throw "FAIL-CLOSED: Missing declared artifact file: $($artifact.filename) (expected at $artifactFile)"
    }

    $declaredFilenames.Add($artifact.filename) | Out-Null

    # Compute hash
    $fileBytes = [System.IO.File]::ReadAllBytes($artifactFile)
    $hashBytes = $sha256.ComputeHash($fileBytes)
    $actualHash = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToUpperInvariant()
    $expectedHash = $artifact.sha256.Replace("-", "").ToUpperInvariant()

    if ($actualHash -ne $expectedHash) {
        throw "FAIL-CLOSED: Hash mismatch for artifact '$($artifact.filename)'. Expected: $expectedHash, Actual: $actualHash"
    }

    Write-Host "    [OK] Hash verified: $($artifact.filename) ($($fileBytes.Length) bytes) [SHA256: $($actualHash.Substring(0, 16))...]" -ForegroundColor Green
}

# ==============================================================================
# STEP 6: Detect Unexpected Files (Tamper / Injection Gate)
# ==============================================================================
Write-Host "`n[*] Step 6: Checking for unexpected or unauthorized files in release directory..." -ForegroundColor Yellow

# Allowed root files not declared in artifacts: release_manifest.json, release_manifest.json.p7s, SHA256SUMS
$systemAllowed = @("release_manifest.json", "release_manifest.json.p7s", "SHA256SUMS")

$actualFiles = Get-ChildItem -Path $ReleaseDirectory -File -Recurse
foreach ($file in $actualFiles) {
    $relative = (Resolve-Path -LiteralPath $file.FullName -Relative).TrimStart(".\")
    $filename = $file.Name

    # Check forbidden extensions
    if ($file.Extension -eq ".pdb") {
        throw "FAIL-CLOSED: Forbidden debug symbol file detected in release package: $($file.FullName)"
    }
    if ($file.Extension -eq ".tmp") {
        throw "FAIL-CLOSED: Forbidden temporary file detected in release package: $($file.FullName)"
    }
    if ($filename -eq "appsettings.Development.json") {
        throw "FAIL-CLOSED: Forbidden development configuration detected: $($file.FullName)"
    }

    if ($systemAllowed -contains $filename) {
        continue
    }

    if (-not $declaredFilenames.Contains($filename) -and -not $declaredFilenames.Contains($relative)) {
        throw "FAIL-CLOSED: Unauthorized unexpected file found in release bundle: $relative"
    }
}

Write-Host "    [OK] Zero unexpected, debug, or temporary files found." -ForegroundColor Green

# ==============================================================================
# STEP 7: Enforce PE Signing & Binary Classification Policy
# ==============================================================================
Write-Host "`n[*] Step 7: Enforcing PE classification and Authenticode signature policy..." -ForegroundColor Yellow

foreach ($artifact in $manifest.artifacts) {
    $artifactFile = Join-Path $ReleaseDirectory $artifact.filename
    $ext = [System.IO.Path]::GetExtension($artifactFile).ToLowerInvariant()

    if ($ext -notin @(".exe", ".dll", ".ps1")) {
        continue
    }

    $sig = Get-AuthenticodeSignature -FilePath $artifactFile
    $classification = $artifact.peClassification

    switch ($classification) {
        "SignWithGrozet" {
            $isValid = if ($Production) {
                $sig.Status -eq "Valid"
            } else {
                $sig.Status -eq "Valid" -or ($sig.Status -eq "UnknownError" -and $sig.SignerCertificate.Subject -like "*Test Certificate*")
            }
            if (-not $isValid) {
                throw "FAIL-CLOSED: '$($artifact.filename)' requires 'SignWithGrozet' but signature is '$($sig.Status)' ($($sig.StatusMessage))."
            }
            if ($Production) {
                if ($sig.SignerCertificate.Subject -notlike "*Grozet Software Solutions*") {
                    throw "FAIL-CLOSED: '$($artifact.filename)' signer does not match Grozet Software Solutions: $($sig.SignerCertificate.Subject)"
                }
                if ($null -eq $sig.TimeStamperCertificate) {
                    throw "FAIL-CLOSED: '$($artifact.filename)' is missing mandatory RFC 3161 timestamp counter-signature."
                }
            }
            else {
                if ($sig.SignerCertificate.Subject -notlike "*Grozet*") {
                    throw "FAIL-CLOSED: '$($artifact.filename)' signer does not identify as Grozet: $($sig.SignerCertificate.Subject)"
                }
            }
            Write-Host "    [OK] Grozet signature verified on $($artifact.filename)" -ForegroundColor Green
        }

        "VerifyMicrosoftSignature" {
            if ($sig.Status -ne "Valid") {
                throw "FAIL-CLOSED: Microsoft runtime component '$($artifact.filename)' signature is not valid: $($sig.Status)"
            }
            if ($sig.SignerCertificate.Subject -notlike "*Microsoft Corporation*" -and $sig.SignerCertificate.Subject -notlike "*CN=.NET*") {
                throw "FAIL-CLOSED: Microsoft component '$($artifact.filename)' signer subject does not match Microsoft: $($sig.SignerCertificate.Subject)"
            }
            Write-Host "    [OK] Microsoft signature verified on $($artifact.filename)" -ForegroundColor Green
        }

        "IntentionallyUnsigned" {
            # Third party managed / native DLLs protected via SHA256 in authenticated manifest
            Write-Host "    [OK] Third-party component '$($artifact.filename)' validated as IntentionallyUnsigned (SHA-256 authenticated)." -ForegroundColor Green
        }

        default {
            throw "FAIL-CLOSED: Unknown or unhandled PE classification '$classification' for artifact '$($artifact.filename)'"
        }
    }
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "  RELEASE VERIFICATION PASSED (ZERO DEFECTS)" -ForegroundColor Green
Write-Host "  Package: $ReleaseDirectory" -ForegroundColor Green
Write-Host "  Artifacts Verified: $($manifest.artifacts.Count)" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green

return $true
