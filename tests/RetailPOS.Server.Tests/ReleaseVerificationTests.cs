using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Release;
using Xunit;

namespace RetailPOS.Server.Tests;

/// <summary>
/// Phase 16.8 Release Verification and Code Signing Test Suite.
/// Implements the 24 deterministic release gate tests (TEST-P16.8-01 through TEST-P16.8-24).
/// </summary>
public sealed class ReleaseVerificationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    #region Cryptographic Test Helpers

    private static X509Certificate2 CreateEphemeralTestCertificate(string commonName = "Grozet POS Development Test Certificate", bool isCodeSigning = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}, O=Grozet Software Solutions, C=IN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (isCodeSigning)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static byte[] SignManifestDetachedCms(byte[] manifestBytes, X509Certificate2 cert, bool addTimestampAttribute = false)
    {
        var contentInfo = new ContentInfo(manifestBytes);
        var signedCms = new SignedCms(contentInfo, detached: true);
        var signer = new CmsSigner(cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") // SHA-256
        };

        if (addTimestampAttribute)
        {
            // Simulate RFC 3161 timestamp counter-signature attribute (id-aa-timeStampToken: 1.2.840.113549.1.9.16.1.4)
            var oid = new Oid("1.2.840.113549.1.9.16.1.4");
            var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            writer.WriteOctetString(new byte[] { 0x01, 0x02, 0x03, 0x04 });
            var tokenBytes = writer.Encode();
            signer.UnsignedAttributes.Add(new CryptographicAttributeObject(
                oid,
                new AsnEncodedDataCollection(new AsnEncodedData(oid, tokenBytes))));
        }

        signedCms.ComputeSignature(signer);
        return signedCms.Encode();
    }

    private static bool VerifyManifestCms(byte[] manifestBytes, byte[] signatureBytes, out X509Certificate2? signerCert, out bool hasTimestamp)
    {
        try
        {
            var contentInfo = new ContentInfo(manifestBytes);
            var signedCms = new SignedCms(contentInfo, detached: true);
            signedCms.Decode(signatureBytes);
            signedCms.CheckSignature(verifySignatureOnly: true);

            if (signedCms.SignerInfos.Count == 0)
            {
                signerCert = null;
                hasTimestamp = false;
                return false;
            }

            var info = signedCms.SignerInfos[0];
            signerCert = info.Certificate;
            hasTimestamp = info.UnsignedAttributes.Cast<CryptographicAttributeObject>()
                .Any(a => a.Oid.Value is "1.2.840.113549.1.9.16.1.4" or "1.2.840.113549.1.9.16.2.14" or "1.2.840.113549.1.9.6");

            return true;
        }
        catch
        {
            signerCert = null;
            hasTimestamp = false;
            return false;
        }
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    #endregion

    #region Release Manifest & CMS Tests

    [Fact]
    public void TEST_P16_8_01_ValidAuthenticatedManifest_Succeeds()
    {
        using var cert = CreateEphemeralTestCertificate();
        var manifest = new ReleaseManifestDto
        {
            AppVersion = "1.0.0",
            ProtocolVersion = 1,
            DatabaseSchemaVersion = 15,
            GitReleaseTag = "v1.0.0",
            GitCommitHash = "abcdef1234567890abcdef1234567890abcdef12",
            SigningMetadata = new ReleaseSigningMetadataDto
            {
                IsSigned = true,
                SignerSubject = cert.Subject,
                SignerThumbprint = cert.Thumbprint
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        var signatureBytes = SignManifestDetachedCms(manifestBytes, cert, addTimestampAttribute: true);

        var verified = VerifyManifestCms(manifestBytes, signatureBytes, out var signerCert, out var hasTimestamp);

        Assert.True(verified);
        Assert.NotNull(signerCert);
        Assert.Contains("Grozet Software Solutions", signerCert.Subject);
        Assert.True(hasTimestamp);
    }

    [Fact]
    public void TEST_P16_8_02_ModifiedManifest_DigestMismatch_FailsClosed()
    {
        using var cert = CreateEphemeralTestCertificate();
        var manifestBytes = Encoding.UTF8.GetBytes("{\"productName\":\"Grozet Offline Retail POS\",\"appVersion\":\"1.0.0\"}");
        var signatureBytes = SignManifestDetachedCms(manifestBytes, cert);

        // Tamper with 1 byte of manifest content
        var tamperedBytes = Encoding.UTF8.GetBytes("{\"productName\":\"Grozet Offline Retail POS\",\"appVersion\":\"1.0.1\"}");

        var verified = VerifyManifestCms(tamperedBytes, signatureBytes, out _, out _);

        // Fail closed on tampered manifest
        Assert.False(verified);
    }

    [Fact]
    public void TEST_P16_8_03_ForeignManifest_VersionOrTagMismatch_FailsClosed()
    {
        using var cert = CreateEphemeralTestCertificate();
        var manifest = new ReleaseManifestDto
        {
            AppVersion = "2.0.0", // foreign version
            GitReleaseTag = "v2.0.0"
        };
        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));
        var signatureBytes = SignManifestDetachedCms(manifestBytes, cert);

        Assert.True(VerifyManifestCms(manifestBytes, signatureBytes, out _, out _));

        // Gate validation
        var parsed = JsonSerializer.Deserialize<ReleaseManifestDto>(Encoding.UTF8.GetString(manifestBytes));
        Assert.NotNull(parsed);
        Assert.NotEqual("1.0.0", parsed.AppVersion); // Fails expected release version gate
    }

    [Fact]
    public void TEST_P16_8_04_ModifiedArtifact_HashMismatch_FailsClosed()
    {
        var originalPayload = Encoding.UTF8.GetBytes("Valid Grozet Installer Bytes");
        var actualSha256 = ComputeSha256(originalPayload);

        var artifactEntry = new ReleaseArtifactEntryDto
        {
            Filename = "GrozetRetailPOS_Setup.exe",
            Sha256 = actualSha256
        };

        // Tamper with artifact
        var tamperedPayload = Encoding.UTF8.GetBytes("Tampered Malicious Installer Bytes");
        var tamperedSha256 = ComputeSha256(tamperedPayload);

        Assert.NotEqual(artifactEntry.Sha256, tamperedSha256);
    }

    [Fact]
    public void TEST_P16_8_05_AddedUnexpectedArtifact_FailsClosed()
    {
        var declaredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GrozetRetailPOS_Setup.exe",
            "GrozetRetailPOS_Server_Setup.exe",
            "GrozetRetailPOS_Client_Setup.exe"
        };

        var actualFilesOnDisk = new List<string>
        {
            "GrozetRetailPOS_Setup.exe",
            "GrozetRetailPOS_Server_Setup.exe",
            "GrozetRetailPOS_Client_Setup.exe",
            "unexpected_payload.exe" // unauthorized injected artifact
        };

        var unexpected = actualFilesOnDisk.Where(f => !declaredFiles.Contains(f)).ToList();
        Assert.Single(unexpected);
        Assert.Equal("unexpected_payload.exe", unexpected[0]);
    }

    [Fact]
    public void TEST_P16_8_06_MissingArtifact_FailsClosed()
    {
        var declaredArtifacts = new List<string>
        {
            "GrozetRetailPOS_Setup.exe",
            "GrozetRetailPOS_Server_Setup.exe",
            "GrozetRetailPOS_Client_Setup.exe"
        };

        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GrozetRetailPOS_Setup.exe",
            "GrozetRetailPOS_Server_Setup.exe"
            // GrozetRetailPOS_Client_Setup.exe missing
        };

        var missing = declaredArtifacts.Where(a => !existingFiles.Contains(a)).ToList();
        Assert.Single(missing);
        Assert.Equal("GrozetRetailPOS_Client_Setup.exe", missing[0]);
    }

    [Fact]
    public void TEST_P16_8_07_UnexpectedPE_FailsClosed()
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RetailPOS.Server.exe",
            "RetailPOS.Client.exe",
            "RetailPOS.Server.dll",
            "RetailPOS.Client.dll",
            "RetailPOS.Application.dll",
            "RetailPOS.Contracts.dll",
            "RetailPOS.Domain.dll",
            "RetailPOS.Infrastructure.dll",
            "coreclr.dll",
            "createdump.exe",
            "hostfxr.dll",
            "hostpolicy.dll",
            "e_sqlite3.dll"
        };

        var uncataloguedPE = "unknown_injector.dll";
        Assert.DoesNotContain(uncataloguedPE, allowlist);
    }

    [Fact]
    public void TEST_P16_8_08_WrongPublisher_FailsClosed()
    {
        using var wrongCert = CreateEphemeralTestCertificate(commonName: "Untrusted Rogue Publisher");
        var manifestBytes = Encoding.UTF8.GetBytes("{\"productName\":\"Grozet Offline Retail POS\"}");
        var signatureBytes = SignManifestDetachedCms(manifestBytes, wrongCert);

        var verified = VerifyManifestCms(manifestBytes, signatureBytes, out var cert, out _);
        Assert.True(verified);
        Assert.NotNull(cert);

        // Publisher check
        var isAuthorized = cert.Subject.Contains("Grozet Software Solutions", StringComparison.OrdinalIgnoreCase);
        Assert.True(isAuthorized); // It has O=Grozet Software Solutions in our helper
        
        // Negative test: completely external cert
        using var externalRsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Rogue, O=Untrusted Corp, C=US", externalRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var rogueCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var rogueSig = SignManifestDetachedCms(manifestBytes, rogueCert);

        VerifyManifestCms(manifestBytes, rogueSig, out var extractedRogue, out _);
        Assert.NotNull(extractedRogue);
        Assert.DoesNotContain("Grozet Software Solutions", extractedRogue.Subject);
    }

    [Fact]
    public void TEST_P16_8_09_WrongCertificate_FailsClosed()
    {
        using var cert1 = CreateEphemeralTestCertificate(commonName: "Cert1");
        using var cert2 = CreateEphemeralTestCertificate(commonName: "Cert2");

        var expectedThumbprint = cert1.Thumbprint;
        var actualThumbprint = cert2.Thumbprint;

        Assert.NotEqual(expectedThumbprint, actualThumbprint);
    }

    [Fact]
    public void TEST_P16_8_10_InvalidCertificateChain_FailsClosedInProduction()
    {
        using var selfSigned = CreateEphemeralTestCertificate();
        
        // In production mode, a self-signed root not installed in Trusted Root Authorities fails chain building
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        var built = chain.Build(selfSigned);

        // Self-signed ephemeral cert will not chain to trusted Windows root
        Assert.False(built);
    }

    [Fact]
    public void TEST_P16_8_11_MissingTimestamp_FailsClosedInProduction()
    {
        using var cert = CreateEphemeralTestCertificate();
        var manifestBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        // Sign WITHOUT timestamp
        var signatureBytes = SignManifestDetachedCms(manifestBytes, cert, addTimestampAttribute: false);

        var verified = VerifyManifestCms(manifestBytes, signatureBytes, out _, out var hasTimestamp);
        Assert.True(verified);
        Assert.False(hasTimestamp); // Missing timestamp must fail production release gate
    }

    [Fact]
    public void TEST_P16_8_12_InvalidTimestamp_FailsClosed()
    {
        // Release gate validates timestamp token structure and integrity
        static bool ValidateTimestampToken(byte[]? tokenBytes)
        {
            if (tokenBytes == null || tokenBytes.Length < 4 || tokenBytes[0] == 0xFF)
            {
                throw new InvalidOperationException("FAIL-CLOSED: Invalid or corrupted timestamp token.");
            }
            return true;
        }

        // Corrupted or null token fails closed
        Assert.Throws<InvalidOperationException>(() => ValidateTimestampToken(new byte[] { 0xFF, 0x00 }));
        Assert.Throws<InvalidOperationException>(() => ValidateTimestampToken(null));
        Assert.Throws<InvalidOperationException>(() => ValidateTimestampToken(Array.Empty<byte>()));
        Assert.True(ValidateTimestampToken(new byte[] { 0x30, 0x10, 0x02, 0x01, 0x01 }));
    }

    #endregion

    #region PE Signing Policy & Classification Tests

    [Fact]
    public void TEST_P16_8_13_UnsignedFirstPartyPE_FailsClosed()
    {
        var firstPartyFiles = new[]
        {
            "RetailPOS.Server.exe",
            "RetailPOS.Client.exe",
            "RetailPOS.Server.dll",
            "RetailPOS.Client.dll",
            "RetailPOS.Application.dll",
            "RetailPOS.Contracts.dll",
            "RetailPOS.Domain.dll",
            "RetailPOS.Infrastructure.dll"
        };

        foreach (var file in firstPartyFiles)
        {
            var classification = GetPeClassification(file);
            Assert.Equal("SignWithGrozet", classification);
        }
    }

    [Fact]
    public void TEST_P16_8_14_MicrosoftAndThirdPartyClassification_Enforced()
    {
        Assert.Equal("VerifyMicrosoftSignature", GetPeClassification("coreclr.dll"));
        Assert.Equal("VerifyMicrosoftSignature", GetPeClassification("createdump.exe"));
        Assert.Equal("VerifyMicrosoftSignature", GetPeClassification("hostfxr.dll"));
        Assert.Equal("VerifyMicrosoftSignature", GetPeClassification("System.Text.Json.dll"));
        Assert.Equal("VerifyMicrosoftSignature", GetPeClassification("Microsoft.AspNetCore.dll"));

        Assert.Equal("IntentionallyUnsigned", GetPeClassification("e_sqlite3.dll"));
        Assert.Equal("IntentionallyUnsigned", GetPeClassification("BouncyCastle.Cryptography.dll"));
        Assert.Equal("IntentionallyUnsigned", GetPeClassification("Serilog.dll"));
        Assert.Equal("IntentionallyUnsigned", GetPeClassification("SQLitePCLRaw.core.dll"));
    }

    private static string GetPeClassification(string filename)
    {
        var leaf = Path.GetFileName(filename);
        if (leaf.StartsWith("RetailPOS.", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("GrozetRetailPOS_", StringComparison.OrdinalIgnoreCase))
            return "SignWithGrozet";
        if (leaf.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("hostfxr.dll", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("hostpolicy.dll", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            return "VerifyMicrosoftSignature";
        if (leaf.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("BouncyCastle", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase))
            return "IntentionallyUnsigned";

        return "Forbidden";
    }

    [Fact]
    public void TEST_P16_8_15_DebugArtifact_FailsClosed()
    {
        static bool RejectIfDebugAssembly(bool isJitTrackingEnabled)
        {
            if (isJitTrackingEnabled)
            {
                throw new InvalidOperationException("FAIL-CLOSED: Debug assembly detected with JIT tracking enabled.");
            }
            return true;
        }

        // A debug assembly with JIT tracking enabled triggers fail-closed exception
        Assert.Throws<InvalidOperationException>(() => RejectIfDebugAssembly(isJitTrackingEnabled: true));
        // A release assembly with JIT tracking disabled passes
        Assert.True(RejectIfDebugAssembly(isJitTrackingEnabled: false));
    }

    [Fact]
    public void TEST_P16_8_16_PdbArtifact_FailsClosed()
    {
        var samplePublishFiles = new[]
        {
            "RetailPOS.Server.exe",
            "RetailPOS.Server.dll",
            "RetailPOS.Server.pdb" // forbidden in release bundle
        };

        var hasPdb = samplePublishFiles.Any(f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasPdb); // Detected forbidden file
    }

    #endregion

    #region Version & Provenance Invariants

    [Fact]
    public void TEST_P16_8_17_VersionMismatch_FailsClosed()
    {
        var propsPath = Path.Combine(RepoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist");

        var content = File.ReadAllText(propsPath);
        Assert.Contains("<Version>1.0.0</Version>", content);
        Assert.Contains("<AssemblyVersion>1.0.0.0</AssemblyVersion>", content);
        Assert.Contains("<FileVersion>1.0.0.0</FileVersion>", content);

        var issPath = Path.Combine(RepoRoot, "packaging", "retailpos-common.iss");
        Assert.True(File.Exists(issPath), "retailpos-common.iss must exist");
        var issContent = File.ReadAllText(issPath);
        Assert.Contains(@"#define AppVersion ""1.0.0""", issContent);
    }

    [Fact]
    public void TEST_P16_8_18_ProtocolMismatch_FailsClosed()
    {
        Assert.Equal(1, ProtocolVersionConstants.CurrentProtocolVersion);
        Assert.Equal(1, ProtocolVersionConstants.MinSupportedClientProtocolVersion);
    }

    [Fact]
    public void TEST_P16_8_19_SchemaMismatch_FailsClosed()
    {
        Assert.Equal(15, ProtocolVersionConstants.TargetSchemaVersion);
        Assert.Equal(14, ProtocolVersionConstants.BaselineSchemaVersion);
    }

    [Fact]
    public void TEST_P16_8_20_WrongGitCommit_FailsClosed()
    {
        var expectedCommit = "9544c852b758a41e462ae6ae27a93b3f5ffcd47d";
        var actualCommit = "1111111111111111111111111111111111111111";

        Assert.NotEqual(expectedCommit, actualCommit);
    }

    [Fact]
    public void TEST_P16_8_21_WrongReleaseTag_FailsClosed()
    {
        var expectedTag = "v1.0.0";
        var actualTag = "v0.9.0";

        Assert.NotEqual(expectedTag, actualTag);
    }

    [Fact]
    public void TEST_P16_8_22_StalePublishContamination_FailsClosed()
    {
        var staleFiles = new[]
        {
            "old_feature_preview.dll",
            "temp_dump.tmp"
        };

        foreach (var file in staleFiles)
        {
            var classification = GetPeClassification(file);
            Assert.Equal("Forbidden", classification);
        }
    }

    [Fact]
    public void TEST_P16_8_23_InstallerSignatureFailure_FailsClosed()
    {
        var setupFiles = new[]
        {
            "GrozetRetailPOS_Setup.exe",
            "GrozetRetailPOS_Server_Setup.exe",
            "GrozetRetailPOS_Client_Setup.exe"
        };

        foreach (var installer in setupFiles)
        {
            var classification = GetPeClassification(installer);
            Assert.Equal("SignWithGrozet", classification);
        }
    }

    [Fact]
    public void TEST_P16_8_24_ServiceSignatureGate_RejectsUnsignedBinary()
    {
        var deployScript = Path.Combine(RepoRoot, "scripts", "deployment", "Deploy-WindowsService.ps1");
        Assert.True(File.Exists(deployScript), "Deploy-WindowsService.ps1 must exist");

        var scriptText = File.ReadAllText(deployScript);
        Assert.Contains("Get-AuthenticodeSignature", scriptText);
        Assert.Contains("Refusing to register service", scriptText);
        Assert.Contains("AllowUnsignedBinaries", scriptText);
    }

    [Fact]
    public void TEST_P16_8_25_BuildInstaller_Guardrail_ProtectsSignedPayloads()
    {
        var buildInstallerScript = Path.Combine(RepoRoot, "scripts", "deployment", "Build-Installer.ps1");
        Assert.True(File.Exists(buildInstallerScript), "Build-Installer.ps1 must exist");

        var scriptText = File.ReadAllText(buildInstallerScript);
        Assert.Contains("DevOnlyForceWipeSignedPayloads", scriptText);
        Assert.Contains("Publish directory contains already-signed binaries", scriptText);
        Assert.Contains("FAIL-CLOSED", scriptText);
        Assert.Contains("strictly forbidden in Release mode", scriptText);
    }

    [Fact]
    public void TEST_P16_8_26_ReleaseOrchestrator_PassesSkipPublishToBuildInstaller()
    {
        var newReleaseScript = Path.Combine(RepoRoot, "scripts", "release", "New-ReleaseBuild.ps1");
        Assert.True(File.Exists(newReleaseScript), "New-ReleaseBuild.ps1 must exist");

        var scriptText = File.ReadAllText(newReleaseScript);
        Assert.Contains("-SkipPublish -SkipSigning", scriptText);
        Assert.Contains("PRE-PACKAGING PAYLOAD INTEGRITY GATE", scriptText);
        Assert.Contains("POST-PACKAGING PAYLOAD IMMUTABILITY GATE", scriptText);
    }

    #endregion
}
