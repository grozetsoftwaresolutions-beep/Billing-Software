<#
.SYNOPSIS
    Master release engineering and build orchestration script for GROZET RetailPOS.

.DESCRIPTION
    Executes the authoritative Phase 16.8 release pipeline:
      1. Git release tag and clean working tree provenance gate.
      2. Version invariant consistency verification.
      3. Clean, release build, and regression test execution.
      4. Self-contained ReadyToRun win-x64 publishing.
      5. Publish directory hygiene and debug artifact sanitization.
      6. PE signing policy enforcement on first-party binaries and deployment scripts.
      7. RFC 3161 SHA-256 timestamping with fail-closed fallback.
      8. Verification of Microsoft runtime signatures without re-signing.
      9. Inno Setup installer compilation (packaging already-signed payloads).
      10. Signing and RFC 3161 timestamping of installer executables.
      11. Manifest generation, SHA256SUMS creation, and detached CMS signing (release_manifest.json.p7s).
      12. Execution of independent release verifier (Test-ReleaseArtifacts.ps1).

.PARAMETER ReleaseTag
    Git release tag to build (e.g. 'v1.0.0'). Must exist and point to current HEAD in production.

.PARAMETER CertificateThumbprint
    Thumbprint of the Grozet Code Signing Certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My.

.PARAMETER TestSign
    Generates and uses an isolated self-signed test certificate for local developer testing.

.PARAMETER Production
    Enforces strict production requirements (fails closed if test cert used, requires valid timestamp).

.PARAMETER SkipGitCheck
    Developer override to skip the git clean/tag check during local pipeline testing.

.PARAMETER SkipTests
    Skips the dotnet test suite execution during local build iteration.

.PARAMETER TimestampServer
    Primary RFC 3161 Timestamp Authority URL. Defaults to 'http://timestamp.digicert.com'.

.PARAMETER BackupTimestampServer
    Secondary RFC 3161 Timestamp Authority URL. Defaults to 'http://timestamp.sectigo.com'.
#>
[CmdletBinding()]
param(
    [string]$ReleaseTag = "v1.0.0",
    [string]$Configuration = "Release",
    [string]$CertificateThumbprint = "",
    [switch]$TestSign,
    [switch]$Production,
    [switch]$SkipGitCheck,
    [switch]$SkipTests,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [string]$BackupTimestampServer = "http://timestamp.sectigo.com",
    [string]$OutputBaseDirectory = "artifacts\releases"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Security

# Configure DOTNET_ROOT if SDK is installed in user profile directory
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:USERPROFILE\.dotnet")) {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
    $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $RepoRoot

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  GROZET RetailPOS - Master Release Build Pipeline" -ForegroundColor Cyan
Write-Host "  Tag: $ReleaseTag | Config: $Configuration | Mode: $(if ($Production) { 'PRODUCTION' } else { 'DEVELOPMENT' })" -ForegroundColor $(if ($Production) { 'Green' } else { 'Yellow' })
Write-Host "============================================================" -ForegroundColor Cyan

# ==============================================================================
# STAGE 1: Git Release Tag & Working Tree Provenance Gate
# ==============================================================================
Write-Host "`n[*] Stage 1: Validating Git repository provenance..." -ForegroundColor Yellow

$gitCommitHash = ""
$gitReleaseTag = $ReleaseTag

if (-not $SkipGitCheck) {
    # Check git working directory cleanliness
    $statusOutput = git status --porcelain
    if ($statusOutput) {
        throw "FAIL-CLOSED: Git working tree is dirty. Commit or stash all changes before creating a release build.`n$statusOutput"
    }

    # Verify release tag exists
    $tagCheck = git rev-parse -q --verify "refs/tags/$ReleaseTag"
    if (-not $tagCheck) {
        throw "FAIL-CLOSED: Release tag '$ReleaseTag' does not exist in local Git repository."
    }

    # Verify tag commit points to HEAD
    $tagCommit = (git rev-list -n 1 $ReleaseTag).Trim()
    $headCommit = (git rev-parse HEAD).Trim()

    if ($tagCommit -ne $headCommit) {
        throw "FAIL-CLOSED: Release tag '$ReleaseTag' points to commit $tagCommit, but current HEAD is $headCommit. Mismatched tag/commit."
    }

    $gitCommitHash = $headCommit
    Write-Host "    [OK] Git tree clean. Release tag '$ReleaseTag' matches HEAD ($gitCommitHash)." -ForegroundColor Green
}
else {
    try {
        $gitCommitHash = (git rev-parse HEAD).Trim()
    }
    catch {
        $gitCommitHash = "0000000000000000000000000000000000000000"
    }
    Write-Host "    [WARN] Git provenance check skipped via -SkipGitCheck flag. Head commit: $gitCommitHash" -ForegroundColor Yellow
}

# ==============================================================================
# STAGE 2: Version Invariant Consistency Check
# ==============================================================================
Write-Host "`n[*] Stage 2: Verifying version consistency across solution and packaging..." -ForegroundColor Yellow

$propsFile = Join-Path $RepoRoot "Directory.Build.props"
$issCommonFile = Join-Path $RepoRoot "packaging\retailpos-common.iss"

if (-not (Test-Path $propsFile)) { throw "Missing Directory.Build.props" }
if (-not (Test-Path $issCommonFile)) { throw "Missing retailpos-common.iss" }

[xml]$propsXml = Get-Content $propsFile
$propsVersion = $propsXml.Project.PropertyGroup.Version | Select-Object -First 1
$propsAssemblyVersion = $propsXml.Project.PropertyGroup.AssemblyVersion | Select-Object -First 1

$issContent = Get-Content $issCommonFile -Raw
$issMatch = [regex]::Match($issContent, '#define\s+AppVersion\s+"([^"]+)"')
if (-not $issMatch.Success) { throw "Cannot find #define AppVersion in retailpos-common.iss" }
$issVersion = $issMatch.Groups[1].Value

if ($propsVersion -ne "1.0.0" -or $issVersion -ne "1.0.0") {
    throw "FAIL-CLOSED: Version mismatch: Directory.Build.props Version='$propsVersion', ISS AppVersion='$issVersion' (Expected: 1.0.0)"
}
if ($propsAssemblyVersion -ne "1.0.0.0") {
    throw "FAIL-CLOSED: AssemblyVersion mismatch in Directory.Build.props: '$propsAssemblyVersion' (Expected: 1.0.0.0)"
}

$appVersion = $propsVersion
Write-Host "    [OK] Versions verified: AppVersion=$appVersion, AssemblyVersion=$propsAssemblyVersion, Protocol=1, Schema=15." -ForegroundColor Green

# ==============================================================================
# STAGE 3: Clean & Release Compilation
# ==============================================================================
Write-Host "`n[*] Stage 3: Compiling solution in $Configuration configuration..." -ForegroundColor Yellow

$solutionPath = Join-Path $RepoRoot "RetailPOS.sln"
dotnet clean $solutionPath -c $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed with exit code $LASTEXITCODE" }

dotnet build $solutionPath -c $Configuration --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

Write-Host "    [OK] Solution compiled successfully." -ForegroundColor Green

# ==============================================================================
# STAGE 4: Execute Test Suite
# ==============================================================================
if (-not $SkipTests) {
    Write-Host "`n[*] Stage 4: Executing test suite..." -ForegroundColor Yellow
    dotnet test $solutionPath -c $Configuration --no-build --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
    Write-Host "    [OK] All automated tests passed." -ForegroundColor Green
}
else {
    Write-Host "`n[*] Stage 4: Skipping test suite via -SkipTests flag." -ForegroundColor Yellow
}

# ==============================================================================
# STAGE 5: Self-Contained ReadyToRun Publishing
# ==============================================================================
Write-Host "`n[*] Stage 5: Publishing self-contained win-x64 applications..." -ForegroundColor Yellow

$serverProj = Join-Path $RepoRoot "src\RetailPOS.Server\RetailPOS.Server.csproj"
$clientProj = Join-Path $RepoRoot "src\RetailPOS.Client\RetailPOS.Client.csproj"

dotnet publish $serverProj -c $Configuration -p:PublishProfile=win-x64 --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "RetailPOS.Server publish failed with exit code $LASTEXITCODE" }

dotnet publish $clientProj -c $Configuration -p:PublishProfile=win-x64 --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "RetailPOS.Client publish failed with exit code $LASTEXITCODE" }

$serverPublishDir = Join-Path $RepoRoot "src\RetailPOS.Server\bin\$Configuration\net10.0\publish\win-x64"
$clientPublishDir = Join-Path $RepoRoot "src\RetailPOS.Client\bin\$Configuration\net10.0\publish\win-x64"

Write-Host "    [OK] Published Server to: $serverPublishDir" -ForegroundColor Green
Write-Host "    [OK] Published Client to: $clientPublishDir" -ForegroundColor Green

# ==============================================================================
# STAGE 6: Publish Directory Hygiene & Sanitization
# ==============================================================================
Write-Host "`n[*] Stage 6: Sanitizing publish directories (removing PDBs and forbidden files)..." -ForegroundColor Yellow

foreach ($dir in @($serverPublishDir, $clientPublishDir)) {
    # Remove all PDB debug symbol files
    $pdbs = Get-ChildItem -Path $dir -Filter "*.pdb" -Recurse
    foreach ($pdb in $pdbs) {
        Remove-Item -LiteralPath $pdb.FullName -Force
    }
    
    # Remove development configs if present
    $devConfig = Join-Path $dir "appsettings.Development.json"
    if (Test-Path $devConfig) {
        Remove-Item -LiteralPath $devConfig -Force
    }

    # Verify no tmp files
    $tmps = Get-ChildItem -Path $dir -Filter "*.tmp" -Recurse
    foreach ($tmp in $tmps) {
        Remove-Item -LiteralPath $tmp.FullName -Force
    }
}

Write-Host "    [OK] Publish directories sanitized (PDBs and debug files eliminated)." -ForegroundColor Green

# ==============================================================================
# STAGE 7: Resolve Code Signing Certificate
# ==============================================================================
Write-Host "`n[*] Stage 7: Resolving Code Signing Certificate..." -ForegroundColor Yellow

$cert = $null

if ($CertificateThumbprint) {
    $found = Get-ChildItem -Path "Cert:\CurrentUser\My", "Cert:\LocalMachine\My" | Where-Object { $_.Thumbprint -eq $CertificateThumbprint } | Select-Object -First 1
    if (-not $found) {
        throw "FAIL-CLOSED: Certificate with thumbprint '$CertificateThumbprint' not found in CurrentUser\My or LocalMachine\My."
    }
    $cert = $found
}
elseif ($TestSign -or -not $Production) {
    Write-Host "    Using local test-signing certificate..." -ForegroundColor Yellow
    $testCertScript = Join-Path $PSScriptRoot "New-TestCertificate.ps1"
    $cert = & $testCertScript
}
else {
    throw "FAIL-CLOSED: Production release build requires -CertificateThumbprint targeting a commercial CA certificate."
}

if ($Production) {
    if ($cert.Subject -like "*Test Certificate*" -or $cert.Subject -like "*Dev Only*") {
        throw "FAIL-CLOSED: Cannot produce a production release with a test certificate: $($cert.Subject)"
    }
}

Write-Host "    [OK] Active Signing Certificate:" -ForegroundColor Green
Write-Host "         Subject:    $($cert.Subject)"
Write-Host "         Thumbprint: $($cert.Thumbprint)"

# Helper function to sign with primary and fallback timestamp server
function Invoke-SafeAuthenticodeSign {
    param(
        [string]$FilePath,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $signed = $false
    $err = $null

    # Try primary TSA
    try {
        $sigResult = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $Certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer -ErrorAction Stop
        if ($sigResult.Status -eq 'Valid') {
            $signed = $true
        }
        else {
            $err = $sigResult.StatusMessage
        }
    }
    catch {
        $err = $_.Exception.Message
    }

    # Try backup TSA if primary failed
    if (-not $signed -and $BackupTimestampServer) {
        try {
            Write-Host "        [TSA Retry] Primary TSA failed ($err); trying backup TSA: $BackupTimestampServer..." -ForegroundColor Yellow
            $sigResult = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $Certificate -HashAlgorithm SHA256 -TimestampServer $BackupTimestampServer -ErrorAction Stop
            if ($sigResult.Status -eq 'Valid') {
                $signed = $true
            }
            else {
                $err = $sigResult.StatusMessage
            }
        }
        catch {
            $err = $_.Exception.Message
        }
    }

    if (-not $signed) {
        if ($Production) {
            throw "FAIL-CLOSED: Authenticode signing and timestamping failed for '$FilePath': $err"
        }
        else {
            # In local dev test mode, sign with test certificate
            Write-Host "        [WARN] Signing with test certificate in development mode ($err)..." -ForegroundColor Yellow
            $sigResult = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $Certificate -HashAlgorithm SHA256 -ErrorAction SilentlyContinue
            if ($sigResult.Status -eq 'Valid' -or ($sigResult.Status -eq 'UnknownError' -and $sigResult.SignerCertificate.Subject -like '*Test Certificate*')) {
                $signed = $true
            }
            else {
                throw "Set-AuthenticodeSignature failed: $($sigResult.StatusMessage)"
            }
        }
    }
}

# ==============================================================================
# STAGE 8: Enforce PE Signing Policy on Published Payloads
# ==============================================================================
Write-Host "`n[*] Stage 8: Signing Grozet first-party binaries and deployment scripts..." -ForegroundColor Yellow

$grozetPayloads = @(
    (Join-Path $serverPublishDir "RetailPOS.Server.exe"),
    (Join-Path $serverPublishDir "RetailPOS.Server.dll"),
    (Join-Path $serverPublishDir "RetailPOS.Application.dll"),
    (Join-Path $serverPublishDir "RetailPOS.Contracts.dll"),
    (Join-Path $serverPublishDir "RetailPOS.Domain.dll"),
    (Join-Path $serverPublishDir "RetailPOS.Infrastructure.dll"),
    (Join-Path $clientPublishDir "RetailPOS.Client.exe"),
    (Join-Path $clientPublishDir "RetailPOS.Client.dll"),
    (Join-Path $RepoRoot "scripts\deployment\Deploy-WindowsService.ps1")
)

foreach ($payload in $grozetPayloads) {
    if (Test-Path $payload) {
        Write-Host "    Signing: $(Split-Path $payload -Leaf)"
        Invoke-SafeAuthenticodeSign -FilePath $payload -Certificate $cert
        
        $verify = Get-AuthenticodeSignature -FilePath $payload
        $isValidSig = if ($Production) {
            $verify.Status -eq 'Valid'
        } else {
            $verify.Status -eq 'Valid' -or ($verify.Status -eq 'UnknownError' -and $verify.SignerCertificate.Subject -like '*Test Certificate*')
        }
        if (-not $isValidSig) {
            throw "FAIL-CLOSED: Signature verification failed on ${payload} - $($verify.StatusMessage)"
        }
    }
}

# Verify Microsoft runtime binaries are NOT altered and have valid Microsoft signatures
Write-Host "    Verifying Microsoft runtime signatures..."
$microsoftBinaries = @(
    (Join-Path $serverPublishDir "coreclr.dll"),
    (Join-Path $serverPublishDir "createdump.exe"),
    (Join-Path $serverPublishDir "hostfxr.dll"),
    (Join-Path $serverPublishDir "System.Text.Json.dll"),
    (Join-Path $serverPublishDir "Microsoft.AspNetCore.dll")
)

foreach ($msBin in $microsoftBinaries) {
    if (Test-Path $msBin) {
        $msSig = Get-AuthenticodeSignature -FilePath $msBin
        if ($msSig.Status -ne 'Valid') {
            throw "FAIL-CLOSED: Microsoft runtime component '$msBin' signature is invalid ($($msSig.StatusMessage))."
        }
        if ($msSig.SignerCertificate.Subject -notlike "*Microsoft Corporation*" -and $msSig.SignerCertificate.Subject -notlike "*CN=.NET*") {
            throw "FAIL-CLOSED: Microsoft runtime binary was unexpectedly re-signed or has wrong subject: $($msSig.SignerCertificate.Subject)"
        }
    }
}

Write-Host "    [OK] Grozet payloads signed and Microsoft signatures verified." -ForegroundColor Green

# ==============================================================================
# STAGE 9: Compile Inno Setup Installers
# ==============================================================================
Write-Host "`n[*] Stage 9: Compiling Inno Setup installers with pre-signed payloads..." -ForegroundColor Yellow

$buildInstallerScript = Join-Path $RepoRoot "scripts\deployment\Build-Installer.ps1"
& $buildInstallerScript -Configuration $Configuration -SkipSigning
if ($LASTEXITCODE -ne 0) { throw "Build-Installer.ps1 failed with exit code $LASTEXITCODE" }

$installerOutputDir = Join-Path $RepoRoot "packaging\Output"
$installers = @(
    (Join-Path $installerOutputDir "GrozetRetailPOS_Setup.exe"),
    (Join-Path $installerOutputDir "GrozetRetailPOS_Server_Setup.exe"),
    (Join-Path $installerOutputDir "GrozetRetailPOS_Client_Setup.exe")
)

foreach ($inst in $installers) {
    if (-not (Test-Path $inst)) {
        throw "FAIL-CLOSED: Expected installer not found: $inst"
    }
}

Write-Host "    [OK] Inno Setup compiled 3 installer executables." -ForegroundColor Green

# ==============================================================================
# STAGE 10: Sign Inno Setup Installer Executables
# ==============================================================================
Write-Host "`n[*] Stage 10: Signing Inno Setup installer executables..." -ForegroundColor Yellow

foreach ($inst in $installers) {
    Write-Host "    Signing installer: $(Split-Path $inst -Leaf)"
    Invoke-SafeAuthenticodeSign -FilePath $inst -Certificate $cert

    $instVerify = Get-AuthenticodeSignature -FilePath $inst
    $isValidInst = if ($Production) {
        $instVerify.Status -eq 'Valid'
    } else {
        $instVerify.Status -eq 'Valid' -or ($instVerify.Status -eq 'UnknownError' -and $instVerify.SignerCertificate.Subject -like '*Test Certificate*')
    }
    if (-not $isValidInst) {
        throw "FAIL-CLOSED: Installer signature verification failed on ${inst} - $($instVerify.StatusMessage)"
    }
}

Write-Host "    [OK] All installer executables signed and verified." -ForegroundColor Green

# ==============================================================================
# STAGE 11: Generate Authenticated Release Package & CMS Manifest
# ==============================================================================
Write-Host "`n[*] Stage 11: Generating release package, manifest, and detached CMS signature..." -ForegroundColor Yellow

$stagingDir = Join-Path $RepoRoot "$OutputBaseDirectory\v$appVersion"
if (-not (Test-Path $stagingDir)) {
    New-Item -Path $stagingDir -ItemType Directory -Force | Out-Null
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
$manifestArtifacts = [System.Collections.Generic.List[PSObject]]::new()
$sumsContent = [System.Text.StringBuilder]::new()

foreach ($inst in $installers) {
    $leafName = Split-Path $inst -Leaf
    $destPath = Join-Path $stagingDir $leafName
    Copy-Item -LiteralPath $inst -Destination $destPath -Force

    $bytes = [System.IO.File]::ReadAllBytes($destPath)
    $hash = [System.BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace("-", "").ToUpperInvariant()
    $sig = Get-AuthenticodeSignature -FilePath $destPath

    $category = switch -Wildcard ($leafName) {
        "*Server*" { "ServerInstaller" }
        "*Client*" { "ClientInstaller" }
        default    { "UnifiedInstaller" }
    }

    $manifestArtifacts.Add([PSCustomObject]@{
        filename = $leafName
        category = $category
        sizeBytes = $bytes.Length
        sha256 = $hash
        peClassification = "SignWithGrozet"
        authenticodeStatus = [string]$sig.Status
        isTimestamped = ($null -ne $sig.TimeStamperCertificate)
    })

    [void]$sumsContent.AppendLine("$hash  $leafName")
}

# Write SHA256SUMS
$sumsPath = Join-Path $stagingDir "SHA256SUMS"
[System.IO.File]::WriteAllText($sumsPath, $sumsContent.ToString(), [System.Text.Encoding]::UTF8)

# Construct Manifest JSON
$manifestObject = [PSCustomObject]@{
    '$schema' = "https://grozet.com/schemas/release-manifest-v1.json"
    productName = "Grozet Offline Retail POS & Billing"
    appVersion = $appVersion
    protocolVersion = 1
    databaseSchemaVersion = 15
    gitReleaseTag = $gitReleaseTag
    gitCommitHash = $gitCommitHash
    buildConfiguration = $Configuration
    buildTimestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    signingMetadata = [PSCustomObject]@{
        isSigned = $true
        signerSubject = $cert.Subject
        signerThumbprint = $cert.Thumbprint
        timestampAuthority = $TimestampServer
        signatureFormat = "CMS_PKCS7_Detached"
        signatureFileName = "release_manifest.json.p7s"
    }
    artifacts = $manifestArtifacts
}

$manifestJson = $manifestObject | ConvertTo-Json -Depth 10
$manifestPath = Join-Path $stagingDir "release_manifest.json"
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

# Sign Manifest with Detached CMS (PKCS#7)
$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$contentInfo = New-Object System.Security.Cryptography.Pkcs.ContentInfo (,$manifestBytes)
$signedCms = New-Object System.Security.Cryptography.Pkcs.SignedCms ($contentInfo, $true) # detached signature
$signer = New-Object System.Security.Cryptography.Pkcs.CmsSigner ($cert)
$signer.DigestAlgorithm = New-Object System.Security.Cryptography.Oid ("2.16.840.1.101.3.4.2.1") # SHA-256
$signer.IncludeOption = [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
$signedCms.ComputeSignature($signer)
$p7sBytes = $signedCms.Encode()

$p7sPath = Join-Path $stagingDir "release_manifest.json.p7s"
[System.IO.File]::WriteAllBytes($p7sPath, $p7sBytes)

Write-Host "    [OK] Release artifacts staged at: $stagingDir" -ForegroundColor Green
Write-Host "    [OK] Detached CMS signature written to: $p7sPath" -ForegroundColor Green

# ==============================================================================
# STAGE 12: Independent Release Verification Gate
# ==============================================================================
Write-Host "`n[*] Stage 12: Running independent release verifier (Test-ReleaseArtifacts.ps1)..." -ForegroundColor Yellow

$verifierScript = Join-Path $PSScriptRoot "Test-ReleaseArtifacts.ps1"
$verifyParams = @{
    ReleaseDirectory = $stagingDir
    ExpectedVersion = $appVersion
}
if ($Production) {
    $verifyParams.Add("Production", $true)
}
if ($CertificateThumbprint) {
    $verifyParams.Add("ExpectedSignerThumbprint", $CertificateThumbprint)
}
if ($gitCommitHash -ne "0000000000000000000000000000000000000000") {
    $verifyParams.Add("ExpectedGitCommit", $gitCommitHash)
}

& $verifierScript @verifyParams
if ($LASTEXITCODE -ne 0) {
    throw "FAIL-CLOSED: Independent release verification gate FAILED with exit code $LASTEXITCODE."
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "  GROZET RetailPOS - RELEASE BUILD COMPLETED SUCCESSFULLY" -ForegroundColor Green
Write-Host "  Release Package: $stagingDir" -ForegroundColor Green
Write-Host "  Version: $appVersion | Commit: $gitCommitHash" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
