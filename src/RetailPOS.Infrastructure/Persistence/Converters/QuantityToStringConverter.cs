using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Infrastructure.Persistence.Converters;

/// <summary>
/// Authoritative converter storing Quantity as deterministic invariant string "{Value}|{UnitCode}".
/// Completely eliminates binary floating-point rounding errors and preserves configured retail precision.
/// </summary>
public sealed class QuantityToStringConverter : ValueConverter<Quantity, string>
{
    public QuantityToStringConverter()
        : base(
            q => $"{q.Value.ToString(CultureInfo.InvariantCulture)}|{q.Unit.Code}",
            s => ParseQuantity(s))
    {
    }

    private static Quantity ParseQuantity(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Quantity.Zero(UnitOfMeasure.Piece);
        }

        var parts = value.Split('|');
        if (parts.Length == 2 && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalVal))
        {
            var unit = UnitOfMeasure.FromCode(parts[1]);
            return new Quantity(decimalVal, unit);
        }

        // Fallback for standalone numeric string
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
        {
            return new Quantity(num, UnitOfMeasure.Piece);
        }

        return Quantity.Zero(UnitOfMeasure.Piece);
    }
}
