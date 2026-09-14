using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Hardware;

public sealed record DrawerKickDirective(
    bool ShouldKick,
    string? Reason = null,
    long? AmountPaise = null);

public sealed record ReceiptPrintResponseDto(
    Guid InvoiceId,
    string InvoiceNumber,
    PrintStatus Status,
    string? ErrorMessage,
    bool IsReprint,
    int ReprintCount,
    DateTime PrintedAtUtc);

public sealed record RenderedReceiptPayloadDto(
    Guid InvoiceId,
    string InvoiceNumber,
    byte[] RawBytes,
    string AsciiPreview,
    int PendingReprintCount);

public sealed record ConfirmReprintRequest(
    string? Reason = null);

public sealed record ReportReprintFailedRequest(
    string ErrorMessage);

public sealed record TestPrintRequestDto(
    string? PrinterName = null,
    PrinterConnectionType ConnectionType = PrinterConnectionType.Simulated,
    PaperWidth PaperWidth = PaperWidth.Width80mm,
    bool OpenDrawer = false,
    string? CustomHeader = null);

public sealed record TestPrintResponseDto(
    bool Success,
    string PrinterName,
    PaperWidth PaperWidth,
    int BytesSent,
    string? ErrorMessage,
    DateTime TimestampUtc);

public sealed record DrawerKickRequestDto(
    string Reason,
    Guid TerminalId);

public sealed record DrawerKickResponseDto(
    bool Success,
    string DrawerType,
    string? ErrorMessage,
    DateTime TimestampUtc);

public sealed record ScaleWeightReadingDto(
    decimal Weight,
    ScaleWeightUnit Unit,
    bool IsStable,
    bool IsZero,
    bool IsTare,
    string RawReading,
    DateTime TimestampUtc);

public sealed record BarcodeLookupResultDto(
    string Barcode,
    bool Found,
    Guid? ProductId,
    string? Sku,
    string? ProductName,
    long SellingPricePaise,
    string? Unit,
    int? TaxCategoryId,
    string Message);

public sealed record HardwareStatusDto(
    bool IsOverallHealthy,
    string PrinterStatus,
    string DrawerStatus,
    string ScaleStatus,
    string ScannerStatus,
    string ActivePrinterName,
    PaperWidth ActivePaperWidth);
