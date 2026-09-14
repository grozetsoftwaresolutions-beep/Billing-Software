using RetailPOS.Domain.Entities.Hardware;

namespace RetailPOS.Application.Persistence;

public interface ITerminalHardwareConfigRepository
{
    Task<TerminalHardwareConfig?> GetByTerminalIdAsync(Guid terminalId, CancellationToken cancellationToken = default);
    Task AddAsync(TerminalHardwareConfig config, CancellationToken cancellationToken = default);
    Task UpdateAsync(TerminalHardwareConfig config, CancellationToken cancellationToken = default);
}
