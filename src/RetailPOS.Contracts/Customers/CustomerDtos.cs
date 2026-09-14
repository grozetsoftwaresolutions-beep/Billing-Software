using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Customers;

public sealed record CustomerDto(
    Guid Id,
    string Name,
    string? Phone,
    string? Email,
    string? Address,
    string? Gstin,
    long OutstandingCreditPaise,
    long CreditBalancePaise,
    long CreditLimitPaise,
    bool IsActive,
    string? Notes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CreateCustomerRequest(
    string Name,
    string? Phone,
    string? Email,
    string? Address,
    string? Gstin,
    long CreditLimitPaise,
    string? Notes);

public sealed record UpdateCustomerRequest(
    string Name,
    string? Phone,
    string? Email,
    string? Address,
    string? Gstin,
    string? Notes);

public sealed record UpdateCreditLimitRequest(
    long CreditLimitPaise,
    string Reason);

public sealed record SetCustomerStatusRequest(
    bool IsActive,
    string Reason);

public sealed record RecordCustomerPaymentRequest(
    long AmountPaise,
    PaymentMethod Method,
    string? ReferenceNumber,
    string? Notes,
    Guid ClientTransactionId);

public sealed record CustomerPaymentDto(
    Guid Id,
    Guid CustomerId,
    long AmountPaise,
    PaymentMethod Method,
    string? ReferenceNumber,
    DateTime PaymentDateUtc,
    Guid ClientTransactionId,
    string? Notes,
    Guid CreatedByUserId,
    DateTime CreatedAtUtc,
    RetailPOS.Contracts.Hardware.DrawerKickDirective? DrawerKick = null);

public sealed record CustomerLedgerEntryDto(
    DateTime TimestampUtc,
    string EntryType,
    string ReferenceNumber,
    long DebtChangePaise,
    long StoreCreditChangePaise,
    long RunningOutstandingCreditPaise,
    long RunningCreditBalancePaise,
    string? Notes);
