using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Sales;

public sealed record CartItemRequest(
    Guid ProductId,
    decimal Quantity,
    DiscountType DiscountType = DiscountType.None,
    decimal DiscountValue = 0m);

public sealed record CalculateSaleRequest(
    IReadOnlyList<CartItemRequest> Items,
    DiscountType OrderDiscountType = DiscountType.None,
    decimal OrderDiscountValue = 0m,
    PaymentMethod EstimatedPaymentMethod = PaymentMethod.Cash);

public sealed record PaymentLineRequest(
    PaymentMethod Method,
    long AmountAppliedPaise,
    long? TenderedAmountPaise = null,
    string? ReferenceNumber = null);

public sealed record CreateSaleRequest(
    Guid ClientTransactionId,
    IReadOnlyList<CartItemRequest> Items,
    IReadOnlyList<PaymentLineRequest> Payments,
    DiscountType OrderDiscountType = DiscountType.None,
    decimal OrderDiscountValue = 0m,
    Guid? CustomerId = null,
    string? Notes = null);

public sealed record RefundItemRequest(
    long SaleItemId,
    decimal Quantity);

public sealed record RefundSaleRequest(
    Guid ClientTransactionId,
    IReadOnlyList<RefundItemRequest> Items,
    PaymentMethod RefundMethod,
    string Reason,
    string? ReferenceNumber = null);

public sealed record SaleSearchRequest(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    Guid? CashierId = null,
    Guid? TerminalId = null,
    string? InvoiceNumber = null,
    int Page = 1,
    int PageSize = 20);

public sealed record TaxComponentBreakdownDto(
    string ComponentName,
    decimal RatePercentage,
    long AmountPaise);

public sealed record SaleItemDto(
    long Id,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    decimal Quantity,
    string UnitCode,
    long UnitSellingPricePaise,
    long GrossAmountPaise,
    long DiscountAmountPaise,
    long TaxableAmountPaise,
    long TaxAmountPaise,
    long LineTotalPaise,
    IReadOnlyList<TaxComponentBreakdownDto> TaxComponents);

public sealed record PaymentRecordDto(
    Guid Id,
    PaymentMethod Method,
    long AmountPaise,
    long TenderedAmountPaise,
    long ChangeAmountPaise,
    string? ReferenceNumber);

public sealed record SaleInvoiceDto(
    Guid Id,
    string InvoiceNumber,
    Guid ClientTransactionId,
    Guid TerminalId,
    Guid CashierId,
    string CashierName,
    Guid? CustomerId,
    SaleStatus Status,
    DateTime CreatedAtUtc,
    long GrossSubtotalPaise,
    long TotalLineDiscountsPaise,
    long InvoiceDiscountPaise,
    long TaxableSubtotalPaise,
    long TotalTaxPaise,
    long RoundingAdjustmentPaise,
    long FinalPayablePaise,
    IReadOnlyList<SaleItemDto> Items,
    IReadOnlyList<PaymentRecordDto> Payments,
    int ReprintCount = 0,
    DateTime? LastReprintedAtUtc = null,
    RetailPOS.Contracts.Hardware.DrawerKickDirective? DrawerKick = null);

public sealed record SaleSummaryDto(
    Guid Id,
    string InvoiceNumber,
    Guid TerminalId,
    Guid CashierId,
    long FinalPayablePaise,
    SaleStatus Status,
    DateTime CreatedAtUtc,
    int ItemCount,
    string PaymentSummary);

public sealed record CartCalculationDto(
    long GrossSubtotalPaise,
    long TotalLineDiscountsPaise,
    long InvoiceDiscountPaise,
    long TaxableSubtotalPaise,
    long TotalTaxPaise,
    long RoundingAdjustmentPaise,
    long FinalPayablePaise,
    IReadOnlyList<SaleItemDto> Lines,
    IReadOnlyList<TaxComponentBreakdownDto> TaxComponents);

public sealed record SaleRefundItemDto(
    long Id,
    Guid RefundId,
    long SaleItemId,
    Guid ProductId,
    decimal QuantityRefunded,
    long RefundAmountPaise,
    long TaxRefundAmountPaise);

public sealed record SaleRefundDto(
    Guid Id,
    Guid SaleId,
    string InvoiceNumber,
    string RefundNumber,
    Guid ClientTransactionId,
    Guid AuthorizedByUserId,
    string AuthorizedByUserName,
    Guid TerminalId,
    PaymentMethod RefundMethod,
    long TotalRefundAmountPaise,
    string Reason,
    DateTime CreatedAtUtc,
    IReadOnlyList<SaleRefundItemDto> Items,
    RetailPOS.Contracts.Hardware.DrawerKickDirective? DrawerKick = null);
