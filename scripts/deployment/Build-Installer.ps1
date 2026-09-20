<#
.SYNOPSIS
    Build-Installer.ps1 - Compiles the three production Inno Setup installers for Grozet Retail POS.
.DESCRIPTION
    Automates clean publishing of Server (win-x64) and Client (win-x64) using established Phase 16.1
    publish profiles, discovers the Inno Setup 6 compiler (iscc.exe), and compiles:
    1. packaging\retailpos-server.iss -> GrozetRetailPOS_Server_Setup.exe
    2. packaging\retailpos-client.iss -> GrozetRetailPOS_Client_Setup.exe
    3. packaging\retailpos.iss        -> GrozetRetailPOS_Setup.exe
    Complies with Sub-Phase 16.4 Production Packaging specifications.
.PARAMETER Configuration
    Build configuration: Release or Debug (default: Release).
.PARAMETER SkipPublish
    Skip the dotnet publish step if artifacts are already current.
.PARAMETER CompilerPath
    Explicit path to ISCC.exe compiler if located in non-standard location.
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish = $false,

    [string]$CompilerPath = '',

    [switch]$SkipSigning = $false,

    [string]$CertificateThumbprint = '',

    [switch]$TestSign = $false,

    [switch]$DevOnlyForceWipeSignedPayloads = $false
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

# Configure DOTNET_ROOT if SDK is installed in user profile directory
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:USERPROFILE\.dotnet")) {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
    $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
}

Write-Host "=== Grozet Retail POS - Build Production Installers ===" -ForegroundColor Cyan
Write-Host "Repository Root: $repoRoot"

# 1. Discover Inno Setup Compiler (ISCC.exe)
function Find-InnoSetupCompiler {
    if (-not [string]::IsNullOrWhiteSpace($CompilerPath) -and (Test-Path $CompilerPath)) {
        return @{ Path = (Resolve-Path $CompilerPath).Path; IsDevFallback = $false }
    }

    # Check environment PATH
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return @{ Path = $cmd.Source; IsDevFallback = $false }
    }

    # Check standard machine install locations
    $standardCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($c in $standardCandidates) {
        if (Test-Path $c) {
            return @{ Path = (Resolve-Path $c).Path; IsDevFallback = $false }
        }
    }

    # Development/Testing fallback ONLY (bundled with Antigravity IDE)
    $devFallback = "${env:LOCALAPPDATA}\Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe"
    if (Test-Path $devFallback) {
        return @{ Path = (Resolve-Path $devFallback).Path; IsDevFallback = $true }
    }

    return $null
}

$compilerInfo = Find-InnoSetupCompiler
if ($null -eq $compilerInfo) {
    Write-Warning "Inno Setup 6 compiler (ISCC.exe) was not found on this machine."
    Write-Warning "Please install Inno Setup 6 or supply -CompilerPath."
    $iscc = $null
} else {
    $iscc = $compilerInfo.Path
    if ($compilerInfo.IsDevFallback) {
        Write-Host "[DEV-FALLBACK] Using Antigravity IDE bundled Inno Setup 6 compiler for local test compilation at: $iscc" -ForegroundColor Magenta
    } else {
        Write-Host "Found Inno Setup Compiler: $iscc" -ForegroundColor Green
    }
}

# 2. Publish Server and Client using established 16.1 profiles
$serverPublishDir = Join-Path $repoRoot "src\RetailPOS.Server\bin\$Configuration\net10.0\publish\win-x64"
$clientPublishDir = Join-Path $repoRoot "src\RetailPOS.Client\bin\$Configuration\net10.0\publish\win-x64"

$serverExe = Join-Path $serverPublishDir "RetailPOS.Server.exe"
$clientExe = Join-Path $clientPublishDir "RetailPOS.Client.exe"

$needsPublish = (-not $SkipPublish) -or (-not (Test-Path $serverExe)) -or (-not (Test-Path $clientExe))

if ($needsPublish) {
    # Safety guardrail: Prevent accidental wiping of already-signed payloads
    $executablesToCheck = @($serverExe, $clientExe)
    foreach ($exeToCheck in $executablesToCheck) {
        if (Test-Path $exeToCheck) {
            $existingSig = Get-AuthenticodeSignature -FilePath $exeToCheck -ErrorAction SilentlyContinue
            if ($existingSig -and $existingSig.Status -ne 'NotSigned') {
                if ($DevOnlyForceWipeSignedPayloads) {
                    if ($Configuration -eq 'Release') {
                        throw "FAIL-CLOSED: -DevOnlyForceWipeSignedPayloads is strictly forbidden in Release mode! Refusing to destroy signed payloads."
                    }
                    Write-Warning "DEVELOPER OVERRIDE: Wiping signed binaries in non-release mode ($Configuration)."
                } else {
                    throw "FAIL-CLOSED: Publish directory contains already-signed binaries ($($existingSig.SignerCertificate.Subject) on $(Split-Path $exeToCheck -Leaf)). Build-Installer.ps1 refused to wipe and republish over signed payloads. Use -SkipPublish to package existing signed binaries."
                }
            }
        }
    }

    # Safe cleanup of previous publish artifacts
    if (Test-Path $serverPublishDir) {
        Write-Host "Cleaning server publish folder: $serverPublishDir"
        Remove-Item -Path "$serverPublishDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $clientPublishDir) {
        Write-Host "Cleaning client publish folder: $clientPublishDir"
        Remove-Item -Path "$clientPublishDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Publishing Server (win-x64, $Configuration)..." -ForegroundColor Yellow
    $serverCsproj = Join-Path $repoRoot "src\RetailPOS.Server\RetailPOS.Server.csproj"
    dotnet publish $serverCsproj -c $Configuration /p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish RetailPOS.Server"
    }

    Write-Host "Publishing Client (win-x64, $Configuration)..." -ForegroundColor Yellow
    $clientCsproj = Join-Path $repoRoot "src\RetailPOS.Client\RetailPOS.Client.csproj"
    dotnet publish $clientCsproj -c $Configuration /p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish RetailPOS.Client"
    }
} else {
    Write-Host "Reusing existing publish artifacts at:"
    Write-Host "  Server: $serverPublishDir"
    Write-Host "  Client: $clientPublishDir"
}

# 3. Ensure Output Directory exists and is cleaned of stale installers
$packagingDir = Join-Path $repoRoot "packaging"
$outputDir = Join-Path $packagingDir "Output"
if (-not (Test-Path $outputDir)) {
    New-Item -Path $outputDir -ItemType Directory -Force | Out-Null
}

$scriptsToBuild = @(
    @{ Script = "retailpos-server.iss"; Output = "GrozetRetailPOS_Server_Setup.exe" },
    @{ Script = "retailpos-client.iss"; Output = "GrozetRetailPOS_Client_Setup.exe" },
    @{ Script = "retailpos.iss";        Output = "GrozetRetailPOS_Setup.exe" }
)

# 4. Compile all three installers if ISCC is available
$results = @()

if ($null -ne $iscc) {
    foreach ($item in $scriptsToBuild) {
        $issPath = Join-Path $packagingDir $item.Script
        $targetExe = Join-Path $outputDir $item.Output

        if (Test-Path $targetExe) {
            Remove-Item $targetExe -Force -ErrorAction SilentlyContinue
        }

        Write-Host "Compiling $($item.Script) -> $($item.Output) ..." -ForegroundColor Cyan
        & $iscc /Qp "/O$outputDir" $issPath
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compilation failed for $($item.Script) with exit code: $LASTEXITCODE"
        }

        if (Test-Path $targetExe) {
            $fileItem = Get-Item $targetExe
            $hash = (Get-FileHash $targetExe -Algorithm SHA256).Hash
            Write-Host "  [OK] $($item.Output) ($([math]::Round($fileItem.Length / 1MB, 2)) MB, SHA256: $hash)" -ForegroundColor Green
            $results += [PSCustomObject]@{
                Script = $item.Script
                Output = $item.Output
                Success = $true
                Size = $fileItem.Length
                SHA256 = $hash
                Path = $fileItem.FullName
            }
        } else {
            throw "Compilation finished but expected output was not found: $targetExe"
        }
    }

    Write-Host "=== All Three Installers Compiled Successfully ===" -ForegroundColor Green
    return $results
} else {
    Write-Host "Compilation skipped: Inno Setup compiler not available." -ForegroundColor Yellow
    return [PSCustomObject]@{
        Success = $false
        Reason = "Inno Setup compiler not available"
    }
}
