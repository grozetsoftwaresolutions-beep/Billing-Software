namespace RetailPOS.Domain.ValueObjects;

/// <summary>
/// Definition of a currency and its minor units. Default is INR (Indian Rupee).
/// </summary>
public sealed record Currency
{
    public string Code { get; }
    public string Symbol { get; }
    public int MinorUnitsPerMajorUnit { get; }
    public int DecimalPlaces { get; }

    public Currency(string code, string symbol, int minorUnitsPerMajorUnit, int decimalPlaces)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Currency code cannot be empty.", nameof(code));
        if (minorUnitsPerMajorUnit <= 0)
            throw new ArgumentException("Minor units per major unit must be positive.", nameof(minorUnitsPerMajorUnit));
        if (decimalPlaces < 0 || decimalPlaces > 4)
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces), "Decimal places must be between 0 and 4.");

        Code = code.Trim().ToUpperInvariant();
        Symbol = symbol ?? string.Empty;
        MinorUnitsPerMajorUnit = minorUnitsPerMajorUnit;
        DecimalPlaces = decimalPlaces;
    }

    public static readonly Currency INR = new("INR", "?", 100, 2);
}
