using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.CashSessions;

public sealed class CashSessionTransaction : BaseEntity<long>
{
    public Guid CashSessionId { get; private set; }
    public CashTransactionType Type { get; private set; }
    public Money Amount { get; private set; }
    public string Reason { get; private set; } = null!;
    public Guid ClientTransactionId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid? AuthorizedByUserId { get; private set; }
    public DateTime TimestampUtc { get; private set; }

    private CashSessionTransaction() { }

    public CashSessionTransaction(
        long id,
        Guid cashSessionId,
        CashTransactionType type,
        Money amount,
        string reason,
        Guid clientTransactionId,
        Guid createdByUserId,
        Guid? authorizedByUserId = null)
    {
        if (cashSessionId == Guid.Empty)
            throw new DomainException("Cash session ID is required.", "INVALID_SESSION_ID");

        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Cash transaction amount must be positive.", "INVALID_AMOUNT");

        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Reason is mandatory for drawer transactions.", "REASON_REQUIRED");

        if (clientTransactionId == Guid.Empty)
            throw new DomainException("Client transaction ID is required for idempotency.", "CLIENT_TX_REQUIRED");

        if (createdByUserId == Guid.Empty)
            throw new DomainException("Created by user ID is required.", "INVALID_USER_ID");

        if ((type == CashTransactionType.PayOut || type == CashTransactionType.CashDrop) && (!authorizedByUserId.HasValue || authorizedByUserId.Value == Guid.Empty))
            throw new DomainException($"Manager/Admin authorization is mandatory for {type}.", "AUTHORIZATION_REQUIRED");

        Id = id;
        CashSessionId = cashSessionId;
        Type = type;
        Amount = amount;
        Reason = reason.Trim();
        ClientTransactionId = clientTransactionId;
        CreatedByUserId = createdByUserId;
        AuthorizedByUserId = authorizedByUserId;
        TimestampUtc = DateTime.UtcNow;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
