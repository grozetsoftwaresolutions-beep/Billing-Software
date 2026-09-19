using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Application.Security.Srp6a;

public sealed class Srp6aPairingService : ISrp6aPairingService
{
    private readonly IPairingTokenRepository _repository;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, BigInteger> _ephemeralBStorage = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> _kPakeStorage = new();

    public Srp6aPairingService(IPairingTokenRepository repository)
    {
        _repository = repository;
    }

    public async Task<(Guid PairingOperationId, string HumanPairingCode, byte[] Salt)> CreatePairingSessionAsync(
        Guid storeId,
        Guid terminalId,
        CancellationToken ct = default)
    {
        Guid pairingOpId = Guid.NewGuid();

        // 80-bit CSPRNG value = 10 bytes = 16 Crockford Base32 characters
        byte[] codeBytes = RandomNumberGenerator.GetBytes(10);
        string rawCode = CrockfordBase32.Encode(codeBytes);
        string humanPairingCode = CrockfordBase32.FormatGroups(rawCode, 4);

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(storeId, terminalId);
        byte[] passcodeBytes = Srp6aApplicationProfile.NormalizePasscodeBytes(humanPairingCode);

        BigInteger x = Srp6aApplicationProfile.CalculateX(salt, idBytes, passcodeBytes);
        BigInteger v = Srp6aApplicationProfile.CalculateVerifier(x);
        byte[] vBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, v);

        // Zeroize sensitive intermediate scalars immediately
        CryptographicOperations.ZeroMemory(codeBytes);
        CryptographicOperations.ZeroMemory(passcodeBytes);

        var token = new PairingEnrollmentToken(
            pairingOpId,
            storeId,
            terminalId,
            salt,
            vBytes,
            DateTime.UtcNow.AddSeconds(300));

        await _repository.AddAsync(token, ct);

        // Plaintext humanPairingCode is returned ONLY in-memory for server console display, NEVER written to disk/DB
        return (pairingOpId, humanPairingCode, salt);
    }

    public async Task<byte[]> InitiatePairingAsync(
        Guid pairingOperationId,
        byte[] clientEphemeralA,
        CancellationToken ct = default)
    {
        var token = await _repository.GetByIdAsync(pairingOperationId, ct)
            ?? throw new DomainException("Pairing operation not found.", "ERROR_PAIRING_NOT_FOUND");

        if (token.State == "COMPLETED")
            throw new DomainException("Pairing operation already completed.", "ERROR_PAIRING_OPERATION_ALREADY_COMPLETED");
        if (token.State == "REVOKED")
            throw new DomainException($"Pairing operation revoked ({token.RevocationReason}).", "ERROR_PAIRING_REVOKED");
        if (token.State == "EXPIRED" || DateTime.UtcNow > token.ExpiresAtUtc)
            throw new DomainException("Pairing operation expired.", "ERROR_PAIRING_EXPIRED");

        if (clientEphemeralA == null || clientEphemeralA.Length != 256)
            throw new DomainException("Client ephemeral A must be 256 bytes.", "INVALID_EPHEMERAL_A");

        BigInteger A = new BigInteger(1, clientEphemeralA);
        if (A.Mod(Srp6aConstants.N).Equals(BigInteger.Zero))
        {
            token.IncrementFailedAttempts("INVALID_EPHEMERAL_A_ZERO");
            throw new DomainException("Illegal client ephemeral: A mod N == 0", "INVALID_EPHEMERAL_A");
        }

        BigInteger v = new BigInteger(1, token.Verifier);
        BigInteger b = Srp6aApplicationProfile.GenerateEphemeralScalar();
        BigInteger B = Srp6aApplicationProfile.CalculateServerEphemeralB(v, b);
        byte[] bBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, B);

        _ephemeralBStorage[pairingOperationId] = b;
        token.RecordClientAndServerEphemerals(clientEphemeralA, bBytes);

        return bBytes;
    }

    public async Task<(EncryptedTmkDto EncryptedTmk, string ServerEvidenceM2, string PublicKeySpki)> ConfirmPairingAsync(
        Guid pairingOperationId,
        byte[] clientEvidenceM1,
        string sasConfirmationCode,
        string candidateF1,
        byte[] retryAuthToken,
        byte[] tmkBytes,
        string activeServerFingerprint,
        string publicKeySpkiBase64,
        CancellationToken ct = default)
    {
        var token = await _repository.GetByIdAsync(pairingOperationId, ct)
            ?? throw new DomainException("Pairing operation not found.", "ERROR_PAIRING_NOT_FOUND");

        if (token.State == "COMPLETED")
            throw new DomainException("Pairing operation already completed.", "ERROR_PAIRING_OPERATION_ALREADY_COMPLETED");
        if (token.State == "REVOKED")
            throw new DomainException($"Pairing operation revoked ({token.RevocationReason}).", "ERROR_PAIRING_REVOKED");
        if (token.State == "EXPIRED" || DateTime.UtcNow > token.ExpiresAtUtc)
            throw new DomainException("Pairing operation expired.", "ERROR_PAIRING_EXPIRED");

        if (!string.Equals(candidateF1, activeServerFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            token.IncrementFailedAttempts("CANDIDATE_F1_MISMATCH");
            throw new DomainException("Candidate certificate fingerprint does not match server active certificate.", "ERROR_FINGERPRINT_MISMATCH");
        }

        if (!_ephemeralBStorage.TryRemove(pairingOperationId, out var b))
        {
            token.Revoke("SERVER_RESTART_INVALIDATION");
            throw new DomainException("Pairing session ephemerals lost (server restart). Re-initiate pairing.", "PAIRING_OPERATION_CANCELLED_BY_SERVER_RESTART");
        }

        if (token.ClientEphemeralA == null || token.ServerEphemeralB == null)
        {
            token.IncrementFailedAttempts("MISSING_EPHEMERALS");
            throw new DomainException("Ephemeral exchange not completed.", "INVALID_STATE");
        }

        BigInteger A = new BigInteger(1, token.ClientEphemeralA);
        BigInteger B = new BigInteger(1, token.ServerEphemeralB);
        BigInteger v = new BigInteger(1, token.Verifier);

        BigInteger S = Srp6aApplicationProfile.CalculateServerSessionSecret(A, B, v, b);
        byte[] kPake = Srp6aApplicationProfile.CalculateSharedSessionKeyKpake(S);

        byte[] idBytes = Srp6aApplicationProfile.CalculateIdentityBytes(token.StoreId, token.TerminalId);
        byte[] expectedM1 = Srp6aApplicationProfile.CalculateClientEvidenceM1(A, B, idBytes, token.Salt, kPake);

        if (!CryptographicOperations.FixedTimeEquals(clientEvidenceM1, expectedM1))
        {
            token.IncrementFailedAttempts("INVALID_CLIENT_EVIDENCE_M1");
            throw new DomainException("Client evidence M1 verification failed.", "ERROR_PAKE_EVIDENCE_MISMATCH");
        }

        byte[] m2 = Srp6aApplicationProfile.CalculateServerEvidenceM2(A, expectedM1, kPake);
        string expectedSas = Srp6aApplicationProfile.CalculateSasConfirmationCode(kPake, token.StoreId, token.TerminalId, pairingOperationId, expectedM1, m2);

        string normSubmittedSas = CrockfordBase32.Normalize(sasConfirmationCode);
        string normExpectedSas = CrockfordBase32.Normalize(expectedSas);

        if (!string.Equals(normSubmittedSas, normExpectedSas, StringComparison.Ordinal))
        {
            token.IncrementFailedAttempts("SAS_CONFIRMATION_MISMATCH");
            throw new DomainException("SAS confirmation code mismatch.", "ERROR_SAS_CODE_MISMATCH");
        }

        byte[] kWrap = Srp6aApplicationProfile.DeriveKeyWrap(kPake, A, B);
        var encryptedTmk = Srp6aApplicationProfile.WrapTmk(kWrap, tmkBytes, token.StoreId, token.TerminalId);
        byte[] retryAuthHash = Srp6aApplicationProfile.CalculateRetryAuthHash(retryAuthToken);

        string m2B64 = Convert.ToBase64String(m2);
        string encryptedTmkJson = JsonSerializer.Serialize(encryptedTmk);

        token.ProvisionTmk(retryAuthHash, encryptedTmkJson, m2B64);

        // Store kPake ephemerally until FinalizePairingAsync
        _kPakeStorage[pairingOperationId] = (byte[])kPake.Clone();

        // Zeroize cryptographic keys from memory
        CryptographicOperations.ZeroMemory(kPake);
        CryptographicOperations.ZeroMemory(kWrap);

        return (encryptedTmk, m2B64, publicKeySpkiBase64);
    }

    public async Task<(EncryptedTmkDto EncryptedTmk, string ServerEvidenceM2, string PublicKeySpki)> RetryPairingAsync(
        Guid pairingOperationId,
        Guid storeId,
        Guid terminalId,
        string candidateF1,
        byte[] retryAuthToken,
        string activeServerFingerprint,
        string publicKeySpkiBase64,
        CancellationToken ct = default)
    {
        var token = await _repository.GetByIdAsync(pairingOperationId, ct)
            ?? throw new DomainException("Pairing operation not found.", "ERROR_PAIRING_NOT_FOUND");

        if (token.State == "COMPLETED")
            throw new DomainException("Pairing operation already completed.", "ERROR_PAIRING_OPERATION_ALREADY_COMPLETED");
        if (token.State == "REVOKED")
            throw new DomainException($"Pairing operation revoked ({token.RevocationReason}).", "ERROR_PAIRING_REVOKED");
        if (token.State == "EXPIRED" || DateTime.UtcNow > token.ExpiresAtUtc)
            throw new DomainException("Pairing operation expired.", "ERROR_PAIRING_EXPIRED");

        if (token.State != "TMK_PROVISIONED")
            throw new DomainException($"Cannot retry pairing from state {token.State}.", "INVALID_STATE");

        if (token.StoreId != storeId || token.TerminalId != terminalId)
            throw new DomainException("Store ID or Terminal ID mismatch.", "ERROR_TERMINAL_ISOLATION_VIOLATION");

        if (!string.Equals(candidateF1, activeServerFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Candidate fingerprint mismatch.", "ERROR_FINGERPRINT_MISMATCH");

        byte[] candidateHash = Srp6aApplicationProfile.CalculateRetryAuthHash(retryAuthToken);
        if (token.RetryAuthHash == null || !CryptographicOperations.FixedTimeEquals(candidateHash, token.RetryAuthHash))
        {
            token.IncrementFailedAttempts("INVALID_RETRY_AUTH_TOKEN");
            throw new DomainException("Invalid retry authorization token.", "ERROR_INVALID_RETRY_TOKEN");
        }

        var encryptedTmk = JsonSerializer.Deserialize<EncryptedTmkDto>(token.EncryptedTmkBlob!)
            ?? throw new DomainException("Encrypted TMK blob corrupted.", "ERROR_CORRUPT_TMK_BLOB");

        return (encryptedTmk, token.ServerEvidenceM2!, publicKeySpkiBase64);
    }

    public async Task FinalizePairingAsync(
        Guid pairingOperationId,
        byte[] finalizeProof,
        byte[]? kPake = null,
        CancellationToken ct = default)
    {
        var token = await _repository.GetByIdAsync(pairingOperationId, ct)
            ?? throw new DomainException("Pairing operation not found.", "ERROR_PAIRING_NOT_FOUND");

        if (token.State == "COMPLETED")
            throw new DomainException("Pairing operation already completed.", "ERROR_PAIRING_OPERATION_ALREADY_COMPLETED");
        if (token.State != "TMK_PROVISIONED")
            throw new DomainException($"Cannot finalize pairing from state {token.State}.", "INVALID_STATE");

        byte[]? keyToUse = kPake;
        bool mustZeroize = false;
        if (keyToUse == null)
        {
            if (!_kPakeStorage.TryRemove(pairingOperationId, out keyToUse))
            {
                throw new DomainException("Pairing session expired or server restarted.", "ERROR_PAIRING_SESSION_LOST");
            }
            mustZeroize = true;
        }

        try
        {
            byte[] m2 = Convert.FromBase64String(token.ServerEvidenceM2!);
            if (!Srp6aApplicationProfile.VerifyFinalizeProof(keyToUse, pairingOperationId, m2, finalizeProof))
            {
                token.IncrementFailedAttempts("INVALID_FINALIZE_PROOF");
                throw new DomainException("Finalize proof verification failed.", "ERROR_INVALID_FINALIZE_PROOF");
            }

            token.Complete(DateTime.UtcNow);
        }
        finally
        {
            if (mustZeroize && keyToUse != null)
            {
                CryptographicOperations.ZeroMemory(keyToUse);
            }
        }
    }
}
