using System.Diagnostics;
using System.Text;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.Hardware;

public sealed class BarcodeScannedEventArgs : EventArgs
{
    public string Barcode { get; }
    public BarcodeScanSource Source { get; }
    public double AverageInterKeyMs { get; }
    public double MaxInterKeyMs { get; }
    public int RawLength { get; }

    public BarcodeScannedEventArgs(
        string barcode,
        BarcodeScanSource source,
        double averageInterKeyMs,
        double maxInterKeyMs,
        int rawLength)
    {
        Barcode = barcode;
        Source = source;
        AverageInterKeyMs = averageInterKeyMs;
        MaxInterKeyMs = maxInterKeyMs;
        RawLength = rawLength;
    }
}

public sealed class KeyboardWedgeListener : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private readonly List<long> _timestamps = new();
    private readonly object _syncLock = new();
    private readonly int _maxInterKeyMs;
    private bool _disposed;

    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    public KeyboardWedgeListener(int maxInterKeyMs = 35)
    {
        _maxInterKeyMs = maxInterKeyMs > 0 ? maxInterKeyMs : 35;
    }

    public void ProcessKey(char c)
    {
        lock (_syncLock)
        {
            if (_disposed) return;

            var timestamp = Stopwatch.GetTimestamp();

            // End of barcode transmission: CR or LF
            if (c == '\r' || c == '\n')
            {
                if (_buffer.Length > 0)
                {
                    FlushBuffer();
                }
                return;
            }

            // If time since last keystroke exceeds 500ms and buffer was not flushed, reset buffer
            if (_timestamps.Count > 0)
            {
                var elapsedMs = (timestamp - _timestamps[^1]) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs > 500.0)
                {
                    _buffer.Clear();
                    _timestamps.Clear();
                }
            }

            _buffer.Append(c);
            _timestamps.Add(timestamp);
        }
    }

    public void ProcessKeyString(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var c in text)
        {
            ProcessKey(c);
        }
    }

    private void FlushBuffer()
    {
        var rawText = _buffer.ToString();
        var timestamps = _timestamps.ToArray();

        _buffer.Clear();
        _timestamps.Clear();

        if (string.IsNullOrWhiteSpace(rawText))
            return;

        // Calculate timing statistics
        double avgInterval = 0;
        double maxInterval = 0;

        if (timestamps.Length > 1)
        {
            var intervals = new List<double>();
            for (var i = 1; i < timestamps.Length; i++)
            {
                var deltaMs = (timestamps[i] - timestamps[i - 1]) * 1000.0 / Stopwatch.Frequency;
                intervals.Add(deltaMs);
            }

            avgInterval = intervals.Average();
            maxInterval = intervals.Max();
        }

        // Classification rule: hardware barcode scanner sends high-speed bursts (< 35ms avg, < 60ms max)
        var isScannerBurst = timestamps.Length > 1 && avgInterval < _maxInterKeyMs && maxInterval < 60.0;
        var source = isScannerBurst ? BarcodeScanSource.ScannerWedge : BarcodeScanSource.ManualEntry;

        var normalized = NormalizeBarcode(rawText);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(
                normalized,
                source,
                avgInterval,
                maxInterval,
                rawText.Length));
        }
    }

    public static string NormalizeBarcode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var trimmed = raw.Trim();

        // Strip STX (0x02) and ETX (0x03)
        trimmed = trimmed.Trim('\x02', '\x03');

        // Strip AIM symbology identifiers (e.g. ]E0, ]C1, ]e0, etc.)
        if (trimmed.StartsWith(']') && trimmed.Length >= 3)
        {
            trimmed = trimmed[3..];
        }

        // Clean control characters and trim whitespace
        var sb = new StringBuilder();
        foreach (var c in trimmed)
        {
            if (!char.IsControl(c) && !char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _disposed = true;
            _buffer.Clear();
            _timestamps.Clear();
        }
    }
}
