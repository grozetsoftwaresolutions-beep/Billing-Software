using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Infrastructure.Persistence.Converters;

/// <summary>
/// Authoritative EF Core converter storing Money as 64-bit integer minor units (paise in INR).
/// Prohibits any floating-point representation in SQLite.
/// </summary>
public sealed class MoneyToMinorUnitsConverter : ValueConverter<Money, long>
{
    public MoneyToMinorUnitsConverter()
        : base(
            money => money.MinorUnits,
            minorUnits => Money.FromMinorUnits(minorUnits, Currency.INR))
    {
    }
}
