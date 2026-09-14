using System.Text;
using RetailPOS.Application.Hardware;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class KeyboardWedgeParser : IBarcodeScanner
{
    private readonly int _maxAvgIntervalMs;
    private readonly int _maxSingleIntervalMs;

    public KeyboardWedgeParser(int maxAvgIntervalMs = 35, int maxSingleIntervalMs = 60)
    {
        _maxAvgIntervalMs = maxAvgIntervalMs;
        _maxSingleIntervalMs = maxSingleIntervalMs;
    }

    public BarcodeScanClassification ClassifyInput(IReadOnlyList<(char Key, long IntervalMs)> keystrokes)
    {
        if (keystrokes == null || keystrokes.Count == 0)
        {
            return new BarcodeScanClassification(BarcodeScanSource.ManualEntry, string.Empty, 0, 0, false);
        }

        var sb = new StringBuilder();
        long totalInterval = 0;
        long maxInterval = 0;
        var count = 0;

        foreach (var (key, interval) in keystrokes)
        {
            // Ignore delimiters in barcode text
            if (key is '\r' or '\n' or (char)0x02 or (char)0x03)
                continue;

            sb.Append(key);
            if (count > 0)
            {
                totalInterval += interval;
                if (interval > maxInterval)
                    maxInterval = interval;
            }
            count++;
        }

        var rawBarcode = sb.ToString().Trim();
        // Strip AIM symbology identifiers (e.g. ]E0, ]C1, ]A0)
        if (rawBarcode.Length > 3 && rawBarcode.StartsWith(']') && char.IsLetterOrDigit(rawBarcode[1]) && char.IsDigit(rawBarcode[2]))
        {
            rawBarcode = rawBarcode[3..];
        }

        var avgInterval = count > 1 ? totalInterval / (count - 1) : 0;
        var isScanner = count >= 3 && avgInterval <= _maxAvgIntervalMs && maxInterval <= _maxSingleIntervalMs;
        var source = isScanner ? BarcodeScanSource.ScannerWedge : BarcodeScanSource.ManualEntry;

        var isValid = rawBarcode.Length >= 3 && rawBarcode.Length <= 64 && !rawBarcode.Contains(' ');

        return new BarcodeScanClassification(source, rawBarcode, avgInterval, maxInterval, isValid);
    }
}
