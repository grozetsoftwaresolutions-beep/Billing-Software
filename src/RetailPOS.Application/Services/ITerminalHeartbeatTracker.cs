using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public interface ITerminalHeartbeatTracker
{
    Task<TerminalHeartbeatResponseDto> RecordHeartbeatAsync(TerminalHeartbeatDto heartbeat, CancellationToken ct = default);
    CounterConnectivityStatus GetConnectivityStatus(Guid terminalId, DateTime? lastHeartbeatUtc = null);
}
