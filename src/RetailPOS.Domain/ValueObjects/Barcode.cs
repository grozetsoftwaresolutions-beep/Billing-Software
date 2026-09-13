using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.ValueObjects;

/// <summary>
/// Validated barcode value object.
/// </summary>
public sealed record Barcode
{
    public string Value { get; }

    public Barcode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("Barcode cannot be empty or whitespace.", "INVALID_BARCODE");

        var clean = value.Trim();
        if (clean.Length < 3 || clean.Length > 64)
            throw new DomainException($"Barcode length must be between 3 and 64 characters (got {clean.Length}).", "INVALID_BARCODE_LENGTH");

        Value = clean;
    }

    public static implicit operator string(Barcode barcode) => barcode.Value;
    public override string ToString() => Value;
}
