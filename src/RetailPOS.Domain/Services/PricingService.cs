using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Services;

public sealed record PricedLineResult(
    Quantity Quantity,
    Money UnitPrice,
    Money GrossAmount,
    Money DiscountAmount,
    Money NetBeforeTax,
    Money TaxableAmount,
    Money TaxAmount,
    IReadOnlyList<(string ComponentName, decimal RatePercentage, Money Amount)> TaxComponents,
    Money LineTotal);

public sealed record PricedInvoiceResult(
    IReadOnlyList<PricedLineResult> Lines,
    Money GrossSubtotal,
    Money TotalLineDiscounts,
    Money InvoiceDiscount,
    Money TaxableSubtotal,
    Money TotalTax,
    IReadOnlyList<(string ComponentName, Money Amount)> TaxComponentTotals,
    Money RoundingAdjustment,
    Money FinalPayable);

public interface IPricingCalculator
{
    PricedLineResult CalculateLine(
        Quantity quantity,
        Money unitPrice,
        TaxCategory? taxCategory,
        PricingModel pricingModel,
        DiscountType discountType = DiscountType.None,
        decimal discountValue = 0m);

    PricedInvoiceResult CalculateInvoice(
        IEnumerable<PricedLineResult> lines,
        Money? invoiceDiscount = null,
        bool roundToWholeRupee = false);
}

/// <summary>
/// Authoritative calculation engine for sales, POS carts, purchases, and invoices.
/// Enforces consistent calculation order across the entire application.
/// </summary>
public sealed class PricingCalculator : IPricingCalculator
{
    public PricedLineResult CalculateLine(
        Quantity quantity,
        Money unitPrice,
        TaxCategory? taxCategory,
        PricingModel pricingModel,
        DiscountType discountType = DiscountType.None,
        decimal discountValue = 0m)
    {
        // 1. Base gross amount = Quantity * UnitPrice
        var grossAmount = unitPrice * quantity.Value;

        // 2. Line discount
        var discountAmount = DiscountCalculation.CalculateDiscount(grossAmount, discountType, discountValue);

        // 3. Net amount after discount
        var netAmount = grossAmount - discountAmount;

        // 4. Tax calculation
        var taxResult = TaxCalculationService.Calculate(netAmount, taxCategory, pricingModel);

        // 5. Line total
        Money lineTotal = pricingModel == PricingModel.TaxInclusive
            ? netAmount // already includes tax
            : netAmount + taxResult.TotalTaxAmount;

        return new PricedLineResult(
            quantity,
            unitPrice,
            grossAmount,
            discountAmount,
            netAmount,
            taxResult.TaxableAmount,
            taxResult.TotalTaxAmount,
            taxResult.ComponentBreakdown,
            lineTotal);
    }

    public PricedInvoiceResult CalculateInvoice(
        IEnumerable<PricedLineResult> lines,
        Money? invoiceDiscount = null,
        bool roundToWholeRupee = false)
    {
        var lineList = lines.ToList();
        var currency = lineList.FirstOrDefault()?.UnitPrice.Currency ?? Currency.INR;

        var grossSubtotal = lineList.Aggregate(Money.Zero(currency), (sum, l) => sum + l.GrossAmount);
        var totalLineDiscounts = lineList.Aggregate(Money.Zero(currency), (sum, l) => sum + l.DiscountAmount);
        var taxableSubtotal = lineList.Aggregate(Money.Zero(currency), (sum, l) => sum + l.TaxableAmount);
        var totalTax = lineList.Aggregate(Money.Zero(currency), (sum, l) => sum + l.TaxAmount);

        var invDiscount = invoiceDiscount ?? Money.Zero(currency);
        if (invDiscount.MinorUnits > (grossSubtotal - totalLineDiscounts).MinorUnits)
        {
            invDiscount = grossSubtotal - totalLineDiscounts;
        }

        // Aggregate component taxes
        var componentTotals = lineList
            .SelectMany(l => l.TaxComponents)
            .GroupBy(c => c.ComponentName)
            .Select(g => (g.Key, g.Aggregate(Money.Zero(currency), (s, c) => s + c.Amount)))
            .ToList();

        var unroundedTotal = (grossSubtotal - totalLineDiscounts - invDiscount) + (totalTax - lineList.Where(l => l.LineTotal == l.NetBeforeTax).Aggregate(Money.Zero(currency), (s, l) => s + l.TaxAmount));
        
        // Accurate total based on line totals sum
        var linesTotalSum = lineList.Aggregate(Money.Zero(currency), (sum, l) => sum + l.LineTotal) - invDiscount;

        Money finalPayable;
        Money roundingAdj;

        if (roundToWholeRupee)
        {
            finalPayable = RoundingPolicy.RoundToNearestMajorUnit(linesTotalSum);
            roundingAdj = finalPayable - linesTotalSum;
        }
        else
        {
            finalPayable = linesTotalSum;
            roundingAdj = Money.Zero(currency);
        }

        return new PricedInvoiceResult(
            lineList,
            grossSubtotal,
            totalLineDiscounts,
            invDiscount,
            taxableSubtotal,
            totalTax,
            componentTotals,
            roundingAdj,
            finalPayable);
    }
}
