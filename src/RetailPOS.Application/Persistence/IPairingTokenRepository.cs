using System;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Application.Persistence;

public interface IPairingTokenRepository
{
    Task AddAsync(PairingEnrollmentToken token, CancellationToken ct = default);
    Task<PairingEnrollmentToken?> GetByIdAsync(Guid pairingOperationId, CancellationToken ct = default);
    Task<PairingEnrollmentToken?> GetActiveTokenForTerminalAsync(Guid terminalId, CancellationToken ct = default);
    Task<System.Collections.Generic.IReadOnlyList<PairingEnrollmentToken>> GetPendingAsync(CancellationToken ct = default);
    Task InvalidatePendingTokensOnServerRestartAsync(CancellationToken ct = default);
}
