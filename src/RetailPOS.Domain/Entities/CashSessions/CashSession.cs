using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.CashSessions;

public sealed class CashSession : BaseEntity<Guid>
{
    public int StoreId { get; private set; }
    public Guid TerminalId { get; private set; }
    public Guid CashierUserId { get; private set; }
    public DateTime OpeningTimeUtc { get; private set; }
    public DateTime? ClosingTimeUtc { get; private set; }
    public Money OpeningFloat { get; private set; }
    public Money TotalCashSales { get; private set; }
    public Money TotalCashRepayments { get; private set; }
    public Money TotalCashRefunds { get; private set; }
    public Money TotalPayIns { get; private set; }
    public Money TotalPayOuts { get; private set; }
    public Money TotalCashDrops { get; private set; }
    public Money? ClosingCash { get; private set; }
    public Money? Discrepancy { get; private set; }
    public CashSessionStatus Status { get; private set; }
    public string? ClosingNotes { get; private set; }
    public bool IsForceClosed { get; private set; }
    public Guid? ClosedByUserId { get; private set; }
    public string? ForceCloseReason { get; private set; }

    public Money ExpectedCash => OpeningFloat + TotalCashSales + TotalCashRepayments + TotalPayIns 
                                 - TotalCashRefunds - TotalPayOuts - TotalCashDrops;

    private CashSession() { }

    public CashSession(
        Guid id,
        int storeId,
        Guid terminalId,
        Guid cashierUserId,
        Money openingFloat)
    {
        if (storeId <= 0)
            throw new DomainException("Store ID must be positive.", "INVALID_STORE_ID");

        if (terminalId == Guid.Empty)
            throw new DomainException("Terminal ID is required.", "INVALID_TERMINAL_ID");

        if (cashierUserId == Guid.Empty)
            throw new DomainException("Cashier user ID is required.", "INVALID_USER_ID");

        if (openingFloat.IsNegative)
            throw new DomainException("Opening float cannot be negative.", "INVALID_OPENING_FLOAT");

        Id = id;
        StoreId = storeId;
        TerminalId = terminalId;
        CashierUserId = cashierUserId;
        OpeningTimeUtc = DateTime.UtcNow;
        OpeningFloat = openingFloat;

        var currency = openingFloat.Currency;
        TotalCashSales = Money.Zero(currency);
        TotalCashRepayments = Money.Zero(currency);
        TotalCashRefunds = Money.Zero(currency);
        TotalPayIns = Money.Zero(currency);
        TotalPayOuts = Money.Zero(currency);
        TotalCashDrops = Money.Zero(currency);

        Status = CashSessionStatus.Open;
        IsForceClosed = false;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    private void EnsureOpen()
    {
        if (Status != CashSessionStatus.Open)
            throw new DomainException("Cash session is closed. No further transactions are allowed.", "SESSION_ALREADY_CLOSED");
    }

    public void AddCashSale(Money amount)
    {
        EnsureOpen();
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Cash sale amount must be positive.", "INVALID_AMOUNT");

        TotalCashSales += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddCashRepayment(Money amount)
    {
        EnsureOpen();
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Cash repayment amount must be positive.", "INVALID_AMOUNT");

        TotalCashRepayments += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddCashRefund(Money amount)
    {
        EnsureOpen();
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Cash refund amount must be positive.", "INVALID_AMOUNT");

        TotalCashRefunds += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddPayIn(Money amount)
    {
        EnsureOpen();
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Pay-in amount must be positive.", "INVALID_AMOUNT");

        TotalPayIns += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddPayOut(Money amount)
    {
        EnsureOpen();
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Pay-out amount must be positive.", "INVALID_AMOUNT");

        TotalPayOuts += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddCashDrop(Money amount)
    {
        EnsureOpen();
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Cash drop amount must be positive.", "INVALID_AMOUNT");

        TotalCashDrops += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Close(Money countedCash, string? notes, Guid closedByUserId)
    {
        EnsureOpen();
        if (countedCash.IsNegative)
            throw new DomainException("Closing cash cannot be negative.", "INVALID_CLOSING_CASH");

        if (closedByUserId == Guid.Empty)
            throw new DomainException("Closed by user ID is required.", "INVALID_USER_ID");

        ClosingCash = countedCash;
        Discrepancy = countedCash - ExpectedCash;
        ClosingNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        ClosedByUserId = closedByUserId;
        ClosingTimeUtc = DateTime.UtcNow;
        Status = CashSessionStatus.Closed;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void ForceClose(Money countedCash, string reason, Guid closedByUserId)
    {
        EnsureOpen();
        if (countedCash.IsNegative)
            throw new DomainException("Closing cash cannot be negative.", "INVALID_CLOSING_CASH");

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            throw new DomainException("A detailed force close reason is mandatory (minimum 10 characters).", "FORCE_CLOSE_REASON_REQUIRED");

        if (closedByUserId == Guid.Empty)
            throw new DomainException("Closed by user ID is required.", "INVALID_USER_ID");

        ClosingCash = countedCash;
        Discrepancy = countedCash - ExpectedCash;
        ForceCloseReason = reason.Trim();
        IsForceClosed = true;
        ClosedByUserId = closedByUserId;
        ClosingTimeUtc = DateTime.UtcNow;
        Status = CashSessionStatus.Closed;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
