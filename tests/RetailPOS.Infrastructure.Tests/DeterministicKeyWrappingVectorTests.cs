using System.Security.Cryptography;
using System.Text;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class DeterministicKeyWrappingVectorTests
{
    private static readonly Guid TestStoreGuid = new("11111111-2222-3333-4444-555555555555");
    private static readonly byte[] TestStoreSigningKey = Convert.FromHexString("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");

    private static char[] RecoveryKeyA => new string('A', 64).ToCharArray();
    private const string ExpectedKwkAHex = "EB17436BE211772C79C5DB76C70898B007A409DA9A5971EE46C8ECFF79A0C7B7";
    private const string ExpectedVerifierAHex = "A62F07D7B0F808302904EA74CAF8F035B2D8A7D080362AA4D42AB1F350E12234";
    private static byte[] NonceA => Convert.FromHexString("000102030405060708090A0B");
    private const string ExpectedWrappedAHex = "000102030405060708090A0B273D55442525D15840149C4F1BF1904CD55C587238BCE98B04219860F508B97F405E506B0EB0EBB66C9D3D9159FE68AB";

    private static char[] RecoveryKeyB => new string('B', 64).ToCharArray();
    private const string ExpectedKwkBHex = "AA73F010D1B1A7E9083C131CFE2A1A585BC71B540176D69A3772DE355AE453D1";
    private const string ExpectedVerifierBHex = "C480FD128CB2943F1658442812C55A6260B3D87B1E6547FDC84B63A0B8DB471B";
    private static readonly byte[] NonceB = Convert.FromHexString("0C0D0E0F1011121314151617");
    private const string ExpectedWrappedBHex = "0C0D0E0F1011121314151617BC7B8FE7D18B4CC15298D1A515C0FB3FCF7EE8CEB3929EEA52E201A76871991634018456F39CD00DEDCA630CCB810B17";

    [Fact]
    public void GenerationA_DerivesExactExpectedVerifierAndWrappedKey()
    {
        var service = new StoreIdentityService();

        var (verifier, wrappedKey) = service.WrapStoreSigningKey(TestStoreGuid, RecoveryKeyA, TestStoreSigningKey, NonceA);

        Assert.Equal(ExpectedVerifierAHex, verifier);
        Assert.Equal(ExpectedWrappedAHex, wrappedKey);
    }

    [Fact]
    public void GenerationB_DerivesExactExpectedVerifierAndWrappedKey()
    {
        var service = new StoreIdentityService();

        var (verifier, wrappedKey) = service.WrapStoreSigningKey(TestStoreGuid, RecoveryKeyB, TestStoreSigningKey, NonceB);

        Assert.Equal(ExpectedVerifierBHex, verifier);
        Assert.Equal(ExpectedWrappedBHex, wrappedKey);
    }

    [Fact]
    public void GenerationA_UnwrapsToExactOriginalStoreSigningKey()
    {
        var service = new StoreIdentityService();

        var unwrappedS = service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyA, ExpectedWrappedAHex);

        Assert.Equal(TestStoreSigningKey, unwrappedS);
    }

    [Fact]
    public void GenerationB_UnwrapsToExactOriginalStoreSigningKey()
    {
        var service = new StoreIdentityService();

        var unwrappedS = service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyB, ExpectedWrappedBHex);

        Assert.Equal(TestStoreSigningKey, unwrappedS);
    }

    [Fact]
    public void CrossGeneration_StableStoreSigningKeyIsIdentical()
    {
        var service = new StoreIdentityService();

        var unwrappedA = service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyA, ExpectedWrappedAHex);
        var unwrappedB = service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyB, ExpectedWrappedBHex);

        Assert.Equal(unwrappedA, unwrappedB);
    }

    [Fact]
    public void BackupManifestSignedUnderGenA_VerifiesUnderGenB()
    {
        var service = new StoreIdentityService();

        var signingKeyA = service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyA, ExpectedWrappedAHex);
        var signingKeyB = service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyB, ExpectedWrappedBHex);

        byte[] payload = Encoding.UTF8.GetBytes("database_sha256_hash_example_1234567890");

        using var hmacA = new HMACSHA256(signingKeyA);
        byte[] signatureA = hmacA.ComputeHash(payload);

        using var hmacB = new HMACSHA256(signingKeyB);
        byte[] signatureB = hmacB.ComputeHash(payload);

        Assert.True(CryptographicOperations.FixedTimeEquals(signatureA, signatureB));
    }

    [Fact]
    public void WrongRecoveryKey_FailsValidatorCheck()
    {
        var service = new StoreIdentityService();

        bool valid = service.ValidateMasterRecoveryKeyAgainstVerifier(TestStoreGuid, RecoveryKeyB, ExpectedVerifierAHex);

        Assert.False(valid);
    }

    [Fact]
    public void CorrectRecoveryKey_PassesValidatorCheck()
    {
        var service = new StoreIdentityService();

        bool validA = service.ValidateMasterRecoveryKeyAgainstVerifier(TestStoreGuid, RecoveryKeyA, ExpectedVerifierAHex);
        bool validB = service.ValidateMasterRecoveryKeyAgainstVerifier(TestStoreGuid, RecoveryKeyB, ExpectedVerifierBHex);

        Assert.True(validA);
        Assert.True(validB);
    }

    [Fact]
    public void WrongRecoveryKey_ThrowsOnUnwrap()
    {
        var service = new StoreIdentityService();

        Assert.ThrowsAny<CryptographicException>(() =>
            service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyB, ExpectedWrappedAHex));
    }

    [Fact]
    public void WrongStoreGuid_ThrowsOnUnwrapDueToAadMismatch()
    {
        var service = new StoreIdentityService();
        var differentStoreGuid = Guid.NewGuid();

        Assert.ThrowsAny<CryptographicException>(() =>
            service.UnwrapStoreSigningKey(differentStoreGuid, RecoveryKeyA, ExpectedWrappedAHex));
    }

    [Fact]
    public void TamperedCiphertext_ThrowsOnUnwrap()
    {
        var service = new StoreIdentityService();

        byte[] wrappedBytes = Convert.FromHexString(ExpectedWrappedAHex);
        wrappedBytes[20] ^= 0xFF; // Tamper ciphertext
        string tamperedHex = Convert.ToHexString(wrappedBytes);

        Assert.ThrowsAny<CryptographicException>(() =>
            service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyA, tamperedHex));
    }

    [Fact]
    public void TamperedTag_ThrowsOnUnwrap()
    {
        var service = new StoreIdentityService();

        byte[] wrappedBytes = Convert.FromHexString(ExpectedWrappedAHex);
        wrappedBytes[^1] ^= 0x01; // Tamper tag
        string tamperedHex = Convert.ToHexString(wrappedBytes);

        Assert.ThrowsAny<CryptographicException>(() =>
            service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyA, tamperedHex));
    }

    [Fact]
    public void TamperedNonce_ThrowsOnUnwrap()
    {
        var service = new StoreIdentityService();

        byte[] wrappedBytes = Convert.FromHexString(ExpectedWrappedAHex);
        wrappedBytes[0] ^= 0xAA; // Tamper nonce
        string tamperedHex = Convert.ToHexString(wrappedBytes);

        Assert.ThrowsAny<CryptographicException>(() =>
            service.UnwrapStoreSigningKey(TestStoreGuid, RecoveryKeyA, tamperedHex));
    }
}
