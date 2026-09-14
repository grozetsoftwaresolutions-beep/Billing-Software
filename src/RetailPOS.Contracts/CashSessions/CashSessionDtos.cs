using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.CashSessions;

public sealed record OpenCashSessionRequest(
    Guid TerminalId,
    long OpeningFloatPaise,
    string? Notes);

public sealed record CloseCashSessionRequest(
    long CountedCashPaise,
    string? Notes);

public sealed record ForceCloseCashSessionRequest(
    long CountedCashPaise,
    string Reason);

public sealed record AddCashTransactionRequest(
    CashTransactionType Type,
    long AmountPaise,
    string Reason,
    Guid ClientTransactionId);

public sealed record CashSessionDto(
    Guid Id,
    int StoreId,
    Guid TerminalId,
    Guid CashierUserId,
    DateTime OpeningTimeUtc,
    DateTime? ClosingTimeUtc,
    long OpeningFloatPaise,
    long TotalCashSalesPaise,
    long TotalCashRepaymentsPaise,
    long TotalCashRefundsPaise,
    long TotalPayInsPaise,
    long TotalPayOutsPaise,
    long TotalCashDropsPaise,
    long ExpectedCashPaise,
    long? ClosingCashPaise,
    long? DiscrepancyPaise,
    CashSessionStatus Status,
    string? ClosingNotes,
    bool IsForceClosed,
    Guid? ClosedByUserId,
    string? ForceCloseReason);

public sealed record CashSessionTransactionDto(
    long Id,
    Guid CashSessionId,
    CashTransactionType Type,
    long AmountPaise,
    string Reason,
    Guid ClientTransactionId,
    Guid CreatedByUserId,
    Guid? AuthorizedByUserId,
    DateTime TimestampUtc);

public sealed record CashSessionReportDto(
    CashSessionDto Session,
    string CashierName,
    string? ClosedByName,
    string TerminalName,
    IReadOnlyList<CashSessionTransactionDto> Transactions);
