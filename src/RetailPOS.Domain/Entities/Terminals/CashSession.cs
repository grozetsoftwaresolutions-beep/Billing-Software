using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Terminals;

public sealed class CashSession : BaseEntity<Guid>
{
    public Guid TerminalId { get; private set; }
    public Guid CashierId { get; private set; }
    public DateTime OpenedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public Money OpeningFloat { get; private set; }
    public Money? ClosingCashCounted { get; private set; }
    public Money? ExpectedClosingCash { get; private set; }
    public Money? Discrepancy => ClosingCashCounted.HasValue && ExpectedClosingCash.HasValue
        ? ClosingCashCounted.Value - ExpectedClosingCash.Value
        : null;
    public CashSessionStatus Status { get; private set; }

    private CashSession() { }

    public CashSession(Guid id, Guid terminalId, Guid cashierId, Money openingFloat)
    {
        Id = id;
        TerminalId = terminalId;
        CashierId = cashierId;
        OpenedAtUtc = DateTime.UtcNow;
        OpeningFloat = openingFloat;
        Status = CashSessionStatus.Open;
    }

    public void CloseSession(Money closingCashCounted, Money expectedClosingCash)
    {
        ClosedAtUtc = DateTime.UtcNow;
        ClosingCashCounted = closingCashCounted;
        ExpectedClosingCash = expectedClosingCash;
        Status = CashSessionStatus.Closed;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
