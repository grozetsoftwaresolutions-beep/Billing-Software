using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Hardware;

public sealed record PrintResult(bool Success, string? ErrorMessage = null)
{
    public static PrintResult Ok() => new(true);
    public static PrintResult Fail(string error) => new(false, error);
}

public sealed record DrawerKickResult(bool Success, string DrawerType, string? ErrorMessage = null)
{
    public static DrawerKickResult Ok(string type) => new(true, type);
    public static DrawerKickResult Fail(string type, string error) => new(false, type, error);
}

public sealed record DeviceStatusResult(bool IsConnected, string StatusMessage)
{
    public static DeviceStatusResult Online(string msg = "Online") => new(true, msg);
    public static DeviceStatusResult Offline(string msg = "Offline") => new(false, msg);
}

public sealed record ScaleWeightReadingResult(
    decimal Weight,
    ScaleWeightUnit Unit,
    bool IsStable,
    bool IsZero,
    bool IsTare,
    string RawReading,
    bool Success,
    string? ErrorMessage = null)
{
    public static ScaleWeightReadingResult Ok(decimal weight, ScaleWeightUnit unit, bool stable, bool zero, bool tare, string raw) =>
        new(weight, unit, stable, zero, tare, raw, true);

    public static ScaleWeightReadingResult Fail(string error) =>
        new(0m, ScaleWeightUnit.Kilogram, false, false, false, string.Empty, false, error);
}

public sealed record ScaleParseResult(
    bool Success,
    decimal Weight,
    ScaleWeightUnit Unit,
    bool IsStable,
    bool IsZero,
    bool IsTare,
    string RawReading,
    string? ErrorMessage = null)
{
    public static ScaleParseResult Ok(decimal weight, ScaleWeightUnit unit, bool stable, bool zero, bool tare, string raw) =>
        new(true, weight, unit, stable, zero, tare, raw);

    public static ScaleParseResult Fail(string error) =>
        new(false, 0m, ScaleWeightUnit.Kilogram, false, false, false, string.Empty, error);
}

public sealed record BarcodeScanClassification(
    BarcodeScanSource Source,
    string NormalizedBarcode,
    long AverageIntervalMs,
    long MaxIntervalMs,
    bool IsValid);

public interface IReceiptPrinter
{
    Task<PrintResult> PrintRawBytesAsync(byte[] bytes, CancellationToken cancellationToken = default);
    Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default);
}

public interface ICashDrawer
{
    Task<DrawerKickResult> OpenDrawerAsync(string reason, CancellationToken cancellationToken = default);
}

public interface IWeighingScale
{
    Task<ScaleWeightReadingResult> ReadWeightAsync(CancellationToken cancellationToken = default);
    Task<bool> ZeroAsync(CancellationToken cancellationToken = default);
    Task<bool> TareAsync(CancellationToken cancellationToken = default);
    Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default);
}

public interface IBarcodeScanner
{
    BarcodeScanClassification ClassifyInput(IReadOnlyList<(char Key, long IntervalMs)> keystrokes);
}

public interface IScaleProtocolParser
{
    ScaleParseResult ParseFrame(ReadOnlySpan<byte> frame);
}
