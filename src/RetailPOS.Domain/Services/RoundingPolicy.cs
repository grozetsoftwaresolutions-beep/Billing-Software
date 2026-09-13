using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Services;

/// <summary>
/// Centralized authoritative rounding policy for all financial calculations.
/// Standardizes MidpointRounding to AwayFromZero across currency, tax, and line amounts.
/// </summary>
public static class RoundingPolicy
{
    public const MidpointRounding DefaultMidpointRounding = MidpointRounding.AwayFromZero;

    /// <summary>
    /// Rounds a decimal value to the specified number of decimal places using AwayFromZero.
    /// </summary>
    public static decimal Round(decimal value, int decimalPlaces = 2) =>
        Math.Round(value, decimalPlaces, DefaultMidpointRounding);

    /// <summary>
    /// Rounds a raw decimal minor unit calculation to the nearest whole integer minor unit (e.g. nearest paise).
    /// </summary>
    public static long RoundToNearestMinorUnit(decimal rawMinorUnits) =>
        (long)Math.Round(rawMinorUnits, 0, DefaultMidpointRounding);

    /// <summary>
    /// Rounds a Money amount to the nearest whole major unit (e.g. rounding ?123.45 to ?123.00, or ?123.50 to ?124.00).
    /// </summary>
    public static Money RoundToNearestMajorUnit(Money money)
    {
        var majorDecimal = (decimal)money.MinorUnits / money.Currency.MinorUnitsPerMajorUnit;
        var roundedMajor = (long)Math.Round(majorDecimal, 0, DefaultMidpointRounding);
        return Money.FromMinorUnits(roundedMajor * money.Currency.MinorUnitsPerMajorUnit, money.Currency);
    }
}
