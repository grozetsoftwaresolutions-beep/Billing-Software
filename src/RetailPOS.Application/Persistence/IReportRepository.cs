using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;

namespace RetailPOS.Application.Persistence;

public interface IReportRepository
{
    Task<DailySalesSummaryReportDto> GetDailySalesSummaryAsync(DateTime fromUtc, DateTime toUtc, Guid? terminalId, CancellationToken ct = default);
    Task<ProductPerformanceReportDto> GetProductPerformanceAsync(DateTime fromUtc, DateTime toUtc, int? categoryId, Guid? productId, int page, int pageSize, CancellationToken ct = default);
    Task<StockValuationReportDto> GetStockValuationAsync(int? categoryId, bool lowStockOnly, string? searchTerm, int page, int pageSize, bool maskCost = false, CancellationToken ct = default);
    Task<StockMovementLedgerReportDto> GetStockMovementLedgerAsync(Guid productId, DateTime fromUtc, DateTime toUtc, int page, int pageSize, CancellationToken ct = default);
    Task<PurchaseSummaryReportDto> GetPurchaseSummaryAsync(DateTime fromUtc, DateTime toUtc, Guid? supplierId, int page, int pageSize, CancellationToken ct = default);
    Task<SupplierBalanceReportDto> GetSupplierBalancesAsync(CancellationToken ct = default);
    Task<CashierPerformanceReportDto> GetCashierPerformanceAsync(DateTime fromUtc, DateTime toUtc, Guid? cashierUserId, CancellationToken ct = default);
    Task<CurrentTerminalReportDto> GetCurrentTerminalReportAsync(Guid terminalId, TimeZoneInfo storeTimeZone, CancellationToken ct = default);
    Task<PaymentSummaryReportDto> GetPaymentSummaryAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<ProfitMarginEstimateReportDto> GetProfitEstimateAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<TaxSummaryReportDto> GetTaxSummaryAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<CustomerCreditReportDto> GetCustomerCreditSummaryAsync(int page, int pageSize, CancellationToken ct = default);
    Task<AuditActivityReportDto> GetAuditActivityAsync(DateTime fromUtc, DateTime toUtc, string? moduleName, string? action, string? performedBy, int page, int pageSize, CancellationToken ct = default);
}
