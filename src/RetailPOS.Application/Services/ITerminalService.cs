using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;

namespace RetailPOS.Application.Services;

public interface ITerminalService
{
    Task<IReadOnlyList<TerminalDto>> GetTerminalsAsync(CancellationToken cancellationToken = default);

    Task<ApiResponse<TerminalDto>> RegisterTerminalAsync(
        RegisterTerminalRequest request,
        string executingUsername,
        CancellationToken cancellationToken = default);

    Task<ApiResponse<TerminalDto>> SetTerminalStatusAsync(
        Guid terminalId,
        bool isActive,
        string executingUsername,
        CancellationToken cancellationToken = default);
}
