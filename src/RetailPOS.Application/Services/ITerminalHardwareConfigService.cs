using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Entities.Hardware;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public interface ITerminalHardwareConfigService
{
    Task<ApiResponse<TerminalHardwareConfigDto>> GetConfigAsync(Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<TerminalHardwareConfigDto>> UpdateConfigAsync(Guid terminalId, UpdateTerminalHardwareConfigRequest request, Guid userId, CancellationToken cancellationToken = default);
}

public sealed class TerminalHardwareConfigService : ITerminalHardwareConfigService
{
    private readonly ITerminalHardwareConfigRepository _repo;
    private readonly ITerminalRepository _terminalRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public TerminalHardwareConfigService(
        ITerminalHardwareConfigRepository repo,
        ITerminalRepository terminalRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _terminalRepo = terminalRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<TerminalHardwareConfigDto>> GetConfigAsync(Guid terminalId, CancellationToken cancellationToken = default)
    {
        var terminal = await _terminalRepo.GetByIdAsync(terminalId, cancellationToken);
        if (terminal == null || !terminal.IsActive)
            return ApiResponse.Fail<TerminalHardwareConfigDto>($"Terminal with ID '{terminalId}' not found or inactive.", "TERMINAL_NOT_FOUND");

        var config = await _repo.GetByTerminalIdAsync(terminalId, cancellationToken);
        if (config == null)
        {
            config = new TerminalHardwareConfig(terminalId);
            await _repo.AddAsync(config, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return ApiResponse.Ok(MapToDto(config));
    }

    public async Task<ApiResponse<TerminalHardwareConfigDto>> UpdateConfigAsync(
        Guid terminalId,
        UpdateTerminalHardwareConfigRequest request,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var terminal = await _terminalRepo.GetByIdAsync(terminalId, cancellationToken);
        if (terminal == null || !terminal.IsActive)
            return ApiResponse.Fail<TerminalHardwareConfigDto>($"Terminal with ID '{terminalId}' not found or inactive.", "TERMINAL_NOT_FOUND");

        var config = await _repo.GetByTerminalIdAsync(terminalId, cancellationToken);
        if (config == null)
        {
            config = new TerminalHardwareConfig(terminalId);
            await _repo.AddAsync(config, cancellationToken);
        }

        if (request.PrinterConnectionType.HasValue || !string.IsNullOrWhiteSpace(request.PrinterNameOrAddress) || request.PaperWidth.HasValue)
        {
            config.UpdatePrinter(
                request.PrinterConnectionType ?? config.PrinterConnectionType,
                request.PrinterNameOrAddress ?? config.PrinterNameOrAddress,
                request.PaperWidth ?? config.PaperWidth,
                request.PrinterEncoding ?? config.PrinterEncoding,
                request.PrinterCodePage ?? config.PrinterCodePage,
                request.PrinterTimeoutMs ?? config.PrinterTimeoutMs,
                request.AutoCut ?? config.AutoCut,
                request.OpenDrawerOnCashSale ?? config.OpenDrawerOnCashSale);
        }

        if (request.DrawerConnectionType.HasValue || request.DrawerKickPin.HasValue)
        {
            config.UpdateDrawer(
                request.DrawerConnectionType ?? config.DrawerConnectionType,
                request.DrawerKickPin ?? config.DrawerKickPin,
                request.DrawerPulseOnMs ?? config.DrawerPulseOnMs,
                request.DrawerPulseOffMs ?? config.DrawerPulseOffMs);
        }

        if (request.ScannerMaxInterKeyMs.HasValue)
        {
            config.UpdateScanner(request.ScannerMaxInterKeyMs.Value);
        }

        if (request.ScaleConnectionType.HasValue || request.IsScaleEnabled.HasValue)
        {
            config.UpdateScale(
                request.ScaleConnectionType ?? config.ScaleConnectionType,
                request.ScalePortName ?? config.ScalePortName,
                request.ScaleBaudRate ?? config.ScaleBaudRate,
                request.ScaleDataBits ?? config.ScaleDataBits,
                request.ScaleParity ?? config.ScaleParity,
                request.ScaleStopBits ?? config.ScaleStopBits,
                request.ScaleProtocol ?? config.ScaleProtocol,
                request.ScaleTimeoutMs ?? config.ScaleTimeoutMs,
                request.IsScaleEnabled ?? config.IsScaleEnabled);
        }

        await _repo.UpdateAsync(config, cancellationToken);
        await _auditRepo.RecordAsync("Terminal", "HARDWARE_CONFIG_UPDATED", userId.ToString(), $"[Terminal:{terminalId}] Hardware configuration updated", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(config));
    }

    private static TerminalHardwareConfigDto MapToDto(TerminalHardwareConfig c) => new(
        c.TerminalId,
        c.PrinterConnectionType,
        c.PrinterNameOrAddress,
        c.PaperWidth,
        c.PrinterEncoding,
        c.PrinterCodePage,
        c.PrinterTimeoutMs,
        c.AutoCut,
        c.OpenDrawerOnCashSale,
        c.DrawerConnectionType,
        c.DrawerKickPin,
        c.DrawerPulseOnMs,
        c.DrawerPulseOffMs,
        c.ScannerMaxInterKeyMs,
        c.ScaleConnectionType,
        c.ScalePortName,
        c.ScaleBaudRate,
        c.ScaleDataBits,
        c.ScaleParity,
        c.ScaleStopBits,
        c.ScaleProtocol,
        c.ScaleTimeoutMs,
        c.IsScaleEnabled,
        c.UpdatedAtUtc);
}
