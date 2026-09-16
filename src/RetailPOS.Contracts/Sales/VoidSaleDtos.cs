using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Sales;

public sealed record VoidSaleRequest(
    string Reason,
    string? SupervisorApprovalToken = null,
    string? RequestNonce = null,
    string? SupervisorUsername = null,
    string? SupervisorPin = null,
    string? SupervisorPassword = null);

public sealed record VoidSaleResponse(
    Guid SaleId,
    string InvoiceNumber,
    SaleStatus Status,
    DateTime VoidedAtUtc,
    string VoidedByUsername,
    string Reason,
    bool StockReversed,
    long CashReversedPaise);
