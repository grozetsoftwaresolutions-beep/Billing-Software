using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Purchases;

public sealed record PurchaseTaxComponentInputDto(
    string ComponentName,
    decimal RatePercentage);

public sealed record PurchaseTaxComponentDto(
    string ComponentName,
    decimal RatePercentage,
    long TaxAmountPaise);

public sealed record CreatePurchaseItemRequest(
    Guid ProductId,
    decimal Quantity,
    long UnitCostPaise,
    List<PurchaseTaxComponentInputDto> TaxComponents,
    bool UpdateMasterCostPrice = true,
    string? BatchNumber = null,
    DateOnly? ExpiryDate = null);

public sealed record CreatePurchaseRequest(
    Guid SupplierId,
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    List<CreatePurchaseItemRequest> Items,
    long InitialPaymentAmountPaise = 0,
    PaymentMethod? InitialPaymentMethod = null,
    string? InitialPaymentReference = null,
    string? Notes = null,
    Guid ClientTransactionId = default);

public sealed record PurchaseItemDto(
    long Id,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    string UnitOfMeasure,
    decimal Quantity,
    long UnitCostPaise,
    decimal TaxRatePercentage,
    long TaxAmountPaise,
    long LineTotalPaise,
    string? BatchNumber,
    DateOnly? ExpiryDate,
    IReadOnlyList<PurchaseTaxComponentDto> TaxComponents);

public sealed record PurchaseInvoiceDto(
    Guid Id,
    int StoreId,
    string InvoiceNumber,
    string SupplierInvoiceNumber,
    Guid SupplierId,
    string SupplierName,
    DateOnly InvoiceDate,
    DateTime ReceivedDateUtc,
    long SubtotalPaise,
    long TotalTaxPaise,
    long GrandTotalPaise,
    long CreditOffsetAmountPaise,
    long AmountPaidPaise,
    long BalanceDuePaise,
    PurchasePaymentStatus PaymentStatus,
    PurchaseStatus Status,
    string? Notes,
    Guid CreatedByUserId,
    DateTime CreatedAtUtc,
    IReadOnlyList<PurchaseItemDto> Items);

public sealed record CancelPurchaseRequest(
    string Reason,
    RefundSettlementMode SettlementMode,
    PaymentMethod? PayoutMethod = null,
    string? PayoutReference = null,
    Guid ClientTransactionId = default);

public sealed record PurchaseCancellationResultDto(
    Guid PurchaseId,
    string InvoiceNumber,
    PurchaseStatus Status,
    RefundSettlementMode SettlementMode,
    long RefundAmountPaise,
    string Reason,
    DateTime CancelledAtUtc);
