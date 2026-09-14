using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Reports;

// Common Date Range Request Query
public sealed record ReportDateRangeQuery(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    Guid? TerminalId = null,
    Guid? CashierUserId = null,
    int? CategoryId = null,
    Guid? SupplierId = null,
    Guid? CustomerId = null,
    int Page = 1,
    int PageSize = 50);

// 1. Daily Sales Summary
public sealed record DailySalesSummaryReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    int TotalInvoicesCount,
    int TotalItemsSoldCount,
    int VoidedInvoicesCount,
    long VoidedAmountPaise,
    long GrossSubtotalPaise,
    long TotalLineDiscountsPaise,
    long TotalInvoiceDiscountsPaise,
    long TaxableSubtotalPaise,
    long TotalTaxPaise,
    long RoundingAdjustmentPaise,
    long GrossSalesPaise,
    int TotalRefundsCount,
    long TotalRefundsPaise,
    long NetRevenuePaise,
    PaymentTenderSummaryDto PaymentTenders,
    IReadOnlyList<HourlySalesDistributionDto> HourlyDistribution);

public sealed record HourlySalesDistributionDto(int Hour, int InvoiceCount, long NetSalesPaise);

// 2. Product Performance
public sealed record ProductPerformanceReportDto(
    int TotalProductsCount,
    decimal TotalQuantitySold,
    decimal TotalQuantityRefunded,
    decimal TotalNetQuantitySold,
    long TotalGrossSalesPaise,
    long TotalLineDiscountsPaise,
    long TotalRefundAmountPaise,
    long TotalNetSalesPaise,
    long TotalCostOfGoodsSoldPaise,
    long TotalEstimatedGrossProfitPaise,
    decimal OverallMarginPercentage,
    PagedResult<ProductPerformanceLineDto> Items);

public sealed record ProductPerformanceLineDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    string CategoryName,
    string UnitOfMeasure,
    decimal QuantitySold,
    decimal QuantityRefunded,
    decimal NetQuantitySold,
    long GrossSalesPaise,
    long LineDiscountsPaise,
    long RefundAmountPaise,
    long NetSalesPaise,
    long CostOfGoodsSoldPaise,
    long EstimatedGrossProfitPaise,
    decimal MarginPercentage);

// 3. Stock Valuation Report
public sealed record StockValuationReportDto(
    int TotalProductsCount,
    decimal TotalStockUnits,
    long TotalValuationAtCostPaise,
    long TotalValuationAtRetailPaise,
    long PotentialProfitPaise,
    int LowStockItemCount,
    PagedResult<StockValuationItemDto> Items);

public sealed record StockValuationItemDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    string CategoryName,
    string UnitOfMeasure,
    decimal CurrentQuantity,
    decimal ReorderLevel,
    long CostPricePaise,
    long SellingPricePaise,
    long StockValuationAtCostPaise,
    long StockValuationAtRetailPaise,
    bool IsLowStock);

// 4. Stock Movement Ledger
public sealed record StockMovementLedgerReportDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    string UnitOfMeasure,
    decimal OpeningQuantity,
    decimal TotalPurchased,
    decimal TotalSold,
    decimal TotalReturnedByCustomers,
    decimal TotalReturnedToSuppliers,
    decimal TotalAdjustedUp,
    decimal TotalAdjustedDown,
    decimal TotalWastage,
    decimal ClosingQuantity,
    PagedResult<StockMovementDetailLineDto> Movements);

public sealed record StockMovementDetailLineDto(
    long Id,
    DateTime TimestampUtc,
    StockMovementType MovementType,
    decimal QuantityChanged,
    decimal ResultingQuantity,
    string? ReferenceNumber,
    string? Reason,
    string? PerformedBy);

// 5. Purchase Summary
public sealed record PurchaseSummaryReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    int TotalInvoicesCount,
    long TotalPurchasesPaise,
    long TotalPaidPaise,
    long TotalBalanceDuePaise,
    long TotalTaxPaidPaise,
    IReadOnlyList<SupplierPurchaseBreakdownDto> SupplierBreakdown,
    PagedResult<PurchaseInvoiceReportLineDto> Invoices);

public sealed record SupplierPurchaseBreakdownDto(
    Guid SupplierId,
    string SupplierName,
    int InvoiceCount,
    long TotalPurchasesPaise,
    long TotalPaidPaise,
    long BalanceDuePaise);

public sealed record PurchaseInvoiceReportLineDto(
    Guid Id,
    string InvoiceNumber,
    string SupplierInvoiceNumber,
    string SupplierName,
    DateOnly InvoiceDate,
    long GrandTotalPaise,
    long AmountPaidPaise,
    long BalanceDuePaise,
    PurchasePaymentStatus PaymentStatus,
    PurchaseStatus Status);

// 6. Supplier Balances
public sealed record SupplierBalanceReportDto(
    long TotalOutstandingPayablePaise,
    long TotalCreditBalancePaise,
    long NetLiabilityPaise,
    IReadOnlyList<SupplierBalanceLineDto> Suppliers);

public sealed record SupplierBalanceLineDto(
    Guid SupplierId,
    string SupplierName,
    string? Phone,
    string? Gstin,
    int PaymentTermsDays,
    long TotalPurchasesPaise,
    long TotalPaidPaise,
    long BalanceDuePaise,
    long CreditBalancePaise,
    long NetPayablePaise);

// 7. Cashier Performance
public sealed record CashierPerformanceReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<CashierPerformanceLineDto> Cashiers);

public sealed record CashierPerformanceLineDto(
    Guid CashierUserId,
    string Username,
    string FullName,
    int ShiftsWorked,
    int BillsCount,
    long GrossSalesPaise,
    long TotalDiscountsPaise,
    long NetSalesPaise,
    long TotalRefundsApprovedPaise,
    long CashCollectedPaise,
    long NonCashCollectedPaise,
    long TotalDiscrepanciesPaise);

// 8. Current Terminal Operational Snapshot
public sealed record CurrentTerminalReportDto(
    Guid TerminalId,
    string TerminalCode,
    string TerminalName,
    DateTime TimestampUtc,
    Guid? ActiveSessionId,
    string? ActiveCashierUsername,
    DateTime? SessionStartTimeUtc,
    string SessionStatus,
    long OpeningFloatPaise,
    long CurrentExpectedCashPaise,
    long TotalCashSalesPaise,
    long TotalCashRepaymentsPaise,
    long TotalPayInsPaise,
    long TotalCashRefundsPaise,
    long TotalPayOutsPaise,
    long TotalCashDropsPaise,
    int TodayBillsCount,
    long TodayGrossSalesPaise,
    long TodayLineDiscountsPaise,
    long TodayInvoiceDiscountsPaise,
    long TodayTaxPaise,
    long TodayNetSalesPaise,
    int TodayRefundsCount,
    long TodayRefundsPaise,
    PaymentTenderSummaryDto TodayTenders,
    IReadOnlyList<RecentTerminalTransactionDto> RecentTransactions);

public sealed record RecentTerminalTransactionDto(
    DateTime TimestampUtc,
    string TransactionType,
    string ReferenceNumber,
    long AmountPaise,
    string CashierName);

// 9. Payment Tender Breakdown
public sealed record PaymentSummaryReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    PaymentTenderDetailDto SalesTenders,
    PaymentTenderDetailDto RepaymentTenders,
    PaymentTenderDetailDto RefundTenders,
    PaymentTenderDetailDto NetTenders);

public sealed record PaymentTenderDetailDto(
    long CashPaise,
    long CardPaise,
    long UpiPaise,
    long BankTransferPaise,
    long ChequePaise,
    long CreditPaise,
    long TotalPaise);

public sealed record PaymentTenderSummaryDto(
    long CashPaise,
    long CardPaise,
    long UpiPaise,
    long BankTransferPaise,
    long ChequePaise,
    long CreditPaise,
    long TotalPaise);

// 10. Profit & Margin Estimate
public sealed record ProfitMarginEstimateReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    long GrossSalesPaise,
    long LineDiscountsPaise,
    long InvoiceDiscountsPaise,
    long RefundedAmountPaise,
    long NetSalesPaise,
    long CostOfGoodsSoldPaise,
    long RefundedCostOfGoodsSoldPaise,
    long NetCostOfGoodsSoldPaise,
    long EstimatedGrossProfitPaise,
    decimal MarginPercentage,
    bool IsEstimate);

// 11. GSTR-1 Aligned Tax Summary
public sealed record TaxSummaryReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    long B2BTaxableSubtotalPaise,
    long B2CTaxableSubtotalPaise,
    long TotalTaxableSubtotalPaise,
    long TotalTaxCollectedPaise,
    long TotalTaxRefundedPaise,
    long NetTaxPayablePaise,
    IReadOnlyList<TaxCategoryBreakdownDto> Categories,
    IReadOnlyList<TaxComponentBreakdownDto> Components);

public sealed record TaxCategoryBreakdownDto(
    int TaxCategoryId,
    string CategoryName,
    long TaxableAmountPaise,
    long TaxAmountPaise,
    long RefundTaxableAmountPaise,
    long RefundTaxAmountPaise,
    long NetTaxAmountPaise);

public sealed record TaxComponentBreakdownDto(
    string ComponentName,
    decimal RatePercentage,
    long TaxCollectedPaise,
    long TaxRefundedPaise,
    long NetTaxPayablePaise);

// 12. Customer Credit Balances
public sealed record CustomerCreditReportDto(
    int TotalCustomersWithCreditCount,
    long TotalCreditLimitPaise,
    long TotalOutstandingDebtPaise,
    long TotalNetReceivablePaise,
    PagedResult<CustomerCreditLineDto> Customers);

public sealed record CustomerCreditLineDto(
    Guid CustomerId,
    string Name,
    string? Phone,
    string? Gstin,
    long CreditLimitPaise,
    long OutstandingCreditPaise,
    long CreditBalancePaise,
    long AvailableCreditPaise,
    DateTime? LastPaymentDateUtc);

// 13. Audit Trail Activity
public sealed record AuditActivityReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    PagedResult<AuditTrailLineDto> Events);

public sealed record AuditTrailLineDto(
    long Id,
    DateTime TimestampUtc,
    string Module,
    string Action,
    string PerformedBy,
    string Details);
