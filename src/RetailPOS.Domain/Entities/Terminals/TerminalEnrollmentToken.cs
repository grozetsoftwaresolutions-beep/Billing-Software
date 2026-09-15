using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Entities.Terminals;

public sealed class TerminalEnrollmentToken : BaseEntity<Guid>
{
    public string TokenHash { get; private set; } = null!;
    public EnrollmentTokenPurpose TokenPurpose { get; private set; }
    public Guid? BoundTerminalId { get; private set; }
    public string? BoundServerId { get; private set; }
    public string? BoundServerFingerprint { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public bool IsConsumed { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string CreatedByUserId { get; private set; } = null!;

    private TerminalEnrollmentToken() { }

    public TerminalEnrollmentToken(
        Guid id,
        string tokenHash,
        EnrollmentTokenPurpose tokenPurpose,
        Guid? boundTerminalId,
        string? boundServerId,
        string? boundServerFingerprint,
        DateTime expiresAtUtc,
        string createdByUserId)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainException("Token hash cannot be empty.", "INVALID_TOKEN_HASH");
        if (string.IsNullOrWhiteSpace(createdByUserId))
            throw new DomainException("Created by user ID cannot be empty.", "INVALID_USER_ID");

        if (tokenPurpose == EnrollmentTokenPurpose.NewEnrollment && boundTerminalId.HasValue)
            throw new DomainException("New enrollment token cannot be bound to a terminal ID.", "INVALID_TOKEN_BINDING");

        if ((tokenPurpose == EnrollmentTokenPurpose.ExistingTerminalRepair || tokenPurpose == EnrollmentTokenPurpose.ServerIdentityRePinning)
            && !boundTerminalId.HasValue)
            throw new DomainException("Repair or re-pinning token must be bound to a terminal ID.", "TERMINAL_ID_REQUIRED");

        if (tokenPurpose == EnrollmentTokenPurpose.ServerIdentityRePinning)
        {
            if (string.IsNullOrWhiteSpace(boundServerId))
                throw new DomainException("Re-pinning token must have BoundServerId.", "SERVER_ID_REQUIRED");
            if (string.IsNullOrWhiteSpace(boundServerFingerprint) || boundServerFingerprint.Length != 64)
                throw new DomainException("Re-pinning token must have a 64-character hex BoundServerFingerprint.", "SERVER_FINGERPRINT_INVALID");
        }

        Id = id;
        TokenHash = tokenHash.Trim();
        TokenPurpose = tokenPurpose;
        BoundTerminalId = boundTerminalId;
        BoundServerId = boundServerId?.Trim();
        BoundServerFingerprint = boundServerFingerprint?.Trim().ToUpperInvariant();
        ExpiresAtUtc = expiresAtUtc;
        CreatedByUserId = createdByUserId.Trim();
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
        IsConsumed = false;
        IsRevoked = false;
    }

    public void Consume(DateTime timestamp)
    {
        if (IsConsumed)
            throw new DomainException("Enrollment token has already been consumed.", "ENROLLMENT_TOKEN_ALREADY_USED");
        if (IsRevoked)
            throw new DomainException("Enrollment token has been revoked.", "ENROLLMENT_TOKEN_REVOKED");
        if (timestamp > ExpiresAtUtc)
            throw new DomainException("Enrollment token has expired.", "ENROLLMENT_TOKEN_EXPIRED");

        IsConsumed = true;
        ConsumedAtUtc = timestamp;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Revoke(DateTime timestamp)
    {
        if (IsConsumed)
            throw new DomainException("Cannot revoke an already consumed token.", "ENROLLMENT_TOKEN_ALREADY_USED");
        if (IsRevoked)
            return;

        IsRevoked = true;
        RevokedAtUtc = timestamp;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
