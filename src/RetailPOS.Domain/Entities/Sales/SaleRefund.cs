using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Sales;

public sealed class SaleRefund : BaseEntity<Guid>
{
    public Guid SaleId { get; private set; }
    public string RefundNumber { get; private set; } = null!;
    public Guid ClientTransactionId { get; private set; }
    public Guid AuthorizedByUserId { get; private set; }
    public string AuthorizedByUserName { get; private set; } = null!;
    public Guid TerminalId { get; private set; }
    public PaymentMethod RefundMethod { get; private set; }
    public Money TotalRefundAmount { get; private set; }
    public string Reason { get; private set; } = null!;

    private readonly List<SaleRefundItem> _items = [];
    public IReadOnlyList<SaleRefundItem> Items => _items.AsReadOnly();

    private SaleRefund() { }

    public SaleRefund(
        Guid id,
        Guid saleId,
        string refundNumber,
        Guid clientTransactionId,
        Guid authorizedByUserId,
        string authorizedByUserName,
        Guid terminalId,
        PaymentMethod refundMethod,
        Money totalRefundAmount,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(refundNumber))
            throw new DomainException("Refund number cannot be empty.", "INVALID_REFUND_NUMBER");

        if (string.IsNullOrWhiteSpace(authorizedByUserName))
            throw new DomainException("Authorizing user name cannot be empty.", "INVALID_AUTHORIZING_USER");

        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Refund reason is mandatory.", "REFUND_REASON_REQUIRED");

        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > 250)
            throw new DomainException("Refund reason cannot exceed 250 characters.", "REFUND_REASON_TOO_LONG");

        if (totalRefundAmount.MinorUnits <= 0)
            throw new DomainException("Refund amount must be greater than zero.", "INVALID_REFUND_AMOUNT");

        Id = id;
        SaleId = saleId;
        RefundNumber = refundNumber.Trim().ToUpperInvariant();
        ClientTransactionId = clientTransactionId;
        AuthorizedByUserId = authorizedByUserId;
        AuthorizedByUserName = authorizedByUserName.Trim();
        TerminalId = terminalId;
        RefundMethod = refundMethod;
        TotalRefundAmount = totalRefundAmount;
        Reason = trimmedReason;
    }

    public void AddItem(SaleRefundItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }
}
