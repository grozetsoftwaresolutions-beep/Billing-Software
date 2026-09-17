using System;
using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Terminals;

public sealed class PairingEnrollmentToken : BaseEntity<Guid>
{
    public Guid PairingOperationId => Id;
    public Guid StoreId { get; private set; }
    public Guid TerminalId { get; private set; }
    public byte[] Salt { get; private set; } = Array.Empty<byte>();
    public byte[] Verifier { get; private set; } = Array.Empty<byte>();
    public byte[]? ClientEphemeralA { get; private set; }
    public byte[]? ServerEphemeralB { get; private set; }
    public string State { get; private set; } = "PENDING"; // PENDING, PAKE_VERIFIED, TMK_PROVISIONED, COMPLETED, EXPIRED, REVOKED
    public byte[]? RetryAuthHash { get; private set; }
    public string? EncryptedTmkBlob { get; private set; }
    public string? ServerEvidenceM2 { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ProvisionedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public string? RevocationReason { get; private set; }

    private PairingEnrollmentToken() { }

    public PairingEnrollmentToken(
        Guid pairingOperationId,
        Guid storeId,
        Guid terminalId,
        byte[] salt,
        byte[] verifier,
        DateTime expiresAtUtc)
    {
        if (pairingOperationId == Guid.Empty)
            throw new DomainException("Pairing operation ID cannot be empty.", "INVALID_PAIRING_OPERATION_ID");
        if (storeId == Guid.Empty)
            throw new DomainException("Store ID cannot be empty.", "INVALID_STORE_ID");
        if (terminalId == Guid.Empty)
            throw new DomainException("Terminal ID cannot be empty.", "INVALID_TERMINAL_ID");
        if (salt == null || salt.Length != 16)
            throw new DomainException("Salt must be exactly 16 bytes.", "INVALID_SALT");
        if (verifier == null || verifier.Length != 256)
            throw new DomainException("Verifier must be exactly 256 bytes.", "INVALID_VERIFIER");

        Id = pairingOperationId;
        StoreId = storeId;
        TerminalId = terminalId;
        Salt = (byte[])salt.Clone();
        Verifier = (byte[])verifier.Clone();
        State = "PENDING";
        FailedAttempts = 0;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = expiresAtUtc;
    }

    public void RecordClientAndServerEphemerals(byte[] clientA, byte[] serverB)
    {
        AssertNotRevokedOrExpired();
        if (State != "PENDING")
            throw new DomainException($"Cannot record ephemerals in state {State}.", "INVALID_STATE_TRANSITION");

        ClientEphemeralA = (byte[])clientA.Clone();
        ServerEphemeralB = (byte[])serverB.Clone();
        State = "PAKE_VERIFIED";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void ProvisionTmk(byte[] retryAuthHash, string encryptedTmkBlob, string serverEvidenceM2)
    {
        AssertNotRevokedOrExpired();
        if (State != "PAKE_VERIFIED")
            throw new DomainException($"Cannot provision TMK in state {State}.", "INVALID_STATE_TRANSITION");
        if (retryAuthHash == null || retryAuthHash.Length != 32)
            throw new DomainException("RetryAuthHash must be exactly 32 bytes.", "INVALID_RETRY_AUTH_HASH");
        if (string.IsNullOrWhiteSpace(encryptedTmkBlob))
            throw new DomainException("EncryptedTmkBlob cannot be empty.", "INVALID_TMK_BLOB");
        if (string.IsNullOrWhiteSpace(serverEvidenceM2))
            throw new DomainException("ServerEvidenceM2 cannot be empty.", "INVALID_SERVER_EVIDENCE");

        RetryAuthHash = (byte[])retryAuthHash.Clone();
        EncryptedTmkBlob = encryptedTmkBlob;
        ServerEvidenceM2 = serverEvidenceM2;
        State = "TMK_PROVISIONED";
        ProvisionedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Complete(DateTime timestamp)
    {
        AssertNotRevokedOrExpired();
        if (State != "TMK_PROVISIONED")
            throw new DomainException($"Cannot complete pairing from state {State}.", "INVALID_STATE_TRANSITION");

        State = "COMPLETED";
        CompletedAtUtc = timestamp;
        RetryAuthHash = null;
        EncryptedTmkBlob = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void IncrementFailedAttempts(string reason = "INVALID_CREDENTIALS")
    {
        FailedAttempts++;
        UpdatedAtUtc = DateTime.UtcNow;
        if (FailedAttempts >= 5)
        {
            Revoke("MAX_ATTEMPTS_EXCEEDED");
        }
    }

    public void Revoke(string reason)
    {
        if (State == "COMPLETED")
            throw new DomainException("Cannot revoke a completed pairing operation.", "PAIRING_ALREADY_COMPLETED");
        if (State == "REVOKED")
            return;

        State = "REVOKED";
        RevocationReason = reason;
        RetryAuthHash = null;
        EncryptedTmkBlob = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public bool CheckAndApplyExpiration(DateTime utcNow)
    {
        if (State != "COMPLETED" && State != "REVOKED" && State != "EXPIRED" && utcNow > ExpiresAtUtc)
        {
            State = "EXPIRED";
            RevocationReason = "PAIRING_EXPIRED";
            RetryAuthHash = null;
            EncryptedTmkBlob = null;
            UpdatedAtUtc = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    private void AssertNotRevokedOrExpired()
    {
        if (State == "REVOKED")
            throw new DomainException($"Pairing operation is revoked ({RevocationReason}).", "ERROR_PAIRING_REVOKED");
        if (State == "EXPIRED" || DateTime.UtcNow > ExpiresAtUtc)
        {
            State = "EXPIRED";
            throw new DomainException("Pairing operation has expired.", "ERROR_PAIRING_EXPIRED");
        }
    }
}
