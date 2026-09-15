using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfTerminalEnrollmentTokenRepository : ITerminalEnrollmentTokenRepository
{
    private readonly RetailDbContext _context;

    public EfTerminalEnrollmentTokenRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(TerminalEnrollmentToken token, CancellationToken ct = default)
    {
        await _context.Set<TerminalEnrollmentToken>().AddAsync(token, ct);
    }

    public async Task<TerminalEnrollmentToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        return await _context.Set<TerminalEnrollmentToken>()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
    }

    public async Task<IReadOnlyList<TerminalEnrollmentToken>> GetActiveTokensForTerminalAsync(Guid terminalId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _context.Set<TerminalEnrollmentToken>()
            .Where(t => t.BoundTerminalId == terminalId && !t.IsConsumed && !t.IsRevoked && t.ExpiresAtUtc > now)
            .ToListAsync(ct);
    }
}
