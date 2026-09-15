using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Entities.Terminals;

public sealed class Terminal : BaseEntity<Guid>
{
    public string TerminalCode { get; private set; } = null!;
    public string TerminalName { get; private set; } = null!;
    public string? ReceiptPrinterName { get; private set; }
    public bool IsActive { get; private set; }

    // Phase 12 Additions
    public TerminalStatus Status { get; private set; }
    public string? PairingTokenHash { get; private set; }
    public string? MachineName { get; private set; }
    public string? ClientIpAddress { get; private set; }
    public DateTime? LastHeartbeatUtc { get; private set; }

    private Terminal() { }

    public Terminal(
        Guid id,
        string terminalCode,
        string terminalName,
        string? receiptPrinterName = null,
        TerminalStatus status = TerminalStatus.Active,
        string? pairingTokenHash = null,
        string? machineName = null,
        string? clientIpAddress = null)
    {
        if (string.IsNullOrWhiteSpace(terminalCode))
            throw new DomainException("Terminal code cannot be empty.", "INVALID_TERMINAL_CODE");

        Id = id;
        TerminalCode = terminalCode.Trim().ToUpperInvariant();
        TerminalName = terminalName.Trim();
        ReceiptPrinterName = receiptPrinterName?.Trim();
        Status = status;
        IsActive = (status == TerminalStatus.Active);
        PairingTokenHash = pairingTokenHash;
        MachineName = machineName?.Trim();
        ClientIpAddress = clientIpAddress?.Trim();
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Activate()
    {
        EnsureNotDecommissioned();
        Status = TerminalStatus.Active;
        IsActive = true;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        EnsureNotDecommissioned();
        Status = TerminalStatus.Inactive;
        IsActive = false;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Decommission()
    {
        if (Status == TerminalStatus.Decommissioned)
            throw new DomainException("Terminal is already decommissioned.", "TERMINAL_ALREADY_DECOMMISSIONED");

        Status = TerminalStatus.Decommissioned;
        IsActive = false;
        PairingTokenHash = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Enroll(string pairingTokenHash, string? machineName, string? clientIpAddress)
    {
        EnsureNotDecommissioned();
        if (string.IsNullOrWhiteSpace(pairingTokenHash))
            throw new DomainException("Pairing token hash cannot be empty.", "INVALID_PAIRING_TOKEN_HASH");

        PairingTokenHash = pairingTokenHash.Trim();
        MachineName = machineName?.Trim() ?? MachineName;
        ClientIpAddress = clientIpAddress?.Trim() ?? ClientIpAddress;
        Status = TerminalStatus.Active;
        IsActive = true;
        LastHeartbeatUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RecordHeartbeat(string? clientIpAddress, DateTime timestamp)
    {
        EnsureNotDecommissioned();
        if (Status != TerminalStatus.Active)
            throw new DomainException("Inactive terminal cannot record heartbeat.", "TERMINAL_NOT_ACTIVE");

        LastHeartbeatUtc = timestamp;
        if (!string.IsNullOrWhiteSpace(clientIpAddress))
            ClientIpAddress = clientIpAddress.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RotatePairingToken(string newPairingTokenHash)
    {
        EnsureNotDecommissioned();
        if (string.IsNullOrWhiteSpace(newPairingTokenHash))
            throw new DomainException("New pairing token hash cannot be empty.", "INVALID_PAIRING_TOKEN_HASH");

        PairingTokenHash = newPairingTokenHash.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    private void EnsureNotDecommissioned()
    {
        if (Status == TerminalStatus.Decommissioned)
            throw new DomainException("Decommissioned terminal cannot be modified or reactivated.", "TERMINAL_DECOMMISSIONED");
    }
}
