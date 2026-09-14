using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public interface IReportService
{
    Task<ApiResponse<DailySalesSummaryReportDto>> GetDailySalesSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<ProductPerformanceReportDto>> GetProductPerformanceAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<StockValuationReportDto>> GetStockValuationAsync(ReportDateRangeQuery query, bool lowStockOnly, string? searchTerm, RoleType executingRole, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<StockMovementLedgerReportDto>> GetStockMovementLedgerAsync(Guid productId, ReportDateRangeQuery query, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<PurchaseSummaryReportDto>> GetPurchaseSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<SupplierBalanceReportDto>> GetSupplierBalancesAsync(string executingUser, CancellationToken ct = default);
    Task<ApiResponse<CashierPerformanceReportDto>> GetCashierPerformanceAsync(ReportDateRangeQuery query, RoleType executingRole, Guid executingUserId, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<CurrentTerminalReportDto>> GetCurrentTerminalReportAsync(Guid terminalId, RoleType executingRole, Guid? requestingTerminalId, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<PaymentSummaryReportDto>> GetPaymentSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<ProfitMarginEstimateReportDto>> GetProfitEstimateAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<TaxSummaryReportDto>> GetTaxSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<CustomerCreditReportDto>> GetCustomerCreditSummaryAsync(int page, int pageSize, string executingUser, CancellationToken ct = default);
    Task<ApiResponse<AuditActivityReportDto>> GetAuditActivityAsync(ReportDateRangeQuery query, string? moduleName, string? action, string? performedBy, string executingUser, CancellationToken ct = default);
    Task WriteCsvAsync(string reportType, ReportDateRangeQuery query, Stream outputStream, RoleType executingRole, Guid executingUserId, string executingUser, Guid? terminalId, CancellationToken ct = default);
}
