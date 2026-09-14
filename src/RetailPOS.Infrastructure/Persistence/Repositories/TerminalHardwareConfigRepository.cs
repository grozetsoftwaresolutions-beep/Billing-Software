using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Hardware;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class TerminalHardwareConfigRepository : ITerminalHardwareConfigRepository
{
    private readonly RetailDbContext _context;

    public TerminalHardwareConfigRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<TerminalHardwareConfig?> GetByTerminalIdAsync(Guid terminalId, CancellationToken cancellationToken = default)
    {
        return await _context.TerminalHardwareConfigs.FirstOrDefaultAsync(c => c.Id == terminalId, cancellationToken);
    }

    public async Task AddAsync(TerminalHardwareConfig config, CancellationToken cancellationToken = default)
    {
        await _context.TerminalHardwareConfigs.AddAsync(config, cancellationToken);
    }

    public Task UpdateAsync(TerminalHardwareConfig config, CancellationToken cancellationToken = default)
    {
        _context.TerminalHardwareConfigs.Update(config);
        return Task.CompletedTask;
    }
}
