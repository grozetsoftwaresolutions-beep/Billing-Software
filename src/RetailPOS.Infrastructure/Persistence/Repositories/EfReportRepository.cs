using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfReportRepository : IReportRepository
{
    private readonly RetailDbContext _db;

    public EfReportRepository(RetailDbContext db)
    {
        _db = db;
    }

    private async Task<TimeZoneInfo> GetStoreTimeZoneAsync(CancellationToken ct)
    {
        var store = await _db.Stores.AsNoTracking().FirstOrDefaultAsync(ct);
        var tzId = store?.TimeZoneId ?? "India Standard Time";
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
    }

    public async Task<DailySalesSummaryReportDto> GetDailySalesSummaryAsync(DateTime fromUtc, DateTime toUtc, Guid? terminalId, CancellationToken ct = default)
    {
        var salesQuery = _db.SalesInvoices.AsNoTracking().Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc);
        if (terminalId.HasValue)
            salesQuery = salesQuery.Where(s => s.TerminalId == terminalId.Value);

        var allSales = await salesQuery.Select(s => new
        {
            s.Id,
            s.Status,
            FinalPayablePaise = s.FinalPayable.MinorUnits,
            GrossSubtotalPaise = s.GrossSubtotal.MinorUnits,
            TotalLineDiscountsPaise = s.TotalLineDiscounts.MinorUnits,
            InvoiceDiscountPaise = s.InvoiceDiscount.MinorUnits,
            TaxableSubtotalPaise = s.TaxableSubtotal.MinorUnits,
            TotalTaxPaise = s.TotalTax.MinorUnits,
            RoundingAdjustmentPaise = s.RoundingAdjustment.MinorUnits,
            s.CreatedAtUtc
        }).ToListAsync(ct);

        var voided = allSales.Where(s => s.Status == SaleStatus.Voided).ToList();
        var active = allSales.Where(s => s.Status != SaleStatus.Voided).ToList();

        var voidedCount = voided.Count;
        var voidedAmountPaise = voided.Sum(s => s.FinalPayablePaise);

        var totalInvoicesCount = active.Count;
        var grossSubtotalPaise = active.Sum(s => s.GrossSubtotalPaise);
        var totalLineDiscountsPaise = active.Sum(s => s.TotalLineDiscountsPaise);
        var totalInvoiceDiscountsPaise = active.Sum(s => s.InvoiceDiscountPaise);
        var taxableSubtotalPaise = active.Sum(s => s.TaxableSubtotalPaise);
        var totalTaxPaise = active.Sum(s => s.TotalTaxPaise);
        var roundingAdjustmentPaise = active.Sum(s => s.RoundingAdjustmentPaise);
        var grossSalesPaise = active.Sum(s => s.FinalPayablePaise);

        var activeSaleIds = active.Select(s => s.Id).ToList();

        var totalItemsSoldCount = 0;
        if (activeSaleIds.Count > 0)
        {
            totalItemsSoldCount = await _db.SalesItems.AsNoTracking()
                .Where(i => activeSaleIds.Contains(i.SaleId))
                .CountAsync(ct);
        }

        var refundsQuery = _db.SaleRefunds.AsNoTracking().Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc);
        if (terminalId.HasValue)
            refundsQuery = refundsQuery.Where(r => r.TerminalId == terminalId.Value);

        var refunds = await refundsQuery.Select(r => new { RefundAmountPaise = r.TotalRefundAmount.MinorUnits }).ToListAsync(ct);
        var totalRefundsCount = refunds.Count;
        var totalRefundsPaise = refunds.Sum(r => r.RefundAmountPaise);
        var netRevenuePaise = grossSalesPaise - totalRefundsPaise;

        // Payment tenders
        var payments = activeSaleIds.Count > 0
            ? await _db.Payments.AsNoTracking()
                .Where(p => activeSaleIds.Contains(p.SaleId))
                .Select(p => new { p.Method, AmountPaise = p.Amount.MinorUnits })
                .ToListAsync(ct)
            : new();

        var cashPaise = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountPaise);
        var cardPaise = payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.AmountPaise);
        var upiPaise = payments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.AmountPaise);
        var bankPaise = payments.Where(p => p.Method == PaymentMethod.BankTransfer).Sum(p => p.AmountPaise);
        var chequePaise = payments.Where(p => p.Method == PaymentMethod.Cheque).Sum(p => p.AmountPaise);
        var creditPaise = payments.Where(p => p.Method == PaymentMethod.Credit).Sum(p => p.AmountPaise);
        var totalTendersPaise = payments.Sum(p => p.AmountPaise);

        var tendersDto = new PaymentTenderSummaryDto(cashPaise, cardPaise, upiPaise, bankPaise, chequePaise, creditPaise, totalTendersPaise);

        // Hourly distribution in store local timezone
        var storeTz = await GetStoreTimeZoneAsync(ct);
        var hourlyList = new List<HourlySalesDistributionDto>(24);
        for (int h = 0; h < 24; h++)
        {
            var salesInHour = active.Where(s =>
            {
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(s.CreatedAtUtc, storeTz);
                return localTime.Hour == h;
            }).ToList();

            hourlyList.Add(new HourlySalesDistributionDto(h, salesInHour.Count, salesInHour.Sum(s => s.FinalPayablePaise)));
        }

        return new DailySalesSummaryReportDto(
            fromUtc,
            toUtc,
            totalInvoicesCount,
            totalItemsSoldCount,
            voidedCount,
            voidedAmountPaise,
            grossSubtotalPaise,
            totalLineDiscountsPaise,
            totalInvoiceDiscountsPaise,
            taxableSubtotalPaise,
            totalTaxPaise,
            roundingAdjustmentPaise,
            grossSalesPaise,
            totalRefundsCount,
            totalRefundsPaise,
            netRevenuePaise,
            tendersDto,
            hourlyList);
    }

    public async Task<ProductPerformanceReportDto> GetProductPerformanceAsync(
        DateTime fromUtc, DateTime toUtc, int? categoryId, Guid? productId, int page, int pageSize, CancellationToken ct = default)
    {
        var activeSalesQuery = _db.SalesInvoices.AsNoTracking()
            .Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc && s.Status != SaleStatus.Voided);

        var activeSaleIds = await activeSalesQuery.Select(s => s.Id).ToListAsync(ct);

        var itemsQuery = _db.SalesItems.AsNoTracking().Where(i => activeSaleIds.Contains(i.SaleId));
        if (productId.HasValue)
            itemsQuery = itemsQuery.Where(i => i.ProductId == productId.Value);

        var items = await itemsQuery.Select(i => new
        {
            i.Id,
            i.SaleId,
            i.ProductId,
            i.ProductSKU,
            i.ProductName,
            Quantity = i.Quantity.Value,
            Unit = i.Quantity.Unit.Code,
            UnitCostPaise = i.UnitCostPrice.MinorUnits,
            UnitSellingPaise = i.UnitSellingPrice.MinorUnits,
            GrossAmountPaise = i.GrossAmount.MinorUnits,
            DiscountAmountPaise = i.DiscountAmount.MinorUnits
        }).ToListAsync(ct);

        var refundIds = await _db.SaleRefunds.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var refundItems = refundIds.Count > 0
            ? await _db.SaleRefundItems.AsNoTracking()
                .Where(ri => refundIds.Contains(ri.RefundId))
                .Select(ri => new
                {
                    ri.SaleItemId,
                    ri.ProductId,
                    QuantityRefunded = ri.QuantityRefunded.Value,
                    RefundAmountPaise = ri.RefundAmount.MinorUnits
                })
                .ToListAsync(ct)
            : new();

        var categories = await _db.Categories.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var categoryMap = categories.ToDictionary(c => c.Id, c => c.Name);

        var products = await _db.Products.AsNoTracking()
            .Select(p => new { p.Id, p.SKU, p.Name, p.CategoryId, Unit = p.Unit.Code })
            .ToListAsync(ct);

        var productMap = products.ToDictionary(p => p.Id);

        // Group by product
        var productGroups = items.GroupBy(i => i.ProductId).ToList();

        // Build performance lines
        var lines = new List<ProductPerformanceLineDto>();
        foreach (var group in productGroups)
        {
            var pId = group.Key;
            productMap.TryGetValue(pId, out var prodMeta);

            if (categoryId.HasValue && prodMeta?.CategoryId != categoryId.Value)
                continue;

            var sku = prodMeta?.SKU ?? group.First().ProductSKU;
            var name = prodMeta?.Name ?? group.First().ProductName;
            var catName = (prodMeta?.CategoryId.HasValue == true && categoryMap.TryGetValue(prodMeta.CategoryId.Value, out var cn))
                ? cn
                : "General";
            var uom = prodMeta?.Unit ?? group.First().Unit;

            decimal qtySold = group.Sum(g => g.Quantity);
            long grossSalesPaise = group.Sum(g => g.GrossAmountPaise);
            long lineDiscountsPaise = group.Sum(g => g.DiscountAmountPaise);

            // Historical COGS strictly uses SaleItem.UnitCostPrice
            long grossCogsPaise = group.Sum(g =>
                Convert.ToInt64(Math.Round(g.Quantity * g.UnitCostPaise, MidpointRounding.AwayFromZero)));

            // Refunds for this product
            var pRefunds = refundItems.Where(r => r.ProductId == pId).ToList();
            decimal qtyRefunded = pRefunds.Sum(r => r.QuantityRefunded);
            long refundAmountPaise = pRefunds.Sum(r => r.RefundAmountPaise);

            // Refunded COGS: join via SaleItemId if available, otherwise average unit cost of group
            long refundedCogsPaise = 0;
            var itemCostMap = group.ToDictionary(g => g.Id, g => g.UnitCostPaise);
            foreach (var r in pRefunds)
            {
                long unitCost = itemCostMap.TryGetValue(r.SaleItemId, out var uc)
                    ? uc
                    : (group.FirstOrDefault()?.UnitCostPaise ?? 0);
                refundedCogsPaise += Convert.ToInt64(Math.Round(r.QuantityRefunded * unitCost, MidpointRounding.AwayFromZero));
            }

            decimal netQty = qtySold - qtyRefunded;
            long netSalesPaise = grossSalesPaise - lineDiscountsPaise - refundAmountPaise;
            long netCogsPaise = grossCogsPaise - refundedCogsPaise;
            long profitPaise = netSalesPaise - netCogsPaise;

            decimal margin = netSalesPaise <= 0
                ? 0.00m
                : Math.Round(((decimal)profitPaise / (decimal)netSalesPaise) * 100m, 2, MidpointRounding.AwayFromZero);

            lines.Add(new ProductPerformanceLineDto(
                pId,
                sku,
                name,
                catName,
                uom,
                qtySold,
                qtyRefunded,
                netQty,
                grossSalesPaise,
                lineDiscountsPaise,
                refundAmountPaise,
                netSalesPaise,
                netCogsPaise,
                profitPaise,
                margin));
        }

        lines = lines.OrderByDescending(l => l.NetSalesPaise).ToList();

        var totalCount = lines.Count;
        var pagedItems = lines.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        decimal totalQtySold = lines.Sum(l => l.QuantitySold);
        decimal totalQtyRefunded = lines.Sum(l => l.QuantityRefunded);
        decimal totalNetQty = lines.Sum(l => l.NetQuantitySold);
        long totalGrossSalesPaise = lines.Sum(l => l.GrossSalesPaise);
        long totalLineDiscountsPaise = lines.Sum(l => l.LineDiscountsPaise);
        long totalRefundAmountPaise = lines.Sum(l => l.RefundAmountPaise);
        long totalNetSalesPaise = lines.Sum(l => l.NetSalesPaise);
        long totalCogsPaise = lines.Sum(l => l.CostOfGoodsSoldPaise);
        long totalProfitPaise = lines.Sum(l => l.EstimatedGrossProfitPaise);

        decimal overallMargin = totalNetSalesPaise <= 0
            ? 0.00m
            : Math.Round(((decimal)totalProfitPaise / (decimal)totalNetSalesPaise) * 100m, 2, MidpointRounding.AwayFromZero);

        return new ProductPerformanceReportDto(
            totalCount,
            totalQtySold,
            totalQtyRefunded,
            totalNetQty,
            totalGrossSalesPaise,
            totalLineDiscountsPaise,
            totalRefundAmountPaise,
            totalNetSalesPaise,
            totalCogsPaise,
            totalProfitPaise,
            overallMargin,
            new PagedResult<ProductPerformanceLineDto>(pagedItems, totalCount, page, pageSize));
    }

    public async Task<StockValuationReportDto> GetStockValuationAsync(
        int? categoryId, bool lowStockOnly, string? searchTerm, int page, int pageSize, bool maskCost = false, CancellationToken ct = default)
    {
        var productsQuery = _db.Products.AsNoTracking().AsQueryable();

        if (categoryId.HasValue)
            productsQuery = productsQuery.Where(p => p.CategoryId == categoryId.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            productsQuery = productsQuery.Where(p => EF.Functions.Like(p.SKU, $"%{term}%") || EF.Functions.Like(p.Name, $"%{term}%"));
        }

        var products = await productsQuery.Select(p => new
        {
            p.Id,
            p.SKU,
            p.Name,
            p.CategoryId,
            Unit = p.Unit.Code,
            CostPaise = p.CostPrice.MinorUnits,
            SellingPaise = p.SellingPrice.MinorUnits
        }).ToListAsync(ct);

        var categories = await _db.Categories.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var categoryMap = categories.ToDictionary(c => c.Id, c => c.Name);

        var productIds = products.Select(p => p.Id).ToList();

        var balances = productIds.Count > 0
            ? await _db.StockBalances.AsNoTracking()
                .Where(b => productIds.Contains(b.ProductId))
                .Select(b => new
            {
                b.ProductId,
                CurrentQty = b.CurrentQuantity.Value,
                ReorderLevel = b.ReorderLevel.Value,
                b.IsLowStock
            })
            .ToListAsync(ct)
            : new();

        var balanceMap = balances.ToDictionary(b => b.ProductId);

        var items = new List<StockValuationItemDto>();
        foreach (var p in products)
        {
            balanceMap.TryGetValue(p.Id, out var b);
            decimal currentQty = b?.CurrentQty ?? 0m;
            decimal reorderLevel = b?.ReorderLevel ?? 0m;
            bool isLowStock = b?.IsLowStock ?? (currentQty <= reorderLevel);

            if (lowStockOnly && !isLowStock)
                continue;

            long costPaise = maskCost ? 0 : p.CostPaise;
            long sellingPaise = p.SellingPaise;

            long valCost = maskCost ? 0 : Convert.ToInt64(Math.Round(currentQty * costPaise, MidpointRounding.AwayFromZero));
            long valRetail = Convert.ToInt64(Math.Round(currentQty * sellingPaise, MidpointRounding.AwayFromZero));

            var catName = (p.CategoryId.HasValue && categoryMap.TryGetValue(p.CategoryId.Value, out var cn)) ? cn : "General";

            items.Add(new StockValuationItemDto(
                p.Id,
                p.SKU,
                p.Name,
                catName,
                p.Unit,
                currentQty,
                reorderLevel,
                costPaise,
                sellingPaise,
                valCost,
                valRetail,
                isLowStock));
        }

        items = items.OrderBy(i => i.ProductName).ToList();

        var totalCount = items.Count;
        var pagedItems = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        decimal totalStockUnits = items.Sum(i => i.CurrentQuantity);
        long totalValCost = items.Sum(i => i.StockValuationAtCostPaise);
        long totalValRetail = items.Sum(i => i.StockValuationAtRetailPaise);
        long potentialProfit = maskCost ? 0 : (totalValRetail - totalValCost);
        int lowStockCount = items.Count(i => i.IsLowStock);

        return new StockValuationReportDto(
            totalCount,
            totalStockUnits,
            totalValCost,
            totalValRetail,
            potentialProfit,
            lowStockCount,
            new PagedResult<StockValuationItemDto>(pagedItems, totalCount, page, pageSize));
    }

    public async Task<StockMovementLedgerReportDto> GetStockMovementLedgerAsync(
        Guid productId, DateTime fromUtc, DateTime toUtc, int page, int pageSize, CancellationToken ct = default)
    {
        var product = await _db.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.SKU, p.Name, Unit = p.Unit.Code })
            .FirstOrDefaultAsync(ct);

        var sku = product?.SKU ?? "";
        var name = product?.Name ?? "";
        var unit = product?.Unit ?? "PC";

        // Authoritative Opening Quantity: most recent movement before fromUtc
        var priorMovement = await _db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && m.CreatedAtUtc < fromUtc)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Select(m => new { ResultingQty = m.ResultingQuantity.Value })
            .FirstOrDefaultAsync(ct);

        decimal openingQty = priorMovement?.ResultingQty ?? 0m;

        // Movements during the period
        var periodMovements = await _db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && m.CreatedAtUtc >= fromUtc && m.CreatedAtUtc < toUtc)
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.CreatedAtUtc,
                m.MovementType,
                QtyChanged = m.QuantityChanged.Value,
                ResultingQty = m.ResultingQuantity.Value,
                m.ReferenceNumber,
                m.Reason,
                m.UserId
            })
            .ToListAsync(ct);

        var userIds = periodMovements.Where(m => m.UserId.HasValue).Select(m => m.UserId!.Value).Distinct().ToList();
        var users = userIds.Count > 0
            ? await _db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username })
                .ToListAsync(ct)
            : new();
        var userMap = users.ToDictionary(u => u.Id, u => u.Username);

        decimal totalPurchased = periodMovements.Where(m => m.MovementType == StockMovementType.Purchase).Sum(m => m.QtyChanged);
        decimal totalSold = periodMovements.Where(m => m.MovementType == StockMovementType.Sale).Sum(m => Math.Abs(m.QtyChanged));
        decimal totalReturnedCustomers = periodMovements.Where(m => m.MovementType == StockMovementType.SaleReturn).Sum(m => m.QtyChanged);
        decimal totalReturnedSuppliers = periodMovements.Where(m => m.MovementType == StockMovementType.PurchaseReturn).Sum(m => Math.Abs(m.QtyChanged));
        decimal totalAdjustedUp = periodMovements.Where(m => m.MovementType == StockMovementType.AdjustmentIncrease).Sum(m => m.QtyChanged);
        decimal totalAdjustedDown = periodMovements.Where(m => m.MovementType == StockMovementType.AdjustmentDecrease).Sum(m => Math.Abs(m.QtyChanged));
        decimal totalWastage = periodMovements.Where(m => m.MovementType == StockMovementType.Wastage).Sum(m => Math.Abs(m.QtyChanged));

        decimal closingQty = openingQty + periodMovements.Sum(m => m.QtyChanged);

        var lineDtos = periodMovements.Select(m => new StockMovementDetailLineDto(
            m.Id,
            m.CreatedAtUtc,
            m.MovementType,
            m.QtyChanged,
            m.ResultingQty,
            m.ReferenceNumber,
            m.Reason,
            m.UserId.HasValue && userMap.TryGetValue(m.UserId.Value, out var un) ? un : null
        )).ToList();

        var totalCount = lineDtos.Count;
        var paged = lineDtos.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new StockMovementLedgerReportDto(
            productId,
            sku,
            name,
            unit,
            openingQty,
            totalPurchased,
            totalSold,
            totalReturnedCustomers,
            totalReturnedSuppliers,
            totalAdjustedUp,
            totalAdjustedDown,
            totalWastage,
            closingQty,
            new PagedResult<StockMovementDetailLineDto>(paged, totalCount, page, pageSize));
    }

    public async Task<PurchaseSummaryReportDto> GetPurchaseSummaryAsync(
        DateTime fromUtc, DateTime toUtc, Guid? supplierId, int page, int pageSize, CancellationToken ct = default)
    {
        var purchQuery = _db.PurchaseInvoices.AsNoTracking()
            .Where(p => p.ReceivedDateUtc >= fromUtc && p.ReceivedDateUtc < toUtc && p.Status != PurchaseStatus.Cancelled);

        if (supplierId.HasValue)
            purchQuery = purchQuery.Where(p => p.SupplierId == supplierId.Value);

        var invoices = await purchQuery.Select(p => new
        {
            p.Id,
            p.InvoiceNumber,
            p.SupplierInvoiceNumber,
            p.SupplierId,
            p.InvoiceDate,
            GrandTotalPaise = p.GrandTotal.MinorUnits,
            AmountPaidPaise = p.AmountPaid.MinorUnits,
            BalanceDuePaise = p.BalanceDue.MinorUnits,
            TotalTaxPaise = p.TotalTax.MinorUnits,
            p.PaymentStatus,
            p.Status
        }).ToListAsync(ct);

        var supplierIds = invoices.Select(i => i.SupplierId).Distinct().ToList();
        var suppliers = supplierIds.Count > 0
            ? await _db.Suppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(ct)
            : new();
        var supplierMap = suppliers.ToDictionary(s => s.Id, s => s.Name);

        var invoiceLines = invoices.Select(inv => new PurchaseInvoiceReportLineDto(
            inv.Id,
            inv.InvoiceNumber,
            inv.SupplierInvoiceNumber,
            supplierMap.TryGetValue(inv.SupplierId, out var sName) ? sName : "Unknown",
            inv.InvoiceDate,
            inv.GrandTotalPaise,
            inv.AmountPaidPaise,
            inv.BalanceDuePaise,
            inv.PaymentStatus,
            inv.Status
        )).OrderByDescending(l => l.InvoiceDate).ToList();

        var breakdown = invoices.GroupBy(i => i.SupplierId).Select(g => new SupplierPurchaseBreakdownDto(
            g.Key,
            supplierMap.TryGetValue(g.Key, out var sn) ? sn : "Unknown",
            g.Count(),
            g.Sum(x => x.GrandTotalPaise),
            g.Sum(x => x.AmountPaidPaise),
            g.Sum(x => x.BalanceDuePaise)
        )).OrderByDescending(b => b.TotalPurchasesPaise).ToList();

        int totalInvoices = invoiceLines.Count;
        long totalPurchases = invoiceLines.Sum(i => i.GrandTotalPaise);
        long totalPaid = invoiceLines.Sum(i => i.AmountPaidPaise);
        long totalBalanceDue = invoiceLines.Sum(i => i.BalanceDuePaise);
        long totalTaxPaid = invoices.Sum(i => i.TotalTaxPaise);

        var pagedInvoices = invoiceLines.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new PurchaseSummaryReportDto(
            fromUtc,
            toUtc,
            totalInvoices,
            totalPurchases,
            totalPaid,
            totalBalanceDue,
            totalTaxPaid,
            breakdown,
            new PagedResult<PurchaseInvoiceReportLineDto>(pagedInvoices, totalInvoices, page, pageSize));
    }

    public async Task<SupplierBalanceReportDto> GetSupplierBalancesAsync(CancellationToken ct = default)
    {
        var suppliers = await _db.Suppliers.AsNoTracking().Select(s => new
        {
            s.Id,
            s.Name,
            s.Phone,
            s.TaxNumber,
            s.PaymentTermsDays,
            BalanceDuePaise = s.BalanceDue.MinorUnits,
            CreditBalancePaise = s.CreditBalance.MinorUnits
        }).ToListAsync(ct);

        var purchases = await _db.PurchaseInvoices.AsNoTracking()
            .Where(p => p.Status != PurchaseStatus.Cancelled)
            .Select(p => new { p.SupplierId, GrandTotalPaise = p.GrandTotal.MinorUnits, AmountPaidPaise = p.AmountPaid.MinorUnits })
            .ToListAsync(ct);

        var purchGroup = purchases.GroupBy(p => p.SupplierId).ToDictionary(
            g => g.Key,
            g => (TotalPurchases: g.Sum(x => x.GrandTotalPaise), TotalPaid: g.Sum(x => x.AmountPaidPaise)));

        var lines = suppliers.Select(s =>
        {
            purchGroup.TryGetValue(s.Id, out var pg);
            long netPayable = s.BalanceDuePaise - s.CreditBalancePaise;
            return new SupplierBalanceLineDto(
                s.Id,
                s.Name,
                s.Phone,
                s.TaxNumber,
                s.PaymentTermsDays,
                pg.TotalPurchases,
                pg.TotalPaid,
                s.BalanceDuePaise,
                s.CreditBalancePaise,
                netPayable);
        }).OrderByDescending(l => l.BalanceDuePaise).ToList();

        long totalPayable = lines.Sum(l => l.BalanceDuePaise);
        long totalCredit = lines.Sum(l => l.CreditBalancePaise);
        long netLiability = totalPayable - totalCredit;

        return new SupplierBalanceReportDto(totalPayable, totalCredit, netLiability, lines);
    }

    public async Task<CashierPerformanceReportDto> GetCashierPerformanceAsync(
        DateTime fromUtc, DateTime toUtc, Guid? cashierUserId, CancellationToken ct = default)
    {
        var salesQuery = _db.SalesInvoices.AsNoTracking()
            .Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc && s.Status != SaleStatus.Voided);

        if (cashierUserId.HasValue)
            salesQuery = salesQuery.Where(s => s.CashierId == cashierUserId.Value);

        var sales = await salesQuery.Select(s => new
        {
            s.Id,
            s.CashierId,
            GrossSubtotalPaise = s.GrossSubtotal.MinorUnits,
            LineDiscountsPaise = s.TotalLineDiscounts.MinorUnits,
            InvoiceDiscountPaise = s.InvoiceDiscount.MinorUnits,
            FinalPayablePaise = s.FinalPayable.MinorUnits
        }).ToListAsync(ct);

        var saleIds = sales.Select(s => s.Id).ToList();

        var payments = saleIds.Count > 0
            ? await _db.Payments.AsNoTracking()
                .Where(p => saleIds.Contains(p.SaleId))
                .Select(p => new { p.SaleId, p.Method, AmountPaise = p.Amount.MinorUnits })
                .ToListAsync(ct)
            : new();

        var refundsQuery = _db.SaleRefunds.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc);

        if (cashierUserId.HasValue)
            refundsQuery = refundsQuery.Where(r => r.AuthorizedByUserId == cashierUserId.Value);

        var refunds = await refundsQuery.Select(r => new
        {
            r.AuthorizedByUserId,
            RefundAmountPaise = r.TotalRefundAmount.MinorUnits
        }).ToListAsync(ct);

        var sessionsQuery = _db.CashSessions.AsNoTracking()
            .Where(s => s.OpeningTimeUtc >= fromUtc && s.OpeningTimeUtc < toUtc);

        if (cashierUserId.HasValue)
            sessionsQuery = sessionsQuery.Where(s => s.CashierUserId == cashierUserId.Value);

        var sessions = await sessionsQuery.Select(s => new
        {
            s.CashierUserId,
            DiscrepancyPaise = s.Discrepancy.HasValue ? s.Discrepancy.Value.MinorUnits : 0
        }).ToListAsync(ct);

        var distinctCashierIds = sales.Select(s => s.CashierId)
            .Union(refunds.Select(r => r.AuthorizedByUserId))
            .Union(sessions.Select(s => s.CashierUserId));

        if (cashierUserId.HasValue)
            distinctCashierIds = distinctCashierIds.Union(new[] { cashierUserId.Value });

        var cashierIdsList = distinctCashierIds.Distinct().ToList();

        var users = cashierIdsList.Count > 0
            ? await _db.Users.AsNoTracking()
                .Where(u => cashierIdsList.Contains(u.Id))
                .Select(u => new { u.Id, u.Username, u.FullName })
                .ToListAsync(ct)
            : new();

        var userMap = users.ToDictionary(u => u.Id);

        var lines = new List<CashierPerformanceLineDto>();
        foreach (var cId in cashierIdsList)
        {
            userMap.TryGetValue(cId, out var u);
            var username = u?.Username ?? "Unknown";
            var fullName = u?.FullName ?? "Unknown";

            var cSales = sales.Where(s => s.CashierId == cId).ToList();
            int billsCount = cSales.Count;
            long grossSalesPaise = cSales.Sum(s => s.GrossSubtotalPaise);
            long totalDiscountsPaise = cSales.Sum(s => s.LineDiscountsPaise + s.InvoiceDiscountPaise);
            long netSalesPaise = cSales.Sum(s => s.FinalPayablePaise);

            var cSaleIds = cSales.Select(s => s.Id).ToHashSet();
            var cPayments = payments.Where(p => cSaleIds.Contains(p.SaleId)).ToList();
            long cashCollected = cPayments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountPaise);
            long nonCashCollected = cPayments.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.AmountPaise);

            var cRefunds = refunds.Where(r => r.AuthorizedByUserId == cId).ToList();
            long refundsApproved = cRefunds.Sum(r => r.RefundAmountPaise);

            var cSessions = sessions.Where(s => s.CashierUserId == cId).ToList();
            int shifts = cSessions.Count;
            long discrepancies = cSessions.Sum(s => Math.Abs(s.DiscrepancyPaise));

            lines.Add(new CashierPerformanceLineDto(
                cId,
                username,
                fullName,
                shifts,
                billsCount,
                grossSalesPaise,
                totalDiscountsPaise,
                netSalesPaise,
                refundsApproved,
                cashCollected,
                nonCashCollected,
                discrepancies));
        }

        return new CashierPerformanceReportDto(fromUtc, toUtc, lines.OrderBy(l => l.Username).ToList());
    }

    public async Task<CurrentTerminalReportDto> GetCurrentTerminalReportAsync(
        Guid terminalId, TimeZoneInfo storeTimeZone, CancellationToken ct = default)
    {
        var terminal = await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == terminalId, ct);
        var termCode = terminal?.TerminalCode ?? "UNKNOWN";
        var termName = terminal?.TerminalName ?? "Unknown Terminal";

        var activeSession = await _db.CashSessions.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status == CashSessionStatus.Open)
            .FirstOrDefaultAsync(ct);

        string? activeCashier = null;
        if (activeSession != null)
        {
            var user = await _db.Users.AsNoTracking()
                .Where(u => u.Id == activeSession.CashierUserId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(ct);
            activeCashier = user;
        }

        long openingFloatPaise = activeSession?.OpeningFloat.MinorUnits ?? 0;
        long currentExpectedCashPaise = activeSession?.ExpectedCash.MinorUnits ?? 0;
        long totalCashSalesPaise = activeSession?.TotalCashSales.MinorUnits ?? 0;
        long totalCashRepaymentsPaise = activeSession?.TotalCashRepayments.MinorUnits ?? 0;
        long totalPayInsPaise = activeSession?.TotalPayIns.MinorUnits ?? 0;
        long totalCashRefundsPaise = activeSession?.TotalCashRefunds.MinorUnits ?? 0;
        long totalPayOutsPaise = activeSession?.TotalPayOuts.MinorUnits ?? 0;
        long totalCashDropsPaise = activeSession?.TotalCashDrops.MinorUnits ?? 0;

        // Today local start of day converted to UTC
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, storeTimeZone);
        var todayStartLocal = DateOnly.FromDateTime(nowLocal).ToDateTime(TimeOnly.MinValue);
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayStartLocal, storeTimeZone);

        var todaySales = await _db.SalesInvoices.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.CreatedAtUtc >= todayStartUtc && s.Status != SaleStatus.Voided)
            .Select(s => new
            {
                s.Id,
                GrossSubtotalPaise = s.GrossSubtotal.MinorUnits,
                LineDiscountsPaise = s.TotalLineDiscounts.MinorUnits,
                InvoiceDiscountPaise = s.InvoiceDiscount.MinorUnits,
                TotalTaxPaise = s.TotalTax.MinorUnits,
                FinalPayablePaise = s.FinalPayable.MinorUnits
            })
            .ToListAsync(ct);

        int todayBills = todaySales.Count;
        long todayGrossSales = todaySales.Sum(s => s.GrossSubtotalPaise);
        long todayLineDiscounts = todaySales.Sum(s => s.LineDiscountsPaise);
        long todayInvoiceDiscounts = todaySales.Sum(s => s.InvoiceDiscountPaise);
        long todayTax = todaySales.Sum(s => s.TotalTaxPaise);
        long todayNetSales = todaySales.Sum(s => s.FinalPayablePaise);

        var todayRefunds = await _db.SaleRefunds.AsNoTracking()
            .Where(r => r.TerminalId == terminalId && r.CreatedAtUtc >= todayStartUtc)
            .Select(r => new { AmountPaise = r.TotalRefundAmount.MinorUnits })
            .ToListAsync(ct);

        int todayRefundsCount = todayRefunds.Count;
        long todayRefundsPaise = todayRefunds.Sum(r => r.AmountPaise);

        var todaySaleIds = todaySales.Select(s => s.Id).ToList();
        var todayPayments = todaySaleIds.Count > 0
            ? await _db.Payments.AsNoTracking()
                .Where(p => todaySaleIds.Contains(p.SaleId))
                .Select(p => new { p.Method, AmountPaise = p.Amount.MinorUnits })
                .ToListAsync(ct)
            : new();

        var tenders = new PaymentTenderSummaryDto(
            todayPayments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountPaise),
            todayPayments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.AmountPaise),
            todayPayments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.AmountPaise),
            todayPayments.Where(p => p.Method == PaymentMethod.BankTransfer).Sum(p => p.AmountPaise),
            todayPayments.Where(p => p.Method == PaymentMethod.Cheque).Sum(p => p.AmountPaise),
            todayPayments.Where(p => p.Method == PaymentMethod.Credit).Sum(p => p.AmountPaise),
            todayPayments.Sum(p => p.AmountPaise));

        // Recent 10 drawer transactions
        var recentTx = activeSession != null
            ? await _db.CashSessionTransactions.AsNoTracking()
                .Where(tx => tx.CashSessionId == activeSession.Id)
                .OrderByDescending(tx => tx.TimestampUtc)
                .Take(10)
                .Select(tx => new
                {
                    tx.TimestampUtc,
                    Type = tx.Type.ToString(),
                    ReferenceNumber = tx.Reason,
                    AmountPaise = tx.Amount.MinorUnits,
                    tx.CreatedByUserId
                })
                .ToListAsync(ct)
            : new();

        var recentUserIds = recentTx.Select(tx => tx.CreatedByUserId).Distinct().ToList();
        var recentUsers = recentUserIds.Count > 0
            ? await _db.Users.AsNoTracking()
                .Where(u => recentUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username })
                .ToListAsync(ct)
            : new();
        var uMap = recentUsers.ToDictionary(u => u.Id, u => u.Username);

        var recentTxDtos = recentTx.Select(tx => new RecentTerminalTransactionDto(
            tx.TimestampUtc,
            tx.Type,
            tx.ReferenceNumber,
            tx.AmountPaise,
            uMap.TryGetValue(tx.CreatedByUserId, out var name) ? name : "Unknown"
        )).ToList();

        return new CurrentTerminalReportDto(
            terminalId,
            termCode,
            termName,
            DateTime.UtcNow,
            activeSession?.Id,
            activeCashier,
            activeSession?.OpeningTimeUtc,
            activeSession != null ? "Open" : "Closed",
            openingFloatPaise,
            currentExpectedCashPaise,
            totalCashSalesPaise,
            totalCashRepaymentsPaise,
            totalPayInsPaise,
            totalCashRefundsPaise,
            totalPayOutsPaise,
            totalCashDropsPaise,
            todayBills,
            todayGrossSales,
            todayLineDiscounts,
            todayInvoiceDiscounts,
            todayTax,
            todayNetSales,
            todayRefundsCount,
            todayRefundsPaise,
            tenders,
            recentTxDtos);
    }

    public async Task<PaymentSummaryReportDto> GetPaymentSummaryAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // 1. Sales Tenders
        var activeSalesIds = await _db.SalesInvoices.AsNoTracking()
            .Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc && s.Status != SaleStatus.Voided)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var salesPayments = activeSalesIds.Count > 0
            ? await _db.Payments.AsNoTracking()
                .Where(p => activeSalesIds.Contains(p.SaleId))
                .Select(p => new { p.Method, AmountPaise = p.Amount.MinorUnits })
                .ToListAsync(ct)
            : new();

        var salesTenders = AggregateTenders(salesPayments);

        // 2. Customer Repayment Tenders
        var custPayments = await _db.CustomerPayments.AsNoTracking()
            .Where(cp => cp.PaymentDateUtc >= fromUtc && cp.PaymentDateUtc < toUtc)
            .Select(cp => new { cp.Method, AmountPaise = cp.Amount.MinorUnits })
            .ToListAsync(ct);

        var repaymentTenders = AggregateTenders(custPayments);

        // 3. Refunds Outflow Tenders
        var refunds = await _db.SaleRefunds.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc)
            .Select(r => new { Method = r.RefundMethod, AmountPaise = r.TotalRefundAmount.MinorUnits })
            .ToListAsync(ct);

        var refundTenders = AggregateTenders(refunds);

        // 4. Net Tenders
        var netTenders = new PaymentTenderDetailDto(
            salesTenders.CashPaise + repaymentTenders.CashPaise - refundTenders.CashPaise,
            salesTenders.CardPaise + repaymentTenders.CardPaise - refundTenders.CardPaise,
            salesTenders.UpiPaise + repaymentTenders.UpiPaise - refundTenders.UpiPaise,
            salesTenders.BankTransferPaise + repaymentTenders.BankTransferPaise - refundTenders.BankTransferPaise,
            salesTenders.ChequePaise + repaymentTenders.ChequePaise - refundTenders.ChequePaise,
            salesTenders.CreditPaise + repaymentTenders.CreditPaise - refundTenders.CreditPaise,
            salesTenders.TotalPaise + repaymentTenders.TotalPaise - refundTenders.TotalPaise);

        return new PaymentSummaryReportDto(fromUtc, toUtc, salesTenders, repaymentTenders, refundTenders, netTenders);
    }

    private static PaymentTenderDetailDto AggregateTenders(IEnumerable<dynamic> items)
    {
        long cash = 0, card = 0, upi = 0, bank = 0, cheque = 0, credit = 0;
        foreach (var item in items)
        {
            PaymentMethod m = item.Method;
            long amt = item.AmountPaise;
            switch (m)
            {
                case PaymentMethod.Cash: cash += amt; break;
                case PaymentMethod.Card: card += amt; break;
                case PaymentMethod.UPI: upi += amt; break;
                case PaymentMethod.BankTransfer: bank += amt; break;
                case PaymentMethod.Cheque: cheque += amt; break;
                case PaymentMethod.Credit: credit += amt; break;
            }
        }
        long total = cash + card + upi + bank + cheque + credit;
        return new PaymentTenderDetailDto(cash, card, upi, bank, cheque, credit, total);
    }

    public async Task<ProfitMarginEstimateReportDto> GetProfitEstimateAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var sales = await _db.SalesInvoices.AsNoTracking()
            .Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc && s.Status != SaleStatus.Voided)
            .Select(s => new
            {
                s.Id,
                FinalPayablePaise = s.FinalPayable.MinorUnits,
                LineDiscountsPaise = s.TotalLineDiscounts.MinorUnits,
                InvoiceDiscountPaise = s.InvoiceDiscount.MinorUnits
            })
            .ToListAsync(ct);

        long grossSalesPaise = sales.Sum(s => s.FinalPayablePaise);
        long lineDiscountsPaise = sales.Sum(s => s.LineDiscountsPaise);
        long invoiceDiscountsPaise = sales.Sum(s => s.InvoiceDiscountPaise);

        var refunds = await _db.SaleRefunds.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc)
            .Select(r => new { r.Id, RefundAmountPaise = r.TotalRefundAmount.MinorUnits })
            .ToListAsync(ct);

        long refundedAmountPaise = refunds.Sum(r => r.RefundAmountPaise);
        long netSalesPaise = grossSalesPaise - refundedAmountPaise;

        var saleIds = sales.Select(s => s.Id).ToList();
        var saleItems = saleIds.Count > 0
            ? await _db.SalesItems.AsNoTracking()
                .Where(i => saleIds.Contains(i.SaleId))
                .Select(i => new { i.Id, Quantity = i.Quantity.Value, UnitCostPaise = i.UnitCostPrice.MinorUnits })
                .ToListAsync(ct)
            : new();

        long grossCogsPaise = saleItems.Sum(i =>
            Convert.ToInt64(Math.Round(i.Quantity * i.UnitCostPaise, MidpointRounding.AwayFromZero)));

        var refundIds = refunds.Select(r => r.Id).ToList();
        var refundItems = refundIds.Count > 0
            ? await _db.SaleRefundItems.AsNoTracking()
                .Where(ri => refundIds.Contains(ri.RefundId))
                .Select(ri => new { ri.SaleItemId, QuantityRefunded = ri.QuantityRefunded.Value })
                .ToListAsync(ct)
            : new();

        var saleItemCostMap = saleItems.ToDictionary(i => i.Id, i => i.UnitCostPaise);
        long refundedCogsPaise = 0;
        foreach (var ri in refundItems)
        {
            if (saleItemCostMap.TryGetValue(ri.SaleItemId, out var uc))
            {
                refundedCogsPaise += Convert.ToInt64(Math.Round(ri.QuantityRefunded * uc, MidpointRounding.AwayFromZero));
            }
        }

        long netCogsPaise = grossCogsPaise - refundedCogsPaise;
        long profitPaise = netSalesPaise - netCogsPaise;

        decimal margin = netSalesPaise <= 0
            ? 0.00m
            : Math.Round(((decimal)profitPaise / (decimal)netSalesPaise) * 100m, 2, MidpointRounding.AwayFromZero);

        return new ProfitMarginEstimateReportDto(
            fromUtc,
            toUtc,
            grossSalesPaise,
            lineDiscountsPaise,
            invoiceDiscountsPaise,
            refundedAmountPaise,
            netSalesPaise,
            grossCogsPaise,
            refundedCogsPaise,
            netCogsPaise,
            profitPaise,
            margin,
            IsEstimate: true);
    }

    public async Task<TaxSummaryReportDto> GetTaxSummaryAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var sales = await _db.SalesInvoices.AsNoTracking()
            .Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc && s.Status != SaleStatus.Voided)
            .Select(s => new
            {
                s.Id,
                s.CustomerId,
                TaxableSubtotalPaise = s.TaxableSubtotal.MinorUnits,
                TotalTaxPaise = s.TotalTax.MinorUnits
            })
            .ToListAsync(ct);

        var custIds = sales.Where(s => s.CustomerId.HasValue).Select(s => s.CustomerId!.Value).Distinct().ToList();
        var customersWithGstin = custIds.Count > 0
            ? await _db.Customers.AsNoTracking()
                .Where(c => custIds.Contains(c.Id) && !string.IsNullOrEmpty(c.Gstin))
                .Select(c => c.Id)
                .ToListAsync(ct)
            : new();

        var b2bCustomerSet = new HashSet<Guid>(customersWithGstin);

        long b2bTaxable = sales.Where(s => s.CustomerId.HasValue && b2bCustomerSet.Contains(s.CustomerId.Value)).Sum(s => s.TaxableSubtotalPaise);
        long b2cTaxable = sales.Where(s => !s.CustomerId.HasValue || !b2bCustomerSet.Contains(s.CustomerId.Value)).Sum(s => s.TaxableSubtotalPaise);
        long totalTaxable = b2bTaxable + b2cTaxable;
        long totalTaxCollected = sales.Sum(s => s.TotalTaxPaise);

        var refundIds = await _db.SaleRefunds.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var refundItems = refundIds.Count > 0
            ? await _db.SaleRefundItems.AsNoTracking()
                .Where(ri => refundIds.Contains(ri.RefundId))
                .Select(ri => new
                {
                    ri.SaleItemId,
                    RefundAmountPaise = ri.RefundAmount.MinorUnits,
                    TaxRefundPaise = ri.TaxRefundAmount.MinorUnits
                })
                .ToListAsync(ct)
            : new();

        long totalTaxRefunded = refundItems.Sum(ri => ri.TaxRefundPaise);
        long netTaxPayable = totalTaxCollected - totalTaxRefunded;

        // Categories & Components
        var saleIds = sales.Select(s => s.Id).ToList();
        var saleItems = saleIds.Count > 0
            ? await _db.SalesItems.AsNoTracking()
                .Where(i => saleIds.Contains(i.SaleId))
                .Select(i => new
                {
                    i.Id,
                    i.TaxRatePercentage,
                    TaxablePaise = i.TaxableAmount.MinorUnits,
                    TaxPaise = i.TaxAmount.MinorUnits
                })
                .ToListAsync(ct)
            : new();

        var taxComponents = saleIds.Count > 0
            ? await _db.SaleItemTaxComponents.AsNoTracking()
                .Where(tc => saleItems.Select(i => i.Id).Contains(tc.SaleItemId))
                .Select(tc => new
                {
                    tc.SaleItemId,
                    tc.ComponentName,
                    tc.RatePercentage,
                    AmountPaise = tc.Amount.MinorUnits
                })
                .ToListAsync(ct)
            : new();

        // Rate category grouping: 0%, 5%, 18%, etc.
        var categories = saleItems.GroupBy(i => i.TaxRatePercentage).Select(g =>
        {
            decimal rate = g.Key;
            string catName = rate == 0 ? "GST 0% (Nil-rated / Exempt)" : $"GST {rate:G29}%";
            long taxable = g.Sum(x => x.TaxablePaise);
            long taxAmt = g.Sum(x => x.TaxPaise);

            var gItemIds = g.Select(x => x.Id).ToHashSet();
            var gRefunds = refundItems.Where(r => gItemIds.Contains(r.SaleItemId)).ToList();
            long refTax = gRefunds.Sum(r => r.TaxRefundPaise);
            long refTaxable = gRefunds.Sum(r => r.RefundAmountPaise - r.TaxRefundPaise);
            long netTax = taxAmt - refTax;

            return new TaxCategoryBreakdownDto(
                (int)(rate * 100),
                catName,
                taxable,
                taxAmt,
                Math.Max(0, refTaxable),
                refTax,
                netTax);
        }).OrderBy(c => c.TaxCategoryId).ToList();

        // Component breakdown (CGST, SGST, IGST)
        var components = taxComponents.GroupBy(tc => new { tc.ComponentName, tc.RatePercentage }).Select(g =>
        {
            string compName = g.Key.ComponentName;
            decimal rate = g.Key.RatePercentage;
            long collected = g.Sum(x => x.AmountPaise);

            // Proportion of refund tax for this component
            long totalCollectedAll = totalTaxCollected > 0 ? totalTaxCollected : 1;
            long refCompTax = totalTaxCollected > 0 ? Convert.ToInt64(Math.Round((double)collected / totalCollectedAll * totalTaxRefunded)) : 0;
            long netComp = collected - refCompTax;

            return new TaxComponentBreakdownDto(compName, rate, collected, refCompTax, netComp);
        }).OrderBy(c => c.ComponentName).ThenBy(c => c.RatePercentage).ToList();

        return new TaxSummaryReportDto(
            fromUtc,
            toUtc,
            b2bTaxable,
            b2cTaxable,
            totalTaxable,
            totalTaxCollected,
            totalTaxRefunded,
            netTaxPayable,
            categories,
            components);
    }

    public async Task<CustomerCreditReportDto> GetCustomerCreditSummaryAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var zeroMoney = Money.Zero();
        var query = _db.Customers.AsNoTracking()
            .Where(c => c.CreditLimit != zeroMoney || c.OutstandingCredit != zeroMoney || c.CreditBalance != zeroMoney);

        var totalCount = await query.CountAsync(ct);

        var customers = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Phone,
                c.Gstin,
                CreditLimitPaise = c.CreditLimit.MinorUnits,
                OutstandingCreditPaise = c.OutstandingCredit.MinorUnits,
                CreditBalancePaise = c.CreditBalance.MinorUnits
            })
            .ToListAsync(ct);

        var custIds = customers.Select(c => c.Id).ToList();
        var lastPayments = custIds.Count > 0
            ? await _db.CustomerPayments.AsNoTracking()
                .Where(cp => custIds.Contains(cp.CustomerId))
                .GroupBy(cp => cp.CustomerId)
                .Select(g => new { CustomerId = g.Key, LastDate = g.Max(x => x.PaymentDateUtc) })
                .ToListAsync(ct)
            : new();

        var lastPaymentMap = lastPayments.ToDictionary(x => x.CustomerId, x => x.LastDate);

        var lines = customers.Select(c =>
        {
            long avail = Math.Max(0, c.CreditLimitPaise - c.OutstandingCreditPaise);
            lastPaymentMap.TryGetValue(c.Id, out var lastDt);
            return new CustomerCreditLineDto(
                c.Id,
                c.Name,
                c.Phone,
                c.Gstin,
                c.CreditLimitPaise,
                c.OutstandingCreditPaise,
                c.CreditBalancePaise,
                avail,
                lastDt != default ? lastDt : (DateTime?)null);
        }).ToList();

        // Overall summary
        var allSummary = await _db.Customers.AsNoTracking()
            .Where(c => c.CreditLimit != zeroMoney || c.OutstandingCredit != zeroMoney || c.CreditBalance != zeroMoney)
            .Select(c => new
            {
                Limit = c.CreditLimit.MinorUnits,
                Debt = c.OutstandingCredit.MinorUnits,
                Credit = c.CreditBalance.MinorUnits
            })
            .ToListAsync(ct);

        long totalLimit = allSummary.Sum(x => x.Limit);
        long totalDebt = allSummary.Sum(x => x.Debt);
        long totalStoreCredit = allSummary.Sum(x => x.Credit);
        long totalNetReceivable = totalDebt - totalStoreCredit;

        return new CustomerCreditReportDto(
            totalCount,
            totalLimit,
            totalDebt,
            totalNetReceivable,
            new PagedResult<CustomerCreditLineDto>(lines, totalCount, page, pageSize));
    }

    public async Task<AuditActivityReportDto> GetAuditActivityAsync(
        DateTime fromUtc, DateTime toUtc, string? moduleName, string? action, string? performedBy, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.AuditLogs.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc);

        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            var m = moduleName.Trim();
            query = query.Where(a => EF.Functions.Like(a.EntityName, $"%{m}%"));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var act = action.Trim();
            query = query.Where(a => EF.Functions.Like(a.Action, $"%{act}%"));
        }

        if (!string.IsNullOrWhiteSpace(performedBy))
        {
            var pb = performedBy.Trim();
            query = query.Where(a => a.Details != null && EF.Functions.Like(a.Details, $"%{pb}%"));
        }

        var totalCount = await query.CountAsync(ct);

        var events = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditTrailLineDto(
                a.Id,
                a.CreatedAtUtc,
                a.EntityName,
                a.Action,
                a.UserId != null ? a.UserId.ToString()! : "System",
                a.Details ?? ""
            ))
            .ToListAsync(ct);

        return new AuditActivityReportDto(
            fromUtc,
            toUtc,
            new PagedResult<AuditTrailLineDto>(events, totalCount, page, pageSize));
    }
}
