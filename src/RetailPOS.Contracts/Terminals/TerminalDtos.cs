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

public sealed class F2RecoveryArtifactDto
{
    public Guid StoreId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid RotationOperationId { get; set; }
    public string CandidateF2Fingerprint { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public DateTime ValidUntilUtc { get; set; }
    public string Version { get; set; } = "5.2.9.6";
    public string SignatureBase64 { get; set; } = string.Empty;
}
