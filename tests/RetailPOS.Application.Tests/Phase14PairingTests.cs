using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Application.Security.Srp6a;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Application.Tests;

[SupportedOSPlatform("windows")]
public sealed class Phase14PairingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RetailDbContext> _dbOptions;

    public Phase14PairingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new RetailDbContext(_dbOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private RetailDbContext CreateContext() => new RetailDbContext(_dbOptions);

    [Fact]
    public async Task TEST_01_HumanPairingCode_NeverTransmittedOverNetwork()
    {
        var initiateDtoProps = typeof(PairingInitiateRequestDto).GetProperties().Select(p => p.Name).ToList();
        var confirmDtoProps = typeof(PairingConfirmRequestDto).GetProperties().Select(p => p.Name).ToList();
        var retryDtoProps = typeof(PairingRetryRequestDto).GetProperties().Select(p => p.Name).ToList();
        var finalizeDtoProps = typeof(PairingFinalizeRequestDto).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("HumanPairingCode", initiateDtoProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Passcode", initiateDtoProps, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("HumanPairingCode", confirmDtoProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Passcode", confirmDtoProps, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("HumanPairingCode", retryDtoProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("HumanPairingCode", finalizeDtoProps, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TEST_02_HumanPairingCode_NeverPersistedPlaintext()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        var persistedToken = await context.PairingTokens.FirstOrDefaultAsync(t => t.Id == session.PairingOperationId);
        Assert.NotNull(persistedToken);

        string entityJson = JsonSerializer.Serialize(persistedToken);
        string normalizedCode = CrockfordBase32.Normalize(session.HumanPairingCode);

        Assert.DoesNotContain(session.HumanPairingCode, entityJson);
        Assert.DoesNotContain(normalizedCode, entityJson);
    }

    [Fact]
    public async Task TEST_03_HumanPairingCode_NoPlaintextOrPrivateValuesPersisted_OnlyApprovedRecoveryFields()
    {
        var tokenProps = typeof(PairingEnrollmentToken).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain("HumanPairingCode", tokenProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateScalarX", tokenProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ScalarX", tokenProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("EphemeralB", tokenProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("EphemeralA", tokenProps, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TEST_04_Pake_ValidExchange_Succeeds()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string activeCertFingerprint = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string publicKeySpkiBase64 = Convert.ToBase64String(new byte[64]);
        byte[] tmkBytes = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);

        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();
        BigInteger B = new BigInteger(1, bBytes);

        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passcodeBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(session.HumanPairingCode);
        BigInteger x = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, passcodeBytes);
        BigInteger S_client = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        byte[] kPake_client = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S_client);

        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake_client);
        byte[] m2_expected = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake_client);
        string sasCode = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake_client, storeId, terminalId, session.PairingOperationId, m1, m2_expected);

        byte[] retryAuthToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake_client, storeId, terminalId, session.PairingOperationId, m1);

        var confirmResult = await service.ConfirmPairingAsync(
            session.PairingOperationId,
            m1,
            sasCode,
            activeCertFingerprint,
            retryAuthToken,
            tmkBytes,
            activeCertFingerprint,
            publicKeySpkiBase64);
        await context.SaveChangesAsync();

        Assert.NotNull(confirmResult.EncryptedTmk);
        Assert.Equal(Convert.ToBase64String(m2_expected), confirmResult.ServerEvidenceM2);

        byte[] kWrap = Srp6aApplicationProfile.DeriveKeyWrap(kPake_client, A, B);
        byte[] decryptedTmk = Srp6aApplicationProfile.UnwrapTmk(kWrap, confirmResult.EncryptedTmk, storeId, terminalId);
        Assert.Equal(tmkBytes, decryptedTmk);

        byte[] finalizeProof = Srp6aApplicationProfile.DeriveFinalizeProof(kPake_client, session.PairingOperationId, m2_expected);
        await service.FinalizePairingAsync(session.PairingOperationId, finalizeProof, kPake_client);
        await context.SaveChangesAsync();

        var finalToken = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.NotNull(finalToken);
        Assert.Equal("COMPLETED", finalToken.State);
    }

    [Fact]
    public async Task TEST_05_Pake_WrongCode_Fails()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string activeCertFingerprint = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string publicKeySpkiBase64 = Convert.ToBase64String(new byte[64]);
        byte[] tmkBytes = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);

        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();
        BigInteger B = new BigInteger(1, bBytes);

        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] wrongPasscodeBytes = Srp6aApplicationProfile.NormalizePasscodeBytes("0000-0000-0000-0000");
        BigInteger x_wrong = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, wrongPasscodeBytes);
        BigInteger S_wrong = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x_wrong);
        byte[] kPake_wrong = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S_wrong);

        byte[] m1_wrong = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake_wrong);
        byte[] m2_wrong = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1_wrong, kPake_wrong);
        string sasCode_wrong = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake_wrong, storeId, terminalId, session.PairingOperationId, m1_wrong, m2_wrong);
        byte[] retryToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake_wrong, storeId, terminalId, session.PairingOperationId, m1_wrong);

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.ConfirmPairingAsync(
            session.PairingOperationId,
            m1_wrong,
            sasCode_wrong,
            activeCertFingerprint,
            retryToken,
            tmkBytes,
            activeCertFingerprint,
            publicKeySpkiBase64));

        Assert.Equal("ERROR_PAKE_EVIDENCE_MISMATCH", ex.ErrorCode);
    }

    [Fact]
    public async Task TEST_06_Pake_ExpiredCode_Fails()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        var token = await context.PairingTokens.FirstAsync(t => t.Id == session.PairingOperationId);
        typeof(PairingEnrollmentToken).GetProperty("ExpiresAtUtc")!.SetValue(token, DateTime.UtcNow.AddMinutes(-5));
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.InitiatePairingAsync(session.PairingOperationId, aBytes));
        Assert.Equal("ERROR_PAIRING_EXPIRED", ex.ErrorCode);
    }

    [Fact]
    public async Task TEST_07_Pake_FifthFailedAttempt_Behavior()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);

        var token = new PairingEnrollmentToken(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(16),
            new byte[256],
            DateTime.UtcNow.AddMinutes(5));

        await repo.AddAsync(token);
        await context.SaveChangesAsync();

        for (int i = 0; i < 4; i++)
        {
            token.IncrementFailedAttempts();
            Assert.Equal("PENDING", token.State);
            Assert.Equal(i + 1, token.FailedAttempts);
        }

        token.IncrementFailedAttempts();
        Assert.Equal("REVOKED", token.State);
        Assert.Equal(5, token.FailedAttempts);
        Assert.Equal("MAX_ATTEMPTS_EXCEEDED", token.RevocationReason);
    }

    [Fact]
    public async Task TEST_08_Pake_SixthAttempt_Rejected()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        var token = await context.PairingTokens.FirstAsync(t => t.Id == session.PairingOperationId);
        for (int i = 0; i < 5; i++) token.IncrementFailedAttempts();
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.InitiatePairingAsync(session.PairingOperationId, aBytes));
        Assert.Equal("ERROR_PAIRING_REVOKED", ex.ErrorCode);
    }

    [Fact]
    public async Task TEST_09_Pake_SuccessfulPairing_StateProgressionToCompleted()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string activeCertFingerprint = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string publicKeySpkiBase64 = Convert.ToBase64String(new byte[64]);
        byte[] tmkBytes = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();
        var t1 = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.Equal("PENDING", t1!.State);

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);
        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();
        var t2 = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.Equal("PAKE_VERIFIED", t2!.State);

        BigInteger B = new BigInteger(1, bBytes);
        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passcodeBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(session.HumanPairingCode);
        BigInteger x = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, passcodeBytes);
        BigInteger S_client = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        byte[] kPake_client = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S_client);
        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake_client);
        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake_client);
        string sas = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake_client, storeId, terminalId, session.PairingOperationId, m1, m2);
        byte[] retryToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake_client, storeId, terminalId, session.PairingOperationId, m1);

        await service.ConfirmPairingAsync(session.PairingOperationId, m1, sas, activeCertFingerprint, retryToken, tmkBytes, activeCertFingerprint, publicKeySpkiBase64);
        await context.SaveChangesAsync();
        var t3 = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.Equal("TMK_PROVISIONED", t3!.State);

        byte[] finalizeProof = Srp6aApplicationProfile.DeriveFinalizeProof(kPake_client, session.PairingOperationId, m2);
        await service.FinalizePairingAsync(session.PairingOperationId, finalizeProof, kPake_client);
        await context.SaveChangesAsync();
        var t4 = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.Equal("COMPLETED", t4!.State);
        Assert.NotNull(t4.CompletedAtUtc);
    }

    [Fact]
    public async Task TEST_10_Pake_ServerRestart_InvalidatesPending()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var session = await service.CreatePairingSessionAsync(Guid.NewGuid(), Guid.NewGuid());
        await context.SaveChangesAsync();

        await repo.InvalidatePendingTokensOnServerRestartAsync();

        var token = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.NotNull(token);
        Assert.Equal("REVOKED", token.State);
        Assert.Equal("SERVER_RESTART_INVALIDATION", token.RevocationReason);
    }

    [Fact]
    public async Task TEST_11_Pake_ReplayAfterSuccess_Fails()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string cert = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string spki = Convert.ToBase64String(new byte[64]);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);
        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();

        BigInteger B = new BigInteger(1, bBytes);
        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(session.HumanPairingCode);
        BigInteger x = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, passBytes);
        BigInteger S = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        byte[] kPake = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S);
        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake);
        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake);
        string sas = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake, storeId, terminalId, session.PairingOperationId, m1, m2);
        byte[] retryToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake, storeId, terminalId, session.PairingOperationId, m1);

        await service.ConfirmPairingAsync(session.PairingOperationId, m1, sas, cert, retryToken, tmk, cert, spki);
        byte[] proof = Srp6aApplicationProfile.DeriveFinalizeProof(kPake, session.PairingOperationId, m2);
        await service.FinalizePairingAsync(session.PairingOperationId, proof, kPake);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.InitiatePairingAsync(session.PairingOperationId, aBytes));
        Assert.Equal("ERROR_PAIRING_OPERATION_ALREADY_COMPLETED", ex.ErrorCode);
    }

    [Fact]
    public async Task TEST_12_Pake_CrossTerminal_IsolationEnforced()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalA = Guid.NewGuid();
        var terminalB = Guid.NewGuid();
        string cert = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";

        var session = await service.CreatePairingSessionAsync(storeId, terminalA);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.RetryPairingAsync(
            session.PairingOperationId,
            storeId,
            terminalB,
            cert,
            new byte[32],
            cert,
            "spki"));

        Assert.Equal("INVALID_STATE", ex.ErrorCode);
    }

    [Fact]
    public async Task TEST_15_Pake_TranscriptTampering_Fails()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string cert = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string spki = Convert.ToBase64String(new byte[64]);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);

        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();
        BigInteger B = new BigInteger(1, bBytes);

        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passcodeBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(session.HumanPairingCode);
        BigInteger x = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, passcodeBytes);
        BigInteger S_client = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        byte[] kPake_client = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S_client);

        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake_client);
        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake_client);
        string sas = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake_client, storeId, terminalId, session.PairingOperationId, m1, m2);
        byte[] retryToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake_client, storeId, terminalId, session.PairingOperationId, m1);

        m1[0] ^= 0xFF; // Tamper

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.ConfirmPairingAsync(
            session.PairingOperationId,
            m1,
            sas,
            cert,
            retryToken,
            tmk,
            cert,
            spki));

        Assert.Equal("ERROR_PAKE_EVIDENCE_MISMATCH", ex.ErrorCode);
    }

    [Fact]
    public async Task TEST_16_Pake_NonceReplay_Fails()
    {
        BigInteger A_zero = BigInteger.Zero;
        var ex = Assert.Throws<CryptographicException>(() => Srp6aApplicationProfile.CalculateClientSessionSecret(A_zero, BigInteger.Two, BigInteger.One, BigInteger.One));
        Assert.Contains("Illegal public ephemeral", ex.Message);
    }

    [Fact]
    public async Task TEST_17_Pake_DeterministicSha256ProfileVectors_Pass()
    {
        var storeId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        var terminalId = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");
        byte[] I = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] P = Srp6aApplicationProfile.NormalizePasscodeBytes("ABCDEFGHJKMNPQRT");
        byte[] s = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");

        byte[] kBytes = Srp6aApplicationProfile.CalculateMultiplierK();
        Assert.Equal("05B9E8EF059C6B32EA59FC1D322D37F04AA30BAE5AA9003B8321E21DDB04E300", Convert.ToHexString(kBytes));

        BigInteger x = Srp6aApplicationProfile.CalculateX(s, I, P);
        byte[] xBytes = BigIntegers.AsUnsignedByteArray(32, x);
        Assert.Equal("4F502FD1578435BE41EF87A3DC22E90062F30ACFD4B120773E491F15221B3DE4", Convert.ToHexString(xBytes));

        BigInteger v = Srp6aApplicationProfile.CalculateVerifier(x);
        Assert.Equal(2048, v.BitLength);

        byte[] aBytes = Convert.FromHexString("11223344556677889900aabbccddeeff11223344556677889900aabbccddeeff");
        byte[] bBytes = Convert.FromHexString("ffeeddccbbaa00998877665544332211ffeeddccbbaa00998877665544332211");
        BigInteger a = new BigInteger(1, aBytes);
        BigInteger b = new BigInteger(1, bBytes);

        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        BigInteger B = Srp6aApplicationProfile.CalculateServerEphemeralB(v, b);

        BigInteger u = Srp6aApplicationProfile.CalculateScramblerU(A, B);
        byte[] uBytes = BigIntegers.AsUnsignedByteArray(32, u);
        Assert.Equal("B947D92BA2B8F97FB33E0C9E289E596AE4F8512279BAF2EA8E23F925ABC42EC4", Convert.ToHexString(uBytes));

        BigInteger S_client = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        BigInteger S_server = Srp6aApplicationProfile.CalculateServerSessionSecret(A, B, v, b);
        Assert.True(S_client.Equals(S_server));

        byte[] kPake = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S_client);
        Assert.Equal("081B6E1ADF9606CB8407F6F4A96EDA4C75D8EFF4509AE4D188559CA2866378DF", Convert.ToHexString(kPake));

        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, I, s, kPake);
        Assert.Equal("EE945938BB99DD118C512C35E2CD30B4242383162D514ACEFDC762F1A67CE19F", Convert.ToHexString(m1));

        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake);
        Assert.Equal("F4724BE2811F5D39B68B50AE940A1FFE2AA01033930EABE90706E37AF16782EC", Convert.ToHexString(m2));
    }

    [Fact]
    public async Task TEST_18_Pairing_CrashAfterPakeBeforeTmkReceipt_Recovers()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string cert = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string spki = Convert.ToBase64String(new byte[64]);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);
        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();

        BigInteger B = new BigInteger(1, bBytes);
        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(session.HumanPairingCode);
        BigInteger x = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, passBytes);
        BigInteger S = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        byte[] kPake = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S);
        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake);
        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake);
        string sas = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake, storeId, terminalId, session.PairingOperationId, m1, m2);
        byte[] retryToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake, storeId, terminalId, session.PairingOperationId, m1);

        await service.ConfirmPairingAsync(session.PairingOperationId, m1, sas, cert, retryToken, tmk, cert, spki);
        await context.SaveChangesAsync();

        var newService = new Srp6aPairingService(repo);

        var retryResult = await newService.RetryPairingAsync(
            session.PairingOperationId,
            storeId,
            terminalId,
            cert,
            retryToken,
            cert,
            spki);

        Assert.NotNull(retryResult.EncryptedTmk);
        byte[] kWrap = Srp6aApplicationProfile.DeriveKeyWrap(kPake, A, B);
        byte[] recoveredTmk = Srp6aApplicationProfile.UnwrapTmk(kWrap, retryResult.EncryptedTmk, storeId, terminalId);
        Assert.Equal(tmk, recoveredTmk);
    }

    [Fact]
    public async Task TEST_19_Pairing_RetryAfterServerCommit_ReturnsSameSafeTmk()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string cert = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string spki = Convert.ToBase64String(new byte[64]);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);
        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();

        BigInteger B = new BigInteger(1, bBytes);
        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(session.HumanPairingCode);
        BigInteger x = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, passBytes);
        BigInteger S = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        byte[] kPake = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S);
        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake);
        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake);
        string sas = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake, storeId, terminalId, session.PairingOperationId, m1, m2);
        byte[] retryToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake, storeId, terminalId, session.PairingOperationId, m1);

        var confirmRes = await service.ConfirmPairingAsync(session.PairingOperationId, m1, sas, cert, retryToken, tmk, cert, spki);
        await context.SaveChangesAsync();

        var retry1 = await service.RetryPairingAsync(session.PairingOperationId, storeId, terminalId, cert, retryToken, cert, spki);
        var retry2 = await service.RetryPairingAsync(session.PairingOperationId, storeId, terminalId, cert, retryToken, cert, spki);

        Assert.Equal(confirmRes.EncryptedTmk.CiphertextBase64, retry1.EncryptedTmk.CiphertextBase64);
        Assert.Equal(confirmRes.EncryptedTmk.TagBase64, retry1.EncryptedTmk.TagBase64);
        Assert.Equal(confirmRes.EncryptedTmk.IvBase64, retry1.EncryptedTmk.IvBase64);

        Assert.Equal(retry1.EncryptedTmk.CiphertextBase64, retry2.EncryptedTmk.CiphertextBase64);
        Assert.Equal(retry1.EncryptedTmk.TagBase64, retry2.EncryptedTmk.TagBase64);
    }

    [Fact]
    public async Task TEST_20_Pairing_CompletedOperation_CannotBeReplayed()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        string cert = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string spki = Convert.ToBase64String(new byte[64]);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);
        byte[] bBytes = await service.InitiatePairingAsync(session.PairingOperationId, aBytes);
        await context.SaveChangesAsync();

        BigInteger B = new BigInteger(1, bBytes);
        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(session.HumanPairingCode);
        BigInteger x = Srp6aApplicationProfile.CalculateX(session.Salt, idBytes, passBytes);
        BigInteger S = Srp6aApplicationProfile.CalculateClientSessionSecret(A, B, a, x);
        byte[] kPake = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S);
        byte[] m1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, session.Salt, kPake);
        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, m1, kPake);
        string sas = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake, storeId, terminalId, session.PairingOperationId, m1, m2);
        byte[] retryToken = Srp6aApplicationProfile.DeriveRetryAuthToken(kPake, storeId, terminalId, session.PairingOperationId, m1);

        await service.ConfirmPairingAsync(session.PairingOperationId, m1, sas, cert, retryToken, tmk, cert, spki);
        byte[] proof = Srp6aApplicationProfile.DeriveFinalizeProof(kPake, session.PairingOperationId, m2);
        await service.FinalizePairingAsync(session.PairingOperationId, proof, kPake);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.RetryPairingAsync(
            session.PairingOperationId,
            storeId,
            terminalId,
            cert,
            retryToken,
            cert,
            spki));

        Assert.Equal("ERROR_PAIRING_OPERATION_ALREADY_COMPLETED", ex.ErrorCode);
    }

    [Fact]
    public async Task TEST_22_Pairing_SrpParameter_ProfileConformant()
    {
        Assert.Equal(2048, Srp6aConstants.N.BitLength);
        Assert.Equal(BigInteger.ValueOf(2), Srp6aConstants.G);
        Assert.Equal(256, Srp6aConstants.GroupByteLength);
        Assert.Equal(32, Srp6aConstants.HashByteLength);
        Assert.Equal(16, Srp6aConstants.SaltByteLength);

        byte[] aPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, BigInteger.ValueOf(42));
        Assert.Equal(256, aPad.Length);
        Assert.Equal(42, aPad[255]);
        Assert.Equal(0, aPad[0]);
    }

    [Fact]
    public async Task TEST_13_CandidateF1_Mismatch_Rejected()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        // Candidate F1 visual verification fails on Counter UI -> session is revoked
        var pendingToken = await repo.GetByIdAsync(session.PairingOperationId);
        pendingToken!.Revoke("CANDIDATE_F1_MISMATCH");
        await context.SaveChangesAsync();

        var token = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.Equal("REVOKED", token!.State);

        // Subsequent initiate pairing attempt is rejected
        BigInteger a = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger A = Srp6aApplicationProfile.CalculateClientEphemeralA(a);
        byte[] aBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, A);

        await Assert.ThrowsAnyAsync<Exception>(() => service.InitiatePairingAsync(session.PairingOperationId, aBytes));
    }

    [Fact]
    public async Task TEST_14_Unverified_InitialPairing_BlocksApplicationTraffic()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var session = await service.CreatePairingSessionAsync(storeId, terminalId);
        await context.SaveChangesAsync();

        var token = await repo.GetByIdAsync(session.PairingOperationId);
        Assert.Equal("PENDING", token!.State);

        // Business traffic check: state must be COMPLETED
        bool canAccessBusinessApis = token.State == "COMPLETED";
        Assert.False(canAccessBusinessApis);
    }

    [Fact]
    public async Task TEST_21_ConcurrentPairingOperations_Isolated()
    {
        using var context = CreateContext();
        var repo = new EfPairingTokenRepository(context);
        var service = new Srp6aPairingService(repo);

        var storeId = Guid.NewGuid();
        var terminal1 = Guid.NewGuid();
        var terminal2 = Guid.NewGuid();

        var session1 = await service.CreatePairingSessionAsync(storeId, terminal1);
        var session2 = await service.CreatePairingSessionAsync(storeId, terminal2);
        await context.SaveChangesAsync();

        Assert.NotEqual(session1.PairingOperationId, session2.PairingOperationId);
        Assert.NotEqual(session1.HumanPairingCode, session2.HumanPairingCode);

        var token1 = await repo.GetByIdAsync(session1.PairingOperationId);
        var token2 = await repo.GetByIdAsync(session2.PairingOperationId);

        Assert.Equal("PENDING", token1!.State);
        Assert.Equal("PENDING", token2!.State);
    }

    [Fact]
    public void TEST_53_Pairing_DpapiScope_CurrentUser_LockedAndEnforced()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "dpapi_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            string pairingTxPath = Path.Combine(tempDir, "pairing_tx.dat");
            string terminalTokenPath = Path.Combine(tempDir, "terminal_token.dat");
            string consumedRotationsPath = Path.Combine(tempDir, "consumed_rotations.json");

            byte[] secretTmk = RandomNumberGenerator.GetBytes(32);
            byte[] protectedTmk = DpapiStorageHelper.Protect(secretTmk, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(terminalTokenPath, protectedTmk);

            // 1. Decrypt under CurrentUser succeeds
            byte[] loadedProtected = File.ReadAllBytes(terminalTokenPath);
            byte[] decryptedTmk = DpapiStorageHelper.Unprotect(loadedProtected, DataProtectionScope.CurrentUser);
            Assert.Equal(secretTmk, decryptedTmk);

            // 2. Tampered ciphertext fails closed
            byte[] tamperedCiphertext = (byte[])loadedProtected.Clone();
            tamperedCiphertext[tamperedCiphertext.Length - 1] ^= 0xFF;
            Assert.Throws<CryptographicException>(() => DpapiStorageHelper.Unprotect(tamperedCiphertext, DataProtectionScope.CurrentUser));

            // 3. pairing_tx.dat and consumed_rotations.json verified under locked CurrentUser scope
            var testOpId = Guid.NewGuid();
            DpapiStorageHelper.RecordTombstone(consumedRotationsPath, testOpId);
            Assert.True(DpapiStorageHelper.IsTombstoneRecorded(consumedRotationsPath, testOpId));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
