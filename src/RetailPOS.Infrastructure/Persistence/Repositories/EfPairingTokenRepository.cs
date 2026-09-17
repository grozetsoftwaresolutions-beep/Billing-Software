using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfPairingTokenRepository : IPairingTokenRepository
{
    private readonly RetailDbContext _context;

    public EfPairingTokenRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(PairingEnrollmentToken token, CancellationToken ct = default)
    {
        await _context.PairingTokens.AddAsync(token, ct);
    }

    public async Task<PairingEnrollmentToken?> GetByIdAsync(Guid pairingOperationId, CancellationToken ct = default)
    {
        var token = await _context.PairingTokens.FirstOrDefaultAsync(t => t.Id == pairingOperationId, ct);
        if (token != null)
        {
            if (token.CheckAndApplyExpiration(DateTime.UtcNow))
            {
                await _context.SaveChangesAsync(ct);
            }
        }
        return token;
    }

    public async Task<PairingEnrollmentToken?> GetActiveTokenForTerminalAsync(Guid terminalId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tokens = await _context.PairingTokens
            .Where(t => t.TerminalId == terminalId && t.State != "COMPLETED" && t.State != "REVOKED" && t.ExpiresAtUtc > now)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);

        return tokens.FirstOrDefault();
    }

    public async Task InvalidatePendingTokensOnServerRestartAsync(CancellationToken ct = default)
    {
        var pendingTokens = await _context.PairingTokens
            .Where(t => t.State == "PENDING" || t.State == "PAKE_VERIFIED")
            .ToListAsync(ct);

        foreach (var token in pendingTokens)
        {
            token.Revoke("SERVER_RESTART_INVALIDATION");
        }

        if (pendingTokens.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }
    }
}
