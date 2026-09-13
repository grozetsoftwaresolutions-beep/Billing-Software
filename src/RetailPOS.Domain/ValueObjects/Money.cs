using System.Globalization;
using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.ValueObjects;

/// <summary>
/// Immutable monetary value stored strictly as integer minor units (e.g. paise for INR).
/// Absolute prohibition of float/double to prevent IEEE 754 precision errors.
/// </summary>
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    public long MinorUnits { get; }
    public Currency Currency { get; }

    public decimal AmountInMajorUnits => (decimal)MinorUnits / Currency.MinorUnitsPerMajorUnit;

    public bool IsZero => MinorUnits == 0;
    public bool IsPositive => MinorUnits > 0;
    public bool IsNegative => MinorUnits < 0;

    private Money(long minorUnits, Currency currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    public static Money FromMinorUnits(long minorUnits, Currency? currency = null)
    {
        return new Money(minorUnits, currency ?? Currency.INR);
    }

    public static Money FromDecimal(decimal amount, Currency? currency = null, MidpointRounding rounding = MidpointRounding.AwayFromZero)
    {
        var curr = currency ?? Currency.INR;
        var scaled = amount * curr.MinorUnitsPerMajorUnit;
        var roundedMinorUnits = (long)Math.Round(scaled, 0, rounding);
        return new Money(roundedMinorUnits, curr);
    }

    public static Money Zero(Currency? currency = null) =>
        new(0, currency ?? Currency.INR);

    public Money Abs() => new(Math.Abs(MinorUnits), Currency);

    public static Money operator +(Money left, Money right)
    {
        ValidateSameCurrency(left, right);
        return new Money(checked(left.MinorUnits + right.MinorUnits), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        ValidateSameCurrency(left, right);
        return new Money(checked(left.MinorUnits - right.MinorUnits), left.Currency);
    }

    public static Money operator -(Money value)
    {
        return new Money(checked(-value.MinorUnits), value.Currency);
    }

    public static Money operator *(Money money, decimal multiplier)
    {
        var rawResult = money.MinorUnits * multiplier;
        var rounded = (long)Math.Round(rawResult, 0, MidpointRounding.AwayFromZero);
        return new Money(rounded, money.Currency);
    }

    public static Money operator *(decimal multiplier, Money money) => money * multiplier;

    public static Money operator /(Money money, decimal divisor)
    {
        if (divisor == 0)
            throw new DivideByZeroException("Cannot divide Money by zero.");

        var rawResult = money.MinorUnits / divisor;
        var rounded = (long)Math.Round(rawResult, 0, MidpointRounding.AwayFromZero);
        return new Money(rounded, money.Currency);
    }

    private static void ValidateSameCurrency(Money a, Money b)
    {
        if (a.Currency.Code != b.Currency.Code)
            throw new DomainException($"Currency mismatch: {a.Currency.Code} vs {b.Currency.Code}.", "CURRENCY_MISMATCH");
    }

    public bool Equals(Money other) =>
        MinorUnits == other.MinorUnits && Currency.Code == other.Currency.Code;

    public override bool Equals(object? obj) =>
        obj is Money other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(MinorUnits, Currency.Code);

    public int CompareTo(Money other)
    {
        ValidateSameCurrency(this, other);
        return MinorUnits.CompareTo(other.MinorUnits);
    }

    public static bool operator ==(Money left, Money right) => left.Equals(right);
    public static bool operator !=(Money left, Money right) => !left.Equals(right);
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public override string ToString()
    {
        return $"{Currency.Symbol}{AmountInMajorUnits.ToString("F" + Currency.DecimalPlaces, CultureInfo.InvariantCulture)}";
    }
}
