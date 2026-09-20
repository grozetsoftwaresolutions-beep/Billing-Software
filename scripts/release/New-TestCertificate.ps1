<#
.SYNOPSIS
    Generates an isolated self-signed test code-signing certificate for local RetailPOS development and testing.

.DESCRIPTION
    Creates a self-signed X.509 v3 certificate with Code Signing EKU (1.3.6.1.5.5.7.3.3)
    in the CurrentUser\My certificate store.
    
    CRITICAL SECURITY NOTICE:
    This certificate is for DEVELOPMENT, CI PIPELINE VERIFICATION, AND UNIT TESTING ONLY.
    Production release gates strictly reject self-signed or non-commercial CA certificates.
    Never use this certificate for commercial production releases.

.PARAMETER Subject
    The Subject DN for the test certificate. Defaults to 'CN=Grozet POS Development Test Certificate, O=Grozet Software Solutions, C=IN'.

.PARAMETER ValidYears
    Validity period in years. Defaults to 1 year.

.OUTPUTS
    System.Security.Cryptography.X509Certificates.X509Certificate2
#>
[CmdletBinding()]
param(
    [string]$Subject = "CN=Grozet POS Development Test Certificate, O=Grozet Software Solutions, C=IN",
    [int]$ValidYears = 1,
    [switch]$ForceNew
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  GROZET RetailPOS - Test Code-Signing Certificate Generator" -ForegroundColor Cyan
Write-Host "  [DEVELOPMENT AND TEST USE ONLY - NOT FOR PRODUCTION]" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Cyan

# Check if an existing test certificate already exists in Cert:\CurrentUser\My
$existingCerts = Get-ChildItem -Path "Cert:\CurrentUser\My" | Where-Object {
    $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date)
}

if ($existingCerts -and -not $ForceNew) {
    $cert = $existingCerts | Select-Object -First 1
    Write-Host "[OK] Using existing valid test certificate in Cert:\CurrentUser\My" -ForegroundColor Green
    Write-Host "     Subject:    $($cert.Subject)"
    Write-Host "     Thumbprint: $($cert.Thumbprint)"
    Write-Host "     Expires:    $($cert.NotAfter.ToString('yyyy-MM-dd HH:mm:ss'))"
    return $cert
}

Write-Host "[*] Creating new self-signed Code Signing certificate in Cert:\CurrentUser\My..." -ForegroundColor Yellow

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyLength 2048 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -KeyExportPolicy Exportable

Write-Host "[OK] Test certificate created successfully." -ForegroundColor Green
Write-Host "     Subject:    $($cert.Subject)"
Write-Host "     Thumbprint: $($cert.Thumbprint)"
Write-Host "     Expires:    $($cert.NotAfter.ToString('yyyy-MM-dd HH:mm:ss'))"

return $cert
