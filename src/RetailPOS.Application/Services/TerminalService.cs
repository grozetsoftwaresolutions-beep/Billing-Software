using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Application.Services;

public sealed class TerminalService : ITerminalService
{
    private readonly ITerminalRepository _terminalRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public TerminalService(
        ITerminalRepository terminalRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _terminalRepo = terminalRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<TerminalDto>> GetTerminalsAsync(CancellationToken cancellationToken = default)
    {
        var terminals = await _terminalRepo.GetAllAsync(cancellationToken);
        return terminals.Select(t => new TerminalDto(
            t.Id,
            t.TerminalCode,
            t.TerminalName,
            t.ReceiptPrinterName,
            t.IsActive)).ToList();
    }

    public async Task<ApiResponse<TerminalDto>> RegisterTerminalAsync(
        RegisterTerminalRequest request,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TerminalCode))
        {
            throw new DomainException("Terminal code cannot be empty.", "INVALID_TERMINAL_CODE");
        }

        if (string.IsNullOrWhiteSpace(request.TerminalName))
        {
            throw new DomainException("Terminal name cannot be empty.", "INVALID_TERMINAL_NAME");
        }

        // Terminal codes are case-insensitive, normalized to uppercase
        var normalizedCode = request.TerminalCode.Trim().ToUpperInvariant();

        var existing = await _terminalRepo.GetByCodeAsync(normalizedCode, cancellationToken);
        if (existing != null)
        {
            throw new DomainException($"Terminal code '{normalizedCode}' is already registered.", "TERMINAL_ALREADY_EXISTS");
        }

        var terminal = new Terminal(
            Guid.NewGuid(),
            normalizedCode,
            request.TerminalName.Trim(),
            request.ReceiptPrinterName?.Trim());

        await _terminalRepo.AddAsync(terminal, cancellationToken);
        await _auditRepo.RecordAsync("Terminals", "TERMINAL_REGISTERED", executingUsername, $"Registered terminal counter {normalizedCode}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var dto = new TerminalDto(terminal.Id, terminal.TerminalCode, terminal.TerminalName, terminal.ReceiptPrinterName, terminal.IsActive);
        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<TerminalDto>> SetTerminalStatusAsync(
        Guid terminalId,
        bool isActive,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        var terminal = await _terminalRepo.GetByIdAsync(terminalId, cancellationToken);
        if (terminal == null)
        {
            throw new DomainException("Terminal counter not found.", "TERMINAL_NOT_FOUND");
        }

        if (isActive)
        {
            terminal.Activate();
        }
        else
        {
            terminal.Deactivate();
        }

        var action = isActive ? "TERMINAL_ACTIVATED" : "TERMINAL_DEACTIVATED";
        await _auditRepo.RecordAsync("Terminals", action, executingUsername, $"Updated status of terminal {terminal.TerminalCode} to {(isActive ? "Active" : "Inactive")}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var dto = new TerminalDto(terminal.Id, terminal.TerminalCode, terminal.TerminalName, terminal.ReceiptPrinterName, terminal.IsActive);
        return ApiResponse.Ok(dto);
    }
}
