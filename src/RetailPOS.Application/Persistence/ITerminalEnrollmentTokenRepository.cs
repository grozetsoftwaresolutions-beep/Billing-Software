using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Application.Persistence;

public interface ITerminalEnrollmentTokenRepository
{
    Task AddAsync(TerminalEnrollmentToken token, CancellationToken ct = default);
    Task<TerminalEnrollmentToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<TerminalEnrollmentToken>> GetActiveTokensForTerminalAsync(Guid terminalId, CancellationToken ct = default);
}
