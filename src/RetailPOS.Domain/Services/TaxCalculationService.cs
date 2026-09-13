using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Services;

public sealed record TaxCalculationResult(
    Money TaxableAmount,
    Money TotalTaxAmount,
    IReadOnlyList<(string ComponentName, decimal RatePercentage, Money Amount)> ComponentBreakdown);

/// <summary>
/// Domain service providing authoritative tax-inclusive and tax-exclusive calculations.
/// Enforces 100% component reconciliation (e.g. CGST + SGST == TotalTax) with zero discrepancies.
/// </summary>
public static class TaxCalculationService
{
    public static TaxCalculationResult Calculate(
        Money netLineAmount,
        TaxCategory? taxCategory,
        PricingModel pricingModel)
    {
        var currency = netLineAmount.Currency;

        if (taxCategory == null || taxCategory.IsExempt || taxCategory.TotalRatePercentage == 0m || netLineAmount.IsZero)
        {
            return new TaxCalculationResult(netLineAmount, Money.Zero(currency), []);
        }

        var totalRate = taxCategory.TotalRatePercentage;

        if (pricingModel == PricingModel.TaxInclusive)
        {
            // Formula: Taxable = Gross / (1 + Rate / 100)
            // TotalTax = Gross - Taxable
            var rateFraction = 1m + (totalRate / 100m);
            var rawTaxable = (decimal)netLineAmount.MinorUnits / rateFraction;
            var taxableMinorUnits = RoundingPolicy.RoundToNearestMinorUnit(rawTaxable);
            var taxableMoney = Money.FromMinorUnits(taxableMinorUnits, currency);

            var totalTaxMoney = netLineAmount - taxableMoney;
            var breakdowns = AllocateComponentTaxes(totalTaxMoney, taxableMoney, taxCategory);

            return new TaxCalculationResult(taxableMoney, totalTaxMoney, breakdowns);
        }
        else
        {
            // TaxExclusive: Net amount is the taxable base. Tax added on top.
            // Formula: Tax = Base * (Rate / 100)
            var rawTax = (netLineAmount.MinorUnits * totalRate) / 100m;
            var taxMinorUnits = RoundingPolicy.RoundToNearestMinorUnit(rawTax);
            var totalTaxMoney = Money.FromMinorUnits(taxMinorUnits, currency);

            var breakdowns = AllocateComponentTaxes(totalTaxMoney, netLineAmount, taxCategory);

            return new TaxCalculationResult(netLineAmount, totalTaxMoney, breakdowns);
        }
    }

    private static List<(string ComponentName, decimal RatePercentage, Money Amount)> AllocateComponentTaxes(
        Money totalTaxMoney,
        Money taxableAmount,
        TaxCategory category)
    {
        var results = new List<(string, decimal, Money)>();
        if (category.Components.Count == 0) return results;

        long allocatedMinorUnits = 0;
        for (int i = 0; i < category.Components.Count; i++)
        {
            var component = category.Components[i];
            if (i == category.Components.Count - 1)
            {
                // Last component gets the exact remainder to ensure Sum(components) == TotalTax to the exact paise
                var remainder = totalTaxMoney.MinorUnits - allocatedMinorUnits;
                results.Add((component.Name, component.RatePercentage, Money.FromMinorUnits(remainder, taxableAmount.Currency)));
            }
            else
            {
                var rawComponentTax = (taxableAmount.MinorUnits * component.RatePercentage) / 100m;
                var roundedUnits = RoundingPolicy.RoundToNearestMinorUnit(rawComponentTax);
                allocatedMinorUnits += roundedUnits;
                results.Add((component.Name, component.RatePercentage, Money.FromMinorUnits(roundedUnits, taxableAmount.Currency)));
            }
        }
        return results;
    }
}
