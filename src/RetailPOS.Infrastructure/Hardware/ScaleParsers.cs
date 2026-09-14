using System.Globalization;
using System.Text;
using RetailPOS.Application.Hardware;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class GenericAsciiScaleParser : IScaleProtocolParser
{
    // Standard format: ST,GS,+   1.250kg\r\n or US,GS,+   1.250kg\r\n
    public ScaleParseResult ParseFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty)
            return ScaleParseResult.Fail("Empty scale frame.");

        var raw = Encoding.ASCII.GetString(frame).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return ScaleParseResult.Fail("Blank scale frame.");

        var isStable = raw.StartsWith("ST", StringComparison.OrdinalIgnoreCase);
        var isTare = raw.Contains(",NT,", StringComparison.OrdinalIgnoreCase);

        // Find digits and unit
        var parts = raw.Split(',');
        var weightPart = parts.Length >= 3 ? parts[2] : parts[0];

        // Extract unit
        var unit = ScaleWeightUnit.Kilogram;
        if (raw.EndsWith("kg", StringComparison.OrdinalIgnoreCase))
        {
            unit = ScaleWeightUnit.Kilogram;
            weightPart = weightPart.Replace("kg", "", StringComparison.OrdinalIgnoreCase);
        }
        else if (raw.EndsWith("g", StringComparison.OrdinalIgnoreCase))
        {
            unit = ScaleWeightUnit.Gram;
            weightPart = weightPart.Replace("g", "", StringComparison.OrdinalIgnoreCase);
        }
        else if (raw.EndsWith("lb", StringComparison.OrdinalIgnoreCase))
        {
            unit = ScaleWeightUnit.Pound;
            weightPart = weightPart.Replace("lb", "", StringComparison.OrdinalIgnoreCase);
        }

        var cleanWeight = weightPart.Replace("+", "").Trim();
        if (!decimal.TryParse(cleanWeight, NumberStyles.Number, CultureInfo.InvariantCulture, out var val))
        {
            return ScaleParseResult.Fail($"Invalid numeric weight in frame '{raw}'.");
        }

        var isZero = val == 0m;
        return ScaleParseResult.Ok(val, unit, isStable, isZero, isTare, raw);
    }
}

public sealed class CasToledoScaleParser : IScaleProtocolParser
{
    // Fixed format: e.g. 01.250kg
    public ScaleParseResult ParseFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty)
            return ScaleParseResult.Fail("Empty scale frame.");

        var raw = Encoding.ASCII.GetString(frame).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return ScaleParseResult.Fail("Blank scale frame.");

        var unit = raw.EndsWith("g", StringComparison.OrdinalIgnoreCase) && !raw.EndsWith("kg", StringComparison.OrdinalIgnoreCase)
            ? ScaleWeightUnit.Gram
            : ScaleWeightUnit.Kilogram;

        var clean = raw.Replace("kg", "", StringComparison.OrdinalIgnoreCase).Replace("g", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var val))
        {
            return ScaleParseResult.Fail($"Invalid CAS/Toledo weight in frame '{raw}'.");
        }

        return ScaleParseResult.Ok(val, unit, true, val == 0m, false, raw);
    }
}
