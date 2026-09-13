using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Terminals;

public sealed class Terminal : BaseEntity<Guid>
{
    public string TerminalCode { get; private set; } = null!;
    public string TerminalName { get; private set; } = null!;
    public string? ReceiptPrinterName { get; private set; }
    public bool IsActive { get; private set; }

    private Terminal() { }

    public Terminal(Guid id, string terminalCode, string terminalName, string? receiptPrinterName = null)
    {
        if (string.IsNullOrWhiteSpace(terminalCode))
            throw new DomainException("Terminal code cannot be empty.", "INVALID_TERMINAL_CODE");

        Id = id;
        TerminalCode = terminalCode.Trim().ToUpperInvariant();
        TerminalName = terminalName.Trim();
        ReceiptPrinterName = receiptPrinterName?.Trim();
        IsActive = true;
    }

    public void Deactivate() => IsActive = false;
}
