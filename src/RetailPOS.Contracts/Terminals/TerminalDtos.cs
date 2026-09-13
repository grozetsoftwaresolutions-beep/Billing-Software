namespace RetailPOS.Contracts.Terminals;

public sealed record RegisterTerminalRequest(
    string TerminalCode,
    string TerminalName,
    string? ReceiptPrinterName = null);

public sealed record TerminalDto(
    Guid Id,
    string TerminalCode,
    string TerminalName,
    string? ReceiptPrinterName,
    bool IsActive);
