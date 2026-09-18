using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Contracts.Reports;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.E2E.Tests.Simulation;

/// <summary>
/// Independent deterministic accounting & inventory ledger for Sub-Phase 15.4 pilot simulation.
/// Calculates expected values using pure domain equations without invoking production calculators:
///   ExpectedInventory = OpeningInventory + Purchases - Sales + Returns ± Adjustments
///   ExpectedGross = Sum(Qty * UnitPrice)
///   ExpectedNet = Gross - Discounts
///   ExpectedDrawerCash = Float + CashSales + CashRepayments - CashRefunds - PayOuts
///   ExpectedCustomerBalance = Opening + CreditSales - Payments
///   ExpectedSupplierBalance = Opening + Purchases - Payments
/// </summary>
public sealed class IndependentPilotLedger
{
    public sealed class ProductLedger
    {
        public Guid ProductId { get; set; }
        public string Sku { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long CostPricePaise { get; set; }
        public long SellingPricePaise { get; set; }
        public int TaxCategoryId { get; set; }
        public decimal TaxRatePercentage { get; set; }
        public decimal OpeningStock { get; set; }
        public decimal TotalPurchased { get; set; }
        public decimal TotalSold { get; set; }
        public decimal TotalReturned { get; set; }
        public decimal TotalAdjusted { get; set; }

        public decimal ExpectedStock =>
            OpeningStock + TotalPurchased - TotalSold + TotalReturned + TotalAdjusted;
    }

    public sealed class CustomerLedger
    {
        public Guid CustomerId { get; set; }
        public long OpeningBalancePaise { get; set; }
        public long TotalCreditSalesPaise { get; set; }
        public long TotalPaymentsPaise { get; set; }

        public long ExpectedBalancePaise =>
            OpeningBalancePaise + TotalCreditSalesPaise - TotalPaymentsPaise;
    }

    public sealed class SupplierLedger
    {
        public Guid SupplierId { get; set; }
        public long OpeningBalancePaise { get; set; }
        public long TotalPurchasesPaise { get; set; }
        public long TotalPaymentsPaise { get; set; }

        public long ExpectedBalancePaise =>
            OpeningBalancePaise + TotalPurchasesPaise - TotalPaymentsPaise;
    }

    public sealed class TerminalDrawerLedger
    {
        public Guid TerminalId { get; set; }
        public long OpeningFloatPaise { get; set; }
        public long TotalCashSalesPaise { get; set; }
        public long TotalCashRepaymentsPaise { get; set; }
        public long TotalCashRefundsPaise { get; set; }
        public long TotalPayInsPaise { get; set; }
        public long TotalPayOutsPaise { get; set; }

        public long ExpectedDrawerCashPaise =>
            OpeningFloatPaise + TotalCashSalesPaise + TotalCashRepaymentsPaise + TotalPayInsPaise - TotalCashRefundsPaise - TotalPayOutsPaise;
    }

    public Dictionary<Guid, ProductLedger> Products { get; } = new();
    public Dictionary<Guid, CustomerLedger> Customers { get; } = new();
    public Dictionary<Guid, SupplierLedger> Suppliers { get; } = new();
    public Dictionary<Guid, TerminalDrawerLedger> TerminalDrawers { get; } = new();

    // Financial Accumulators
    public int TotalInvoicesCount { get; private set; }
    public int TotalItemsSoldCount { get; private set; }
    public long GrossSubtotalPaise { get; private set; }
    public long TotalLineDiscountsPaise { get; private set; }
    public long TotalInvoiceDiscountsPaise { get; private set; }
    public long TaxableSubtotalPaise { get; private set; }
    public long TotalTaxPaise { get; private set; }
    public long RoundingAdjustmentPaise { get; private set; }
    public long GrossSalesPaise { get; private set; }

    public int TotalRefundsCount { get; private set; }
    public long TotalRefundsPaise { get; private set; }

    public long NetSalesPaise => GrossSubtotalPaise - TotalLineDiscountsPaise - TotalInvoiceDiscountsPaise;
    public long NetRevenuePaise => NetSalesPaise - TotalRefundsPaise;

    // Payment Tenders
    public long TotalCashPaise { get; private set; }
    public long TotalCardPaise { get; private set; }
    public long TotalUpiPaise { get; private set; }
    public long TotalStoreCreditPaise { get; private set; }
    public long TotalPaymentTendersPaise => TotalCashPaise + TotalCardPaise + TotalUpiPaise + TotalStoreCreditPaise;

    // Invoice Number Tracker
    public List<string> IssuedInvoiceNumbers { get; } = new();

    public void RegisterProduct(
        Guid productId,
        string sku,
        string name,
        long costPricePaise,
        long sellingPricePaise,
        int taxCategoryId,
        decimal taxRatePercentage,
        decimal openingStock)
    {
        Products[productId] = new ProductLedger
        {
            ProductId = productId,
            Sku = sku,
            Name = name,
            CostPricePaise = costPricePaise,
            SellingPricePaise = sellingPricePaise,
            TaxCategoryId = taxCategoryId,
            TaxRatePercentage = taxRatePercentage,
            OpeningStock = openingStock
        };
    }

    public void RegisterCustomer(Guid customerId, long openingBalancePaise = 0)
    {
        Customers[customerId] = new CustomerLedger
        {
            CustomerId = customerId,
            OpeningBalancePaise = openingBalancePaise
        };
    }

    public void RegisterSupplier(Guid supplierId, long openingBalancePaise = 0)
    {
        Suppliers[supplierId] = new SupplierLedger
        {
            SupplierId = supplierId,
            OpeningBalancePaise = openingBalancePaise
        };
    }

    public void DeclareOpeningFloat(Guid terminalId, long openingFloatPaise)
    {
        if (!TerminalDrawers.TryGetValue(terminalId, out var drawer))
        {
            drawer = new TerminalDrawerLedger { TerminalId = terminalId };
            TerminalDrawers[terminalId] = drawer;
        }

        drawer.OpeningFloatPaise = openingFloatPaise;
    }

    /// <summary>
    /// Records a sale transaction using purely independent domain equations.
    /// </summary>
    public void RecordSale(
        Guid terminalId,
        IReadOnlyList<(Guid ProductId, decimal Quantity)> items,
        IReadOnlyList<(PaymentMethod Method, long AmountPaise)> payments,
        string invoiceNumber,
        Guid? customerId = null,
        long invoiceDiscountPaise = 0,
        bool applyCashRounding = true)
    {
        long saleGrossSubtotal = 0;
        long saleTaxable = 0;
        long saleTax = 0;

        foreach (var (productId, qty) in items)
        {
            if (!Products.TryGetValue(productId, out var prod))
                throw new InvalidOperationException($"Product {productId} not registered in independent ledger.");

            prod.TotalSold += qty;
            TotalItemsSoldCount += (int)qty;

            long lineGross = (long)Math.Round(prod.SellingPricePaise * qty, MidpointRounding.AwayFromZero);
            saleGrossSubtotal += lineGross;

            // Independent tax computation (Tax Inclusive GST formula)
            if (prod.TaxRatePercentage > 0m)
            {
                decimal rateFraction = 1m + (prod.TaxRatePercentage / 100m);
                long lineTaxable = (long)Math.Round(lineGross / rateFraction, MidpointRounding.AwayFromZero);
                long lineTax = lineGross - lineTaxable;
                saleTaxable += lineTaxable;
                saleTax += lineTax;
            }
            else
            {
                saleTaxable += lineGross;
            }
        }

        GrossSubtotalPaise += saleGrossSubtotal;
        TotalInvoiceDiscountsPaise += invoiceDiscountPaise;
        TaxableSubtotalPaise += saleTaxable;
        TotalTaxPaise += saleTax;

        // Cash rounding calculation
        bool isPureCash = payments.All(p => p.Method == PaymentMethod.Cash);
        long roundedPayable = saleGrossSubtotal - invoiceDiscountPaise;
        long roundingAdj = 0;

        if (applyCashRounding && isPureCash)
        {
            // Nearest 100 paise (whole rupee)
            roundedPayable = (long)Math.Round(roundedPayable / 100.0, MidpointRounding.AwayFromZero) * 100;
            roundingAdj = roundedPayable - (saleGrossSubtotal - invoiceDiscountPaise);
        }

        RoundingAdjustmentPaise += roundingAdj;
        GrossSalesPaise += roundedPayable;
        TotalInvoicesCount++;
        IssuedInvoiceNumbers.Add(invoiceNumber);

        // Record payments into tender accumulators & drawers
        if (!TerminalDrawers.TryGetValue(terminalId, out var drawer))
        {
            drawer = new TerminalDrawerLedger { TerminalId = terminalId };
            TerminalDrawers[terminalId] = drawer;
        }

        foreach (var (method, amount) in payments)
        {
            switch (method)
            {
                case PaymentMethod.Cash:
                    TotalCashPaise += amount;
                    drawer.TotalCashSalesPaise += amount;
                    break;
                case PaymentMethod.Card:
                    TotalCardPaise += amount;
                    break;
                case PaymentMethod.UPI:
                    TotalUpiPaise += amount;
                    break;
                case PaymentMethod.Credit:
                    TotalStoreCreditPaise += amount;
                    if (customerId.HasValue && Customers.TryGetValue(customerId.Value, out var cust))
                    {
                        cust.TotalCreditSalesPaise += amount;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Records an item return/refund using independent equations.
    /// </summary>
    public void RecordRefund(
        Guid terminalId,
        IReadOnlyList<(Guid ProductId, decimal Quantity)> returnedItems,
        PaymentMethod refundMethod,
        long refundAmountPaise)
    {
        foreach (var (productId, qty) in returnedItems)
        {
            if (!Products.TryGetValue(productId, out var prod))
                throw new InvalidOperationException($"Product {productId} not registered in independent ledger.");

            prod.TotalReturned += qty;
        }

        TotalRefundsCount++;
        TotalRefundsPaise += refundAmountPaise;

        if (refundMethod == PaymentMethod.Cash)
        {
            if (!TerminalDrawers.TryGetValue(terminalId, out var drawer))
            {
                drawer = new TerminalDrawerLedger { TerminalId = terminalId };
                TerminalDrawers[terminalId] = drawer;
            }

            drawer.TotalCashRefundsPaise += refundAmountPaise;
        }
    }

    /// <summary>
    /// Records a supplier purchase receipt using independent equations.
    /// </summary>
    public void RecordPurchase(
        Guid supplierId,
        IReadOnlyList<(Guid ProductId, decimal Quantity, long UnitCostPaise)> items,
        long initialPaymentPaise = 0)
    {
        if (!Suppliers.TryGetValue(supplierId, out var sup))
            throw new InvalidOperationException($"Supplier {supplierId} not registered in independent ledger.");

        long purchaseTotal = 0;
        foreach (var (productId, qty, unitCost) in items)
        {
            if (!Products.TryGetValue(productId, out var prod))
                throw new InvalidOperationException($"Product {productId} not registered in independent ledger.");

            prod.TotalPurchased += qty;
            purchaseTotal += (long)Math.Round(unitCost * qty, MidpointRounding.AwayFromZero);
        }

        sup.TotalPurchasesPaise += purchaseTotal;
        sup.TotalPaymentsPaise += initialPaymentPaise;
    }

    /// <summary>
    /// Records an inventory adjustment.
    /// </summary>
    public void RecordAdjustment(Guid productId, decimal quantityDelta)
    {
        if (!Products.TryGetValue(productId, out var prod))
            throw new InvalidOperationException($"Product {productId} not registered in independent ledger.");

        prod.TotalAdjusted += quantityDelta;
    }

    /// <summary>
    /// Records a customer repayment towards credit balance.
    /// </summary>
    public void RecordCustomerPayment(Guid customerId, Guid terminalId, long amountPaise, PaymentMethod method)
    {
        if (!Customers.TryGetValue(customerId, out var cust))
            throw new InvalidOperationException($"Customer {customerId} not registered in independent ledger.");

        cust.TotalPaymentsPaise += amountPaise;

        if (method == PaymentMethod.Cash)
        {
            if (!TerminalDrawers.TryGetValue(terminalId, out var drawer))
            {
                drawer = new TerminalDrawerLedger { TerminalId = terminalId };
                TerminalDrawers[terminalId] = drawer;
            }

            drawer.TotalCashRepaymentsPaise += amountPaise;
        }
    }

    /// <summary>
    /// Records a supplier payment.
    /// </summary>
    public void RecordSupplierPayment(Guid supplierId, long amountPaise)
    {
        if (!Suppliers.TryGetValue(supplierId, out var sup))
            throw new InvalidOperationException($"Supplier {supplierId} not registered in independent ledger.");

        sup.TotalPaymentsPaise += amountPaise;
    }

    // ==========================================
    // DETERMINISTIC RECONCILIATION ASSERTIONS
    // ==========================================

    public void AssertInventoryReconciled(Guid productId, StockBalanceDto actual)
    {
        var expected = Products[productId].ExpectedStock;
        Assert.Equal(expected, actual.CurrentStock);
    }

    public void AssertCustomerReconciled(Guid customerId, CustomerDto actual)
    {
        var expected = Customers[customerId].ExpectedBalancePaise;
        Assert.Equal(expected, actual.OutstandingCreditPaise);
    }

    public void AssertSupplierReconciled(Guid supplierId, SupplierBalanceLineDto actual)
    {
        var expected = Suppliers[supplierId];
        Assert.Equal(expected.TotalPurchasesPaise, actual.TotalPurchasesPaise);
        Assert.Equal(expected.TotalPaymentsPaise, actual.TotalPaidPaise);
        Assert.Equal(expected.ExpectedBalancePaise, actual.BalanceDuePaise);
    }

    public void AssertCashDrawerReconciled(Guid terminalId, CashSessionDto actual)
    {
        var expected = TerminalDrawers[terminalId];
        Assert.Equal(expected.OpeningFloatPaise, actual.OpeningFloatPaise);
        Assert.Equal(expected.TotalCashSalesPaise, actual.TotalCashSalesPaise);
        Assert.Equal(expected.TotalCashRepaymentsPaise, actual.TotalCashRepaymentsPaise);
        Assert.Equal(expected.TotalCashRefundsPaise, actual.TotalCashRefundsPaise);
        Assert.Equal(expected.ExpectedDrawerCashPaise, actual.ExpectedCashPaise);
    }

    public void AssertDailySalesSummaryReconciled(DailySalesSummaryReportDto actual)
    {
        Assert.Equal(TotalInvoicesCount, actual.TotalInvoicesCount);
        Assert.Equal(GrossSubtotalPaise, actual.GrossSubtotalPaise);
        Assert.Equal(TotalInvoiceDiscountsPaise, actual.TotalInvoiceDiscountsPaise);
        Assert.Equal(RoundingAdjustmentPaise, actual.RoundingAdjustmentPaise);
        Assert.Equal(GrossSalesPaise, actual.GrossSalesPaise);
        Assert.Equal(TotalRefundsCount, actual.TotalRefundsCount);
        Assert.Equal(TotalRefundsPaise, actual.TotalRefundsPaise);
        Assert.Equal(NetRevenuePaise, actual.NetRevenuePaise);
    }

    public void AssertPaymentSummaryReconciled(PaymentSummaryReportDto actual)
    {
        Assert.Equal(TotalCashPaise, actual.SalesTenders.CashPaise);
        Assert.Equal(TotalCardPaise, actual.SalesTenders.CardPaise);
        Assert.Equal(TotalUpiPaise, actual.SalesTenders.UpiPaise);
        Assert.Equal(TotalStoreCreditPaise, actual.SalesTenders.CreditPaise);
        Assert.Equal(TotalPaymentTendersPaise, actual.SalesTenders.TotalPaise);
    }

    public void AssertInvoiceSequenceContiguous()
    {
        Assert.NotEmpty(IssuedInvoiceNumbers);
        var distinct = IssuedInvoiceNumbers.Distinct().ToList();
        Assert.Equal(IssuedInvoiceNumbers.Count, distinct.Count); // Zero duplicates

        // Verify strictly formatted and monotonic
        for (int i = 0; i < IssuedInvoiceNumbers.Count; i++)
        {
            var parts = IssuedInvoiceNumbers[i].Split('-');
            Assert.Equal(3, parts.Length);
            Assert.Equal("INV", parts[0]);
            var seqNum = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(i + 1, seqNum); // Monotonic, 1-indexed contiguous
        }
    }
}
