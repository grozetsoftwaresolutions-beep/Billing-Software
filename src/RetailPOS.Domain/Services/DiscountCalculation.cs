using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Services;

/// <summary>
/// Explicit discount calculation rules ensuring discounts never produce negative balances.
/// </summary>
public static class DiscountCalculation
{
    public static Money CalculateDiscount(
        Money baseAmount,
        DiscountType discountType,
        decimal discountValue,
        decimal maxAllowedPercentage = 100m)
    {
        if (baseAmount.IsZero || discountType == DiscountType.None || discountValue <= 0m)
            return Money.Zero(baseAmount.Currency);

        if (baseAmount.IsNegative)
            throw new DomainException("Cannot calculate discount on negative amount.", "INVALID_DISCOUNT_BASE");

        return discountType switch
        {
            DiscountType.Percentage => CalculatePercentageDiscount(baseAmount, discountValue, maxAllowedPercentage),
            DiscountType.FixedAmount => CalculateFixedDiscount(baseAmount, discountValue),
            _ => Money.Zero(baseAmount.Currency)
        };
    }

    private static Money CalculatePercentageDiscount(Money baseAmount, decimal percentage, decimal maxAllowedPercentage)
    {
        if (percentage < 0m || percentage > 100m)
            throw new DomainException($"Discount percentage must be between 0 and 100 (got {percentage}%).", "INVALID_DISCOUNT_PERCENTAGE");

        if (percentage > maxAllowedPercentage)
            throw new DomainException($"Discount percentage of {percentage}% exceeds maximum allowed of {maxAllowedPercentage}%.", "DISCOUNT_EXCEEDS_MAX");

        var rawDiscount = (baseAmount.MinorUnits * percentage) / 100m;
        var roundedPaise = RoundingPolicy.RoundToNearestMinorUnit(rawDiscount);

        // Cap at base amount to prevent negative total
        var cappedPaise = Math.Min(roundedPaise, baseAmount.MinorUnits);
        return Money.FromMinorUnits(cappedPaise, baseAmount.Currency);
    }

    private static Money CalculateFixedDiscount(Money baseAmount, decimal fixedMajorAmount)
    {
        if (fixedMajorAmount < 0m)
            throw new DomainException("Fixed discount amount cannot be negative.", "INVALID_FIXED_DISCOUNT");

        var fixedMoney = Money.FromDecimal(fixedMajorAmount, baseAmount.Currency);

        // Fixed discount cannot exceed the base line amount
        if (fixedMoney.MinorUnits > baseAmount.MinorUnits)
        {
            return baseAmount; // Capped at 100% discount
        }

        return fixedMoney;
    }
}
