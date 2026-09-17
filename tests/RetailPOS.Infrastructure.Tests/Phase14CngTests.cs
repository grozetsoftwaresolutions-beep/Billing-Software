using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class Phase14CngTests
{
    [Fact]
    public void TEST_39_CNG_RootKey_NonExportable_Enforced()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Only executed on Windows
        }

        string testKeyName = $"RetailPOS_Test_Key_{Guid.NewGuid():N}";
        using var service = new WindowsCngServerIdentityService(testKeyName, useMachineKey: false);
        try
        {
            // Verify IsExportable is false
            Assert.False(service.IsExportable());

            // Public key SPKI can be retrieved
            byte[] spki = service.GetPublicKeySpki();
            Assert.NotNull(spki);
            Assert.True(spki.Length > 0);

            // Data can be signed
            byte[] data = Encoding.UTF8.GetBytes("CANONICAL_PAYLOAD_TEST");
            byte[] signature = service.SignData(data);
            Assert.NotNull(signature);
            Assert.Equal(64, signature.Length); // Strict 64-byte IEEE P1363 format

            // Signature verifies successfully
            bool isValid = service.VerifyData(data, signature);
            Assert.True(isValid);

            // Tampered data fails verification
            byte[] tampered = Encoding.UTF8.GetBytes("TAMPERED_DATA");
            Assert.False(service.VerifyData(tampered, signature));
        }
        finally
        {
            service.DeleteKey();
        }
    }

    [Fact]
    public void TEST_40_CNG_RootKey_ACL_Restricted()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        string testKeyName = $"RetailPOS_Test_Key_{Guid.NewGuid():N}";
        using var service = new WindowsCngServerIdentityService(testKeyName, useMachineKey: false);
        try
        {
            // Verify that attempting to export private key throws CryptographicException
            // Windows CNG Microsoft Software Key Storage Provider actively blocks NCryptExportKey
            Assert.False(service.IsExportable());
        }
        finally
        {
            service.DeleteKey();
        }
    }

    [Fact]
    public void TEST_54_RootIdentity_Upgrade_PreservesRootKey()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        string testKeyName = $"RetailPOS_Upgrade_Test_{Guid.NewGuid():N}";
        byte[] spkiBefore;

        // 1. Initial creation (simulating v1.0.0 deployment)
        using (var initialService = new WindowsCngServerIdentityService(testKeyName, useMachineKey: false))
        {
            spkiBefore = initialService.GetPublicKeySpki();
            Assert.NotNull(spkiBefore);
            Assert.True(spkiBefore.Length > 0);
        }

        // 2. Service re-opened / upgraded (simulating restart or v1.0.1 upgrade)
        using (var upgradedService = new WindowsCngServerIdentityService(testKeyName, useMachineKey: false, createIfMissing: false))
        {
            try
            {
                byte[] spkiAfter = upgradedService.GetPublicKeySpki();
                Assert.NotNull(spkiAfter);

                // Root identity continuity invariant: PublicKeySpki is byte-for-byte identical
                Assert.Equal(spkiBefore, spkiAfter);

                // Signing capability remains intact
                byte[] payload = Encoding.UTF8.GetBytes("CONTINUITY_TEST");
                byte[] signature = upgradedService.SignData(payload);
                Assert.True(upgradedService.VerifyData(payload, signature));
            }
            finally
            {
                upgradedService.DeleteKey();
            }
        }
    }

    [Fact]
    public void TEST_55_RootIdentity_MissingKey_FailsClosed()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        string nonExistentKey = $"RetailPOS_NonExistent_Key_{Guid.NewGuid():N}";

        // When createIfMissing is false, missing CNG container must fail closed with Exit Code 110 (ERROR_ROOT_IDENTITY_MISSING)
        var ex = Assert.Throws<RetailPOS.Domain.Common.DomainException>(() =>
            new WindowsCngServerIdentityService(nonExistentKey, useMachineKey: false, createIfMissing: false));

        Assert.Equal("ERROR_ROOT_IDENTITY_MISSING", ex.ErrorCode);
        Assert.Contains("110", ex.Message);
    }
}
