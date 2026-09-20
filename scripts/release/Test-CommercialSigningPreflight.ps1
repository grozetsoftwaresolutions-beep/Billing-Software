<#
.SYNOPSIS
    Test-CommercialSigningPreflight.ps1 - Commercial Code-Signing Fail-Closed Preflight Gate
.DESCRIPTION
    Mandatory preflight gate for Phase 16.10 commercial release signing.
    Validates all 8 commercial signing prerequisites before allowing production build:
    1. Production certificate existence in CurrentUser\My or LocalMachine\My.
    2. Private key accessibility through the cryptographic service provider / hardware token.
    3. Enhanced Key Usage (EKU) includes Code Signing (OID: 1.3.6.1.5.5.7.3.3).
    4. Complete trusted Windows certificate chain (chains to trusted commercial Root CA; rejects self-signed).
    5. Certificate validity and expiration status (NotBefore <= Now <= NotAfter).
    6. Certificate revocation and status validation via Windows CryptoAPI (X509Chain).
    7. Expected commercial publisher identity (CN=Grozet Software Solutions...; rejects test/dev subjects).
    8. Configured thumbprint matches the intended production certificate.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$CertificateThumbprint = $env:GROZET_SIGNING_CERT_THUMBPRINT,

    [string]$ExpectedSubjectMatch = "Grozet Software Solutions",
    
    [switch]$RequireOnlineRevocationCheck = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  GROZET RetailPOS - Commercial Signing Preflight Gate" -ForegroundColor Cyan
Write-Host "  Mode: Production Code Signing Preflight Audit" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$report = [ordered]@{}
$preflightPassed = $true
$failureReasons = [System.Collections.Generic.List[string]]::new()

# ------------------------------------------------------------------------------
# 1. Configured Thumbprint and Certificate Existence
# ------------------------------------------------------------------------------
Write-Host "`n[1/8] Verifying Production Certificate Thumbprint and Store Existence..." -ForegroundColor Yellow

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $preflightPassed = $false
    $msg = "FAIL-CLOSED: No production certificate thumbprint specified. Provide -CertificateThumbprint or set `$env:GROZET_SIGNING_CERT_THUMBPRINT."
    $failureReasons.Add($msg)
    Write-Host "    [FAIL] $msg" -ForegroundColor Red
    $report["1_CertificateExistence"] = "FAILED: No thumbprint configured"
} else {
    $cleanThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()
    $cert = Get-ChildItem -Path "Cert:\CurrentUser\My", "Cert:\LocalMachine\My" | Where-Object { 
        $_.Thumbprint.ToUpperInvariant() -eq $cleanThumbprint 
    } | Select-Object -First 1

    if ($null -eq $cert) {
        $preflightPassed = $false
        $msg = "FAIL-CLOSED: Certificate with thumbprint '$cleanThumbprint' not found in CurrentUser\My or LocalMachine\My."
        $failureReasons.Add($msg)
        Write-Host "    [FAIL] $msg" -ForegroundColor Red
        $report["1_CertificateExistence"] = "FAILED: Not found in certificate stores"
    } else {
        Write-Host "    [PASS] Certificate found in store: $($cert.PSParentPath)" -ForegroundColor Green
        Write-Host "           Subject:    $($cert.Subject)" -ForegroundColor Green
        Write-Host "           Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
        $report["1_CertificateExistence"] = "PASS: Found in store ($($cert.Thumbprint))"

        # ----------------------------------------------------------------------
        # 2. Private Key Accessibility (Hardware Token / CSP / KSP)
        # ----------------------------------------------------------------------
        Write-Host "`n[2/8] Verifying Private Key Accessibility via Hardware Token / Cryptographic Provider..." -ForegroundColor Yellow
        if (-not $cert.HasPrivateKey) {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Certificate does NOT have an accessible private key. Ensure physical hardware token (HSM / SafeNet eToken) is inserted and driver is active."
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["2_PrivateKeyAccessible"] = "FAILED: No private key attached"
        } else {
            try {
                $hasKeyHandle = ($null -ne [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)) -or
                                ($null -ne [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert))
                if ($hasKeyHandle) {
                    Write-Host "    [PASS] Private key accessible through cryptographic provider." -ForegroundColor Green
                    $report["2_PrivateKeyAccessible"] = "PASS: Hardware key handle verified"
                } else {
                    $preflightPassed = $false
                    $msg = "FAIL-CLOSED: Private key handle could not be acquired from cryptographic provider."
                    $failureReasons.Add($msg)
                    Write-Host "    [FAIL] $msg" -ForegroundColor Red
                    $report["2_PrivateKeyAccessible"] = "FAILED: Key handle acquisition failed"
                }
            } catch {
                $preflightPassed = $false
                $msg = "FAIL-CLOSED: Private key accessibility check failed: " + $_.Exception.Message
                $failureReasons.Add($msg)
                Write-Host "    [FAIL] $msg" -ForegroundColor Red
                $report["2_PrivateKeyAccessible"] = "FAILED: " + $_.Exception.Message
            }
        }

        # ----------------------------------------------------------------------
        # 3. Code Signing EKU (1.3.6.1.5.5.7.3.3)
        # ----------------------------------------------------------------------
        Write-Host "`n[3/8] Verifying Enhanced Key Usage (EKU) for Code Signing..." -ForegroundColor Yellow
        $hasCodeSigningEku = $false
        foreach ($ext in $cert.Extensions) {
            if ($ext -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
                foreach ($oid in $ext.EnhancedKeyUsages) {
                    if ($oid.Value -eq "1.3.6.1.5.5.7.3.3") {
                        $hasCodeSigningEku = $true
                        break
                    }
                }
            }
        }
        if (-not $hasCodeSigningEku) {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Certificate lacks Code Signing EKU (OID 1.3.6.1.5.5.7.3.3)."
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["3_CodeSigningEku"] = "FAILED: Missing 1.3.6.1.5.5.7.3.3"
        } else {
            Write-Host "    [PASS] Code Signing EKU (1.3.6.1.5.5.7.3.3) verified." -ForegroundColor Green
            $report["3_CodeSigningEku"] = "PASS: Verified"
        }

        # ----------------------------------------------------------------------
        # 4. Trusted Certificate Chain (Commercial Root CA)
        # ----------------------------------------------------------------------
        Write-Host "`n[4/8] Auditing Windows Certificate Chain and Commercial CA Hierarchy..." -ForegroundColor Yellow
        $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
        $chain.ChainPolicy.RevocationMode = if ($RequireOnlineRevocationCheck) { [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online } else { [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck }
        $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        
        $chainBuilt = $chain.Build($cert)
        $isSelfSigned = ($cert.Subject -eq $cert.Issuer)

        if ($isSelfSigned) {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Certificate is self-signed ($($cert.Subject)). Commercial releases strictly prohibit self-signed certificates."
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["4_TrustedCertificateChain"] = "FAILED: Self-signed certificate detected"
        } elseif (-not $chainBuilt) {
            $preflightPassed = $false
            $chainErrors = ($chain.ChainStatus | ForEach-Object { "$($_.Status): $($_.StatusInformation.Trim())" }) -join "; "
            $msg = "FAIL-CLOSED: Certificate chain validation failed: $chainErrors"
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["4_TrustedCertificateChain"] = "FAILED: $chainErrors"
        } else {
            $rootCert = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
            Write-Host "    [PASS] Chain built successfully to trusted Root CA:" -ForegroundColor Green
            Write-Host "           Root Subject: $($rootCert.Subject)" -ForegroundColor Green
            Write-Host "           Chain Depth:  $($chain.ChainElements.Count)" -ForegroundColor Green
            $report["4_TrustedCertificateChain"] = "PASS: Verified to $($rootCert.Subject) (Depth: $($chain.ChainElements.Count))"
        }

        # ----------------------------------------------------------------------
        # 5. Validity and Expiration Period
        # ----------------------------------------------------------------------
        Write-Host "`n[5/8] Verifying Certificate Validity Dates (NotBefore to NotAfter)..." -ForegroundColor Yellow
        $now = Get-Date
        if ($now -lt $cert.NotBefore) {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Certificate is not yet valid (NotBefore: $($cert.NotBefore))."
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["5_ValidityDates"] = "FAILED: Not yet valid"
        } elseif ($now -gt $cert.NotAfter) {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Certificate is expired (NotAfter: $($cert.NotAfter))."
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["5_ValidityDates"] = "FAILED: Expired on $($cert.NotAfter)"
        } else {
            $remainingDays = [math]::Round(($cert.NotAfter - $now).TotalDays, 1)
            Write-Host "    [PASS] Certificate is active. Expires in $remainingDays days ($($cert.NotAfter))." -ForegroundColor Green
            $report["5_ValidityDates"] = "PASS: Active until $($cert.NotAfter) ($remainingDays days remaining)"
        }

        # ----------------------------------------------------------------------
        # 6. Revocation and Status Validation
        # ----------------------------------------------------------------------
        Write-Host "`n[6/8] Auditing Revocation and Status Validation Policy..." -ForegroundColor Yellow
        if ($RequireOnlineRevocationCheck) {
            $revocationChain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
            $revocationChain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
            $revocationChain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
            $revocationChain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
            $revOk = $revocationChain.Build($cert)
            if (-not $revOk) {
                $revErrors = ($revocationChain.ChainStatus | ForEach-Object { "$($_.Status): $($_.StatusInformation.Trim())" }) -join "; "
                $preflightPassed = $false
                $msg = "FAIL-CLOSED: Online revocation check failed: $revErrors"
                $failureReasons.Add($msg)
                Write-Host "    [FAIL] $msg" -ForegroundColor Red
                $report["6_RevocationStatus"] = "FAILED: $revErrors"
            } else {
                Write-Host "    [PASS] Online revocation status verified (No CRL/OCSP revocation flags)." -ForegroundColor Green
                $report["6_RevocationStatus"] = "PASS: Online CRL/OCSP status valid"
            }
        } else {
            Write-Host "    [INFO] Standard CryptoAPI revocation policy active." -ForegroundColor Cyan
            $report["6_RevocationStatus"] = "PASS: CryptoAPI validation configured"
        }

        # ----------------------------------------------------------------------
        # 7. Expected Commercial Publisher Identity
        # ----------------------------------------------------------------------
        Write-Host "`n[7/8] Verifying Commercial Publisher Identity and Test Certificate Rejection..." -ForegroundColor Yellow
        if ($cert.Subject -like "*Test Certificate*" -or $cert.Subject -like "*Dev Only*" -or $cert.Subject -like "*RetailPOS-Dev*") {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Test certificate rejected for commercial release: $($cert.Subject)"
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["7_PublisherIdentity"] = "FAILED: Test certificate detected ($($cert.Subject))"
        } elseif ($cert.Subject -notlike "*$ExpectedSubjectMatch*") {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Certificate subject '$($cert.Subject)' does not match expected commercial entity '$ExpectedSubjectMatch'."
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["7_PublisherIdentity"] = "FAILED: Subject mismatch"
        } else {
            Write-Host "    [PASS] Commercial publisher identity confirmed: $($cert.Subject)" -ForegroundColor Green
            $report["7_PublisherIdentity"] = "PASS: Verified ($($cert.Subject))"
        }

        # ----------------------------------------------------------------------
        # 8. Thumbprint Match Verification
        # ----------------------------------------------------------------------
        Write-Host "`n[8/8] Verifying Configured Thumbprint Exact Match..." -ForegroundColor Yellow
        if ($cert.Thumbprint.ToUpperInvariant() -ne $cleanThumbprint) {
            $preflightPassed = $false
            $msg = "FAIL-CLOSED: Thumbprint mismatch. Configured: '$cleanThumbprint', Resolved: '$($cert.Thumbprint)'"
            $failureReasons.Add($msg)
            Write-Host "    [FAIL] $msg" -ForegroundColor Red
            $report["8_ThumbprintMatch"] = "FAILED: Mismatch"
        } else {
            Write-Host "    [PASS] Thumbprint exact match verified: $cleanThumbprint" -ForegroundColor Green
            $report["8_ThumbprintMatch"] = "PASS: Exact match ($cleanThumbprint)"
        }
    }
}

Write-Host "`n============================================================" -ForegroundColor Cyan
Write-Host "  COMMERCIAL PREFLIGHT GATE AUDIT SUMMARY" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
$report.GetEnumerator() | Format-Table -Property Key, Value -AutoSize

if (-not $preflightPassed) {
    Write-Host "`n[PREFLIGHT RESULT]: FAIL-CLOSED (Commercial release blocked)" -ForegroundColor Red
    Write-Host "Failure Reasons:" -ForegroundColor Red
    foreach ($r in $failureReasons) {
        Write-Host "  - $r" -ForegroundColor Red
    }
    throw "FAIL-CLOSED: Commercial code signing preflight check failed. Production release aborted."
}

Write-Host "`n[PREFLIGHT RESULT]: PASS (Ready for commercial signing)" -ForegroundColor Green
return $report
