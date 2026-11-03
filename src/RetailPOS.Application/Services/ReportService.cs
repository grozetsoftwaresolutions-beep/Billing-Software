using System.Globalization;
using System.Text;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public sealed class ReportService : IReportService
{
    private readonly IReportRepository _reportRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly IAuditLogRepository _auditRepo;

    public ReportService(
        IReportRepository reportRepo,
        IStoreRepository storeRepo,
        IAuditLogRepository auditRepo)
    {
        _reportRepo = reportRepo;
        _storeRepo = storeRepo;
        _auditRepo = auditRepo;
    }

    private async Task<(DateTime StartUtc, DateTime EndUtc, TimeZoneInfo StoreTz, string? ErrorCode, string? ErrorMessage)> ResolveDateRangeAsync(
        DateTime? fromDateInput, DateTime? toDateInput, CancellationToken ct)
    {
        var store = await _storeRepo.GetStoreAsync(ct);
        var tzId = store?.TimeZoneId ?? "India Standard Time";
        TimeZoneInfo storeTz;
        try
        {
            storeTz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            storeTz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, storeTz);
        var todayLocal = DateOnly.FromDateTime(nowLocal);

        var fromDate = fromDateInput.HasValue ? DateOnly.FromDateTime(fromDateInput.Value) : todayLocal;
        var toDate = toDateInput.HasValue ? DateOnly.FromDateTime(toDateInput.Value) : fromDate;

        if (fromDate > toDate)
        {
            return (default, default, storeTz, "INVALID_DATE_RANGE", "FromDate cannot be later than ToDate.");
        }

        var startLocal = fromDate.ToDateTime(TimeOnly.MinValue);
        var endLocal = toDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, storeTz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, storeTz);

        return (startUtc, endUtc, storeTz, null, null);
    }

    public async Task<ApiResponse<DailySalesSummaryReportDto>> GetDailySalesSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<DailySalesSummaryReportDto>(errMsg!, errCode);

        var report = await _reportRepo.GetDailySalesSummaryAsync(startUtc, endUtc, query.TerminalId, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=daily-sales; FromUtc={startUtc:s}; ToUtc={endUtc:s}; TerminalId={query.TerminalId}; InvoicesCount={report.TotalInvoicesCount}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<ProductPerformanceReportDto>> GetProductPerformanceAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<ProductPerformanceReportDto>(errMsg!, errCode);

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);

        var report = await _reportRepo.GetProductPerformanceAsync(startUtc, endUtc, query.CategoryId, null, page, pageSize, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=product-sales; FromUtc={startUtc:s}; ToUtc={endUtc:s}; CategoryId={query.CategoryId}; TotalProducts={report.TotalProductsCount}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<StockValuationReportDto>> GetStockValuationAsync(ReportDateRangeQuery query, bool lowStockOnly, string? searchTerm, RoleType executingRole, string executingUser, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var maskCost = executingRole == RoleType.Cashier;

        var report = await _reportRepo.GetStockValuationAsync(query.CategoryId, lowStockOnly, searchTerm, page, pageSize, maskCost, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=stock; CategoryId={query.CategoryId}; LowStockOnly={lowStockOnly}; TotalProducts={report.TotalProductsCount}; MaskedCost={maskCost}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<StockMovementLedgerReportDto>> GetStockMovementLedgerAsync(Guid productId, ReportDateRangeQuery query, string executingUser, CancellationToken ct = default)
    {
        if (productId == Guid.Empty)
            return ApiResponse.Fail<StockMovementLedgerReportDto>("ProductId is required for stock ledger.", "INVALID_PRODUCT_ID");

        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<StockMovementLedgerReportDto>(errMsg!, errCode);

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);

        var report = await _reportRepo.GetStockMovementLedgerAsync(productId, startUtc, endUtc, page, pageSize, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=stock-movement; ProductId={productId}; FromUtc={startUtc:s}; ToUtc={endUtc:s}; MovementsCount={report.Movements.TotalCount}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<PurchaseSummaryReportDto>> GetPurchaseSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<PurchaseSummaryReportDto>(errMsg!, errCode);

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);

        var report = await _reportRepo.GetPurchaseSummaryAsync(startUtc, endUtc, query.SupplierId, page, pageSize, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=purchases; FromUtc={startUtc:s}; ToUtc={endUtc:s}; SupplierId={query.SupplierId}; TotalInvoices={report.TotalInvoicesCount}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<SupplierBalanceReportDto>> GetSupplierBalancesAsync(string executingUser, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetSupplierBalancesAsync(ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=supplier-balances; TotalSuppliers={report.Suppliers.Count}; NetLiabilityPaise={report.NetLiabilityPaise}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<CashierPerformanceReportDto>> GetCashierPerformanceAsync(
        ReportDateRangeQuery query, RoleType executingRole, Guid executingUserId, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<CashierPerformanceReportDto>(errMsg!, errCode);

        Guid? targetCashierId = query.CashierUserId;
        if (executingRole == RoleType.Cashier)
        {
            if (query.CashierUserId.HasValue && query.CashierUserId.Value != executingUserId)
            {
                return ApiResponse.Fail<CashierPerformanceReportDto>("Cashiers are only authorized to view their own summary.", "FORBIDDEN");
            }
            targetCashierId = executingUserId;
        }

        var report = await _reportRepo.GetCashierPerformanceAsync(startUtc, endUtc, targetCashierId, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=cashier-summary; FromUtc={startUtc:s}; ToUtc={endUtc:s}; TargetCashierId={targetCashierId}; CashiersCount={report.Cashiers.Count}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<CurrentTerminalReportDto>> GetCurrentTerminalReportAsync(
        Guid terminalId, RoleType executingRole, Guid? requestingTerminalId, string executingUser, CancellationToken ct = default)
    {
        if (terminalId == Guid.Empty)
            return ApiResponse.Fail<CurrentTerminalReportDto>("TerminalId is required.", "INVALID_TERMINAL_ID");

        if (executingRole == RoleType.Cashier && requestingTerminalId.HasValue && requestingTerminalId.Value != terminalId)
        {
            return ApiResponse.Fail<CurrentTerminalReportDto>("Cashiers are only authorized to view their assigned terminal.", "FORBIDDEN");
        }

        var store = await _storeRepo.GetStoreAsync(ct);
        var tzId = store?.TimeZoneId ?? "India Standard Time";
        TimeZoneInfo storeTz;
        try
        {
            storeTz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            storeTz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }

        var report = await _reportRepo.GetCurrentTerminalReportAsync(terminalId, storeTz, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=current-terminal; TerminalId={terminalId}; ActiveSessionId={report.ActiveSessionId}; ExpectedCashPaise={report.CurrentExpectedCashPaise}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<PaymentSummaryReportDto>> GetPaymentSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<PaymentSummaryReportDto>(errMsg!, errCode);

        var report = await _reportRepo.GetPaymentSummaryAsync(startUtc, endUtc, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=payments; FromUtc={startUtc:s}; ToUtc={endUtc:s}; TotalSalesPaise={report.SalesTenders.TotalPaise}; NetTendersPaise={report.NetTenders.TotalPaise}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<ProfitMarginEstimateReportDto>> GetProfitEstimateAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<ProfitMarginEstimateReportDto>(errMsg!, errCode);

        var report = await _reportRepo.GetProfitEstimateAsync(startUtc, endUtc, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=profit-estimate; FromUtc={startUtc:s}; ToUtc={endUtc:s}; NetSalesPaise={report.NetSalesPaise}; GrossProfitPaise={report.EstimatedGrossProfitPaise}; MarginPercent={report.MarginPercentage}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<TaxSummaryReportDto>> GetTaxSummaryAsync(ReportDateRangeQuery query, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<TaxSummaryReportDto>(errMsg!, errCode);

        var report = await _reportRepo.GetTaxSummaryAsync(startUtc, endUtc, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=tax-summary; FromUtc={startUtc:s}; ToUtc={endUtc:s}; TotalTaxCollectedPaise={report.TotalTaxCollectedPaise}; NetTaxPayablePaise={report.NetTaxPayablePaise}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<CustomerCreditReportDto>> GetCustomerCreditSummaryAsync(int page, int pageSize, string executingUser, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var report = await _reportRepo.GetCustomerCreditSummaryAsync(page, pageSize, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=customer-balances; TotalCustomers={report.TotalCustomersWithCreditCount}; OutstandingDebtPaise={report.TotalOutstandingDebtPaise}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<AuditActivityReportDto>> GetAuditActivityAsync(ReportDateRangeQuery query, string? moduleName, string? action, string? performedBy, string executingUser, CancellationToken ct = default)
    {
        var (startUtc, endUtc, _, errCode, errMsg) = await ResolveDateRangeAsync(query.FromDate, query.ToDate, ct);
        if (errCode != null)
            return ApiResponse.Fail<AuditActivityReportDto>(errMsg!, errCode);

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);

        var report = await _reportRepo.GetAuditActivityAsync(startUtc, endUtc, moduleName, action, performedBy, page, pageSize, ct);
        await _auditRepo.RecordAsync("Reports", "REPORT_GENERATED", executingUser,
            $"ReportType=audit-activity; FromUtc={startUtc:s}; ToUtc={endUtc:s}; EventsCount={report.Events.TotalCount}", ct);

        return ApiResponse.Ok(report);
    }

    public async Task WriteCsvAsync(
        string reportType,
        ReportDateRangeQuery query,
        Stream outputStream,
        RoleType executingRole,
        Guid executingUserId,
        string executingUser,
        Guid? terminalId,
        CancellationToken ct = default)
    {
        var normalizedType = reportType.Trim().ToLowerInvariant();
        var validTypes = new HashSet<string>
        {
            "daily-sales", "product-sales", "stock", "stock-movement", "purchases",
            "supplier-balances", "cashier-summary", "current-terminal", "payments",
            "profit-estimate", "tax-summary", "customer-balances", "audit-activity"
        };

        if (!validTypes.Contains(normalizedType))
            throw new DomainException($"Invalid report type '{reportType}' for CSV export.", "INVALID_REPORT_TYPE");

        // Role-based restrictions mirror JSON matrix
        if (executingRole == RoleType.Cashier)
        {
            var forbiddenForCashier = new HashSet<string>
            {
                "product-sales", "stock-movement", "purchases", "supplier-balances",
                "profit-estimate", "tax-summary", "audit-activity"
            };

            if (forbiddenForCashier.Contains(normalizedType))
                throw new DomainException("Cashiers are not authorized to export this financial report.", "FORBIDDEN");

            if (normalizedType == "cashier-summary" && query.CashierUserId.HasValue && query.CashierUserId.Value != executingUserId)
                throw new DomainException("Cashiers are only authorized to view their own summary.", "FORBIDDEN");

            if (normalizedType == "daily-sales")
            {
                if (!terminalId.HasValue || terminalId.Value == Guid.Empty)
                    throw new DomainException("Cashier session is not bound to a registered terminal.", "TERMINAL_REQUIRED");

                if (query.TerminalId.HasValue && query.TerminalId.Value != terminalId.Value)
                    throw new DomainException("Cashiers are only authorized to view daily sales for their own terminal.", "FORBIDDEN");

                query = query with { TerminalId = terminalId.Value };
            }
        }

        using var writer = new StreamWriter(outputStream, new UTF8Encoding(true), bufferSize: 4096, leaveOpen: true);

        switch (normalizedType)
        {
            case "daily-sales":
                var salesRes = await GetDailySalesSummaryAsync(query, executingUser, ct);
                if (!salesRes.Success) throw new DomainException(salesRes.ErrorMessage!, salesRes.ErrorCode!);
                var s = salesRes.Data!;
                await writer.WriteLineAsync("Metric,Value");
                await writer.WriteLineAsync($"FromDate,{s.FromUtc:yyyy-MM-dd HH:mm:ss}");
                await writer.WriteLineAsync($"ToDate,{s.ToUtc:yyyy-MM-dd HH:mm:ss}");
                await writer.WriteLineAsync($"TotalInvoices,{s.TotalInvoicesCount}");
                await writer.WriteLineAsync($"TotalItemsSold,{s.TotalItemsSoldCount}");
                await writer.WriteLineAsync($"VoidedInvoices,{s.VoidedInvoicesCount}");
                await writer.WriteLineAsync($"VoidedAmount,{s.VoidedAmountPaise / 100.0:F2}");
                await writer.WriteLineAsync($"GrossSubtotal,{s.GrossSubtotalPaise / 100.0:F2}");
                await writer.WriteLineAsync($"LineDiscounts,{s.TotalLineDiscountsPaise / 100.0:F2}");
                await writer.WriteLineAsync($"InvoiceDiscounts,{s.TotalInvoiceDiscountsPaise / 100.0:F2}");
                await writer.WriteLineAsync($"TaxableSubtotal,{s.TaxableSubtotalPaise / 100.0:F2}");
                await writer.WriteLineAsync($"TotalTax,{s.TotalTaxPaise / 100.0:F2}");
                await writer.WriteLineAsync($"RoundingAdjustment,{s.RoundingAdjustmentPaise / 100.0:F2}");
                await writer.WriteLineAsync($"GrossSales,{s.GrossSalesPaise / 100.0:F2}");
                await writer.WriteLineAsync($"TotalRefundsCount,{s.TotalRefundsCount}");
                await writer.WriteLineAsync($"TotalRefundsAmount,{s.TotalRefundsPaise / 100.0:F2}");
                await writer.WriteLineAsync($"NetRevenue,{s.NetRevenuePaise / 100.0:F2}");
                break;

            case "product-sales":
                var prodRes = await GetProductPerformanceAsync(query, executingUser, ct);
                if (!prodRes.Success) throw new DomainException(prodRes.ErrorMessage!, prodRes.ErrorCode!);
                await writer.WriteLineAsync("SKU,ProductName,Category,UOM,QuantitySold,QuantityRefunded,NetQuantitySold,GrossSales,LineDiscounts,RefundAmount,NetSales,COGS,EstimatedGrossProfit,MarginPercentage");
                foreach (var line in prodRes.Data!.Items.Items)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        EscapeCsv(line.Sku),
                        EscapeCsv(line.ProductName),
                        EscapeCsv(line.CategoryName),
                        EscapeCsv(line.UnitOfMeasure),
                        line.QuantitySold.ToString(CultureInfo.InvariantCulture),
                        line.QuantityRefunded.ToString(CultureInfo.InvariantCulture),
                        line.NetQuantitySold.ToString(CultureInfo.InvariantCulture),
                        (line.GrossSalesPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (line.LineDiscountsPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (line.RefundAmountPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (line.NetSalesPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (line.CostOfGoodsSoldPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (line.EstimatedGrossProfitPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        line.MarginPercentage.ToString("F2", CultureInfo.InvariantCulture)));
                }
                break;

            case "stock":
                var stockRes = await GetStockValuationAsync(query, false, null, executingRole, executingUser, ct);
                if (!stockRes.Success) throw new DomainException(stockRes.ErrorMessage!, stockRes.ErrorCode!);
                await writer.WriteLineAsync("SKU,ProductName,Category,UOM,CurrentQuantity,ReorderLevel,CostPrice,SellingPrice,ValuationAtCost,ValuationAtRetail,IsLowStock");
                foreach (var item in stockRes.Data!.Items.Items)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        EscapeCsv(item.Sku),
                        EscapeCsv(item.ProductName),
                        EscapeCsv(item.CategoryName),
                        EscapeCsv(item.UnitOfMeasure),
                        item.CurrentQuantity.ToString(CultureInfo.InvariantCulture),
                        item.ReorderLevel.ToString(CultureInfo.InvariantCulture),
                        (item.CostPricePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (item.SellingPricePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (item.StockValuationAtCostPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (item.StockValuationAtRetailPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        item.IsLowStock ? "Yes" : "No"));
                }
                break;

            case "stock-movement":
                var pId = query.TerminalId ?? query.SupplierId ?? query.CustomerId ?? Guid.Empty;
                var smRes = await GetStockMovementLedgerAsync(pId, query, executingUser, ct);
                if (!smRes.Success) throw new DomainException(smRes.ErrorMessage!, smRes.ErrorCode!);
                await writer.WriteLineAsync("TimestampUtc,MovementType,QuantityChanged,ResultingQuantity,ReferenceNumber,Reason,PerformedBy");
                foreach (var m in smRes.Data!.Movements.Items)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        m.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        m.MovementType.ToString(),
                        m.QuantityChanged.ToString(CultureInfo.InvariantCulture),
                        m.ResultingQuantity.ToString(CultureInfo.InvariantCulture),
                        EscapeCsv(m.ReferenceNumber),
                        EscapeCsv(m.Reason),
                        EscapeCsv(m.PerformedBy)));
                }
                break;

            case "purchases":
                var purchRes = await GetPurchaseSummaryAsync(query, executingUser, ct);
                if (!purchRes.Success) throw new DomainException(purchRes.ErrorMessage!, purchRes.ErrorCode!);
                await writer.WriteLineAsync("InvoiceNumber,SupplierInvoiceNumber,SupplierName,InvoiceDate,GrandTotal,AmountPaid,BalanceDue,PaymentStatus,Status");
                foreach (var inv in purchRes.Data!.Invoices.Items)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        EscapeCsv(inv.InvoiceNumber),
                        EscapeCsv(inv.SupplierInvoiceNumber),
                        EscapeCsv(inv.SupplierName),
                        inv.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        (inv.GrandTotalPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (inv.AmountPaidPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (inv.BalanceDuePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        inv.PaymentStatus.ToString(),
                        inv.Status.ToString()));
                }
                break;

            case "supplier-balances":
                var supRes = await GetSupplierBalancesAsync(executingUser, ct);
                if (!supRes.Success) throw new DomainException(supRes.ErrorMessage!, supRes.ErrorCode!);
                await writer.WriteLineAsync("SupplierName,Phone,GSTIN,PaymentTermsDays,TotalPurchases,TotalPaid,BalanceDue,CreditBalance,NetPayable");
                foreach (var sup in supRes.Data!.Suppliers)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        EscapeCsv(sup.SupplierName),
                        EscapeCsv(sup.Phone),
                        EscapeCsv(sup.Gstin),
                        sup.PaymentTermsDays.ToString(CultureInfo.InvariantCulture),
                        (sup.TotalPurchasesPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (sup.TotalPaidPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (sup.BalanceDuePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (sup.CreditBalancePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (sup.NetPayablePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture)));
                }
                break;

            case "cashier-summary":
                var cRes = await GetCashierPerformanceAsync(query, executingRole, executingUserId, executingUser, ct);
                if (!cRes.Success) throw new DomainException(cRes.ErrorMessage!, cRes.ErrorCode!);
                await writer.WriteLineAsync("Username,FullName,ShiftsWorked,BillsCount,GrossSales,TotalDiscounts,NetSales,TotalRefundsApproved,CashCollected,NonCashCollected,TotalDiscrepancies");
                foreach (var c in cRes.Data!.Cashiers)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        EscapeCsv(c.Username),
                        EscapeCsv(c.FullName),
                        c.ShiftsWorked.ToString(CultureInfo.InvariantCulture),
                        c.BillsCount.ToString(CultureInfo.InvariantCulture),
                        (c.GrossSalesPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (c.TotalDiscountsPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (c.NetSalesPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (c.TotalRefundsApprovedPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (c.CashCollectedPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (c.NonCashCollectedPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (c.TotalDiscrepanciesPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture)));
                }
                break;

            case "current-terminal":
                var termId = query.TerminalId ?? terminalId ?? Guid.Empty;
                var termRes = await GetCurrentTerminalReportAsync(termId, executingRole, terminalId, executingUser, ct);
                if (!termRes.Success) throw new DomainException(termRes.ErrorMessage!, termRes.ErrorCode!);
                var tr = termRes.Data!;
                await writer.WriteLineAsync("Metric,Value");
                await writer.WriteLineAsync($"TerminalCode,{EscapeCsv(tr.TerminalCode)}");
                await writer.WriteLineAsync($"TerminalName,{EscapeCsv(tr.TerminalName)}");
                await writer.WriteLineAsync($"ActiveCashier,{EscapeCsv(tr.ActiveCashierUsername)}");
                await writer.WriteLineAsync($"OpeningFloat,{tr.OpeningFloatPaise / 100.0:F2}");
                await writer.WriteLineAsync($"ExpectedCash,{tr.CurrentExpectedCashPaise / 100.0:F2}");
                await writer.WriteLineAsync($"TodayBillsCount,{tr.TodayBillsCount}");
                await writer.WriteLineAsync($"TodayNetSales,{tr.TodayNetSalesPaise / 100.0:F2}");
                await writer.WriteLineAsync($"TodayRefunds,{tr.TodayRefundsPaise / 100.0:F2}");
                break;

            case "payments":
                var payRes = await GetPaymentSummaryAsync(query, executingUser, ct);
                if (!payRes.Success) throw new DomainException(payRes.ErrorMessage!, payRes.ErrorCode!);
                var p = payRes.Data!;
                await writer.WriteLineAsync("Category,Cash,Card,UPI,BankTransfer,Cheque,Credit,Total");
                await WritePaymentRow(writer, "SalesInflow", p.SalesTenders);
                await WritePaymentRow(writer, "RepaymentInflow", p.RepaymentTenders);
                await WritePaymentRow(writer, "RefundOutflow", p.RefundTenders);
                await WritePaymentRow(writer, "NetTenders", p.NetTenders);
                break;

            case "profit-estimate":
                var profRes = await GetProfitEstimateAsync(query, executingUser, ct);
                if (!profRes.Success) throw new DomainException(profRes.ErrorMessage!, profRes.ErrorCode!);
                var pe = profRes.Data!;
                await writer.WriteLineAsync("Metric,Value");
                await writer.WriteLineAsync($"GrossSales,{pe.GrossSalesPaise / 100.0:F2}");
                await writer.WriteLineAsync($"LineDiscounts,{pe.LineDiscountsPaise / 100.0:F2}");
                await writer.WriteLineAsync($"InvoiceDiscounts,{pe.InvoiceDiscountsPaise / 100.0:F2}");
                await writer.WriteLineAsync($"RefundedAmount,{pe.RefundedAmountPaise / 100.0:F2}");
                await writer.WriteLineAsync($"NetSales,{pe.NetSalesPaise / 100.0:F2}");
                await writer.WriteLineAsync($"GrossCOGS,{pe.CostOfGoodsSoldPaise / 100.0:F2}");
                await writer.WriteLineAsync($"RefundedCOGS,{pe.RefundedCostOfGoodsSoldPaise / 100.0:F2}");
                await writer.WriteLineAsync($"NetCOGS,{pe.NetCostOfGoodsSoldPaise / 100.0:F2}");
                await writer.WriteLineAsync($"EstimatedGrossProfit,{pe.EstimatedGrossProfitPaise / 100.0:F2}");
                await writer.WriteLineAsync($"MarginPercentage,{pe.MarginPercentage:F2}%");
                break;

            case "tax-summary":
                var taxRes = await GetTaxSummaryAsync(query, executingUser, ct);
                if (!taxRes.Success) throw new DomainException(taxRes.ErrorMessage!, taxRes.ErrorCode!);
                var ts = taxRes.Data!;
                await writer.WriteLineAsync("Category/Component,TaxableAmount,TaxAmount,RefundTaxable,RefundTax,NetTax");
                foreach (var cat in ts.Categories)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        EscapeCsv(cat.CategoryName),
                        (cat.TaxableAmountPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (cat.TaxAmountPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (cat.RefundTaxableAmountPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (cat.RefundTaxAmountPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (cat.NetTaxAmountPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture)));
                }
                break;

            case "customer-balances":
                var custRes = await GetCustomerCreditSummaryAsync(query.Page, query.PageSize, executingUser, ct);
                if (!custRes.Success) throw new DomainException(custRes.ErrorMessage!, custRes.ErrorCode!);
                await writer.WriteLineAsync("CustomerName,Phone,GSTIN,CreditLimit,OutstandingCredit,StoreCredit,AvailableCredit,LastPaymentDate");
                foreach (var cust in custRes.Data!.Customers.Items)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        EscapeCsv(cust.Name),
                        EscapeCsv(cust.Phone),
                        EscapeCsv(cust.Gstin),
                        (cust.CreditLimitPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (cust.OutstandingCreditPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (cust.CreditBalancePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        (cust.AvailableCreditPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                        cust.LastPaymentDateUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? ""));
                }
                break;

            case "audit-activity":
                var auditRes = await GetAuditActivityAsync(query, null, null, null, executingUser, ct);
                if (!auditRes.Success) throw new DomainException(auditRes.ErrorMessage!, auditRes.ErrorCode!);
                await writer.WriteLineAsync("TimestampUtc,Module,Action,PerformedBy,Details");
                foreach (var ev in auditRes.Data!.Events.Items)
                {
                    await writer.WriteLineAsync(string.Join(",",
                        ev.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        EscapeCsv(ev.Module),
                        EscapeCsv(ev.Action),
                        EscapeCsv(ev.PerformedBy),
                        EscapeCsv(ev.Details)));
                }
                break;
        }

        await writer.FlushAsync(ct);
    }

    private static async Task WritePaymentRow(StreamWriter writer, string label, PaymentTenderDetailDto d)
    {
        await writer.WriteLineAsync(string.Join(",",
            label,
            (d.CashPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
            (d.CardPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
            (d.UpiPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
            (d.BankTransferPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
            (d.ChequePaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
            (d.CreditPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture),
            (d.TotalPaise / 100.0).ToString("F2", CultureInfo.InvariantCulture)));
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
