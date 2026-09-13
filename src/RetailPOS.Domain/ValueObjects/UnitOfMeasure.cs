namespace RetailPOS.Domain.ValueObjects;

/// <summary>
/// Unit of measurement defining unit-specific precision rules for retail products.
/// </summary>
public sealed record UnitOfMeasure
{
    public string Code { get; }
    public string Name { get; }
    public int DecimalPlaces { get; }
    public bool IsWeighed { get; }

    public UnitOfMeasure(string code, string name, int decimalPlaces, bool isWeighed)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Unit code cannot be empty.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Unit name cannot be empty.", nameof(name));
        if (decimalPlaces < 0 || decimalPlaces > 4)
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces), "Decimal places must be between 0 and 4.");

        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        DecimalPlaces = decimalPlaces;
        IsWeighed = isWeighed;
    }

    public static readonly UnitOfMeasure Piece = new("PC", "Piece", 0, false);
    public static readonly UnitOfMeasure Kilogram = new("KG", "Kilogram", 3, true);
    public static readonly UnitOfMeasure Gram = new("G", "Gram", 0, true);
    public static readonly UnitOfMeasure Litre = new("L", "Litre", 3, true);
    public static readonly UnitOfMeasure Millilitre = new("ML", "Millilitre", 0, true);

    public static UnitOfMeasure FromCode(string code) => code?.Trim().ToUpperInvariant() switch
    {
        "PC" or "PIECE" or "UNIT" or "NOS" => Piece,
        "KG" or "KILOGRAM" => Kilogram,
        "G" or "GM" or "GRAM" => Gram,
        "L" or "LTR" or "LITRE" => Litre,
        "ML" or "MILLILITRE" => Millilitre,
        _ => new UnitOfMeasure(code ?? "PC", code ?? "Piece", 3, true)
    };
}
