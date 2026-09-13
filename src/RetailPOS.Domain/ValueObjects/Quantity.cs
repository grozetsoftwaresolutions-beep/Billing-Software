using System.Globalization;
using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.ValueObjects;

/// <summary>
/// Domain representation of product quantity using exact decimal math with unit-specific precision.
/// Strictly prohibits binary floating-point types.
/// </summary>
public readonly struct Quantity : IEquatable<Quantity>, IComparable<Quantity>
{
    public decimal Value { get; }
    public UnitOfMeasure Unit { get; }

    public bool IsZero => Value == 0m;
    public bool IsPositive => Value > 0m;
    public bool IsNegative => Value < 0m;

    public Quantity(decimal value, UnitOfMeasure unit)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));

        // Validate that precision does not exceed Unit.DecimalPlaces
        var rounded = Math.Round(value, unit.DecimalPlaces, MidpointRounding.AwayFromZero);
        if (value != rounded)
        {
            throw new DomainException(
                $"Quantity value {value} exceeds allowed precision of {unit.DecimalPlaces} decimal places for unit {unit.Code}.",
                "INVALID_QUANTITY_PRECISION");
        }

        Value = rounded;
    }

    public static Quantity From(decimal value, UnitOfMeasure unit) => new(value, unit);

    public static Quantity Zero(UnitOfMeasure unit) => new(0m, unit);

    public static Quantity operator +(Quantity left, Quantity right)
    {
        ValidateSameUnit(left, right);
        return new Quantity(left.Value + right.Value, left.Unit);
    }

    public static Quantity operator -(Quantity left, Quantity right)
    {
        ValidateSameUnit(left, right);
        return new Quantity(left.Value - right.Value, left.Unit);
    }

    public static Quantity operator -(Quantity quantity) =>
        new(-quantity.Value, quantity.Unit);

    private static void ValidateSameUnit(Quantity a, Quantity b)
    {
        if (a.Unit.Code != b.Unit.Code)
            throw new DomainException($"Unit mismatch: {a.Unit.Code} vs {b.Unit.Code}.", "UNIT_MISMATCH");
    }

    public bool Equals(Quantity other) =>
        Value == other.Value && Unit.Code == other.Unit.Code;

    public override bool Equals(object? obj) =>
        obj is Quantity other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Value, Unit.Code);

    public int CompareTo(Quantity other)
    {
        ValidateSameUnit(this, other);
        return Value.CompareTo(other.Value);
    }

    public static bool operator ==(Quantity left, Quantity right) => left.Equals(right);
    public static bool operator !=(Quantity left, Quantity right) => !left.Equals(right);
    public static bool operator <(Quantity left, Quantity right) => left.CompareTo(right) < 0;
    public static bool operator <=(Quantity left, Quantity right) => left.CompareTo(right) <= 0;
    public static bool operator >(Quantity left, Quantity right) => left.CompareTo(right) > 0;
    public static bool operator >=(Quantity left, Quantity right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        $"{Value.ToString("F" + Unit.DecimalPlaces, CultureInfo.InvariantCulture)} {Unit.Code}";
}
