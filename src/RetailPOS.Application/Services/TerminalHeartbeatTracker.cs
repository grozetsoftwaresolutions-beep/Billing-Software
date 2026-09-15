using System.Collections.Concurrent;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public sealed class TerminalHeartbeatTracker : ITerminalHeartbeatTracker
{
    private readonly ConcurrentDictionary<Guid, DateTime> _lastHeartbeats = new();
    private readonly ITerminalRepository _terminalRepository;
    private readonly IUnitOfWork _unitOfWork;

    public TerminalHeartbeatTracker(ITerminalRepository terminalRepository, IUnitOfWork unitOfWork)
    {
        _terminalRepository = terminalRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<TerminalHeartbeatResponseDto> RecordHeartbeatAsync(TerminalHeartbeatDto heartbeat, CancellationToken ct = default)
    {
        var terminal = await _terminalRepository.GetByIdAsync(heartbeat.TerminalId, ct);
        if (terminal is null)
            throw new DomainException("Terminal not found.", "TERMINAL_NOT_FOUND");

        if (terminal.Status == TerminalStatus.Decommissioned)
            throw new DomainException("Terminal is decommissioned.", "TERMINAL_DECOMMISSIONED");

        if (terminal.Status != TerminalStatus.Active)
            throw new DomainException("Terminal is inactive.", "TERMINAL_NOT_ACTIVE");

        var now = DateTime.UtcNow;
        _lastHeartbeats[terminal.Id] = now;

        terminal.RecordHeartbeat(heartbeat.ClientIpAddress, now);
        await _unitOfWork.SaveChangesAsync(ct);

        return new TerminalHeartbeatResponseDto(
            terminal.Id,
            terminal.Status,
            CounterConnectivityStatus.Online,
            now);
    }

    public CounterConnectivityStatus GetConnectivityStatus(Guid terminalId, DateTime? lastHeartbeatUtc = null, DateTime? referenceTimeUtc = null)
    {
        DateTime? timestamp = _lastHeartbeats.TryGetValue(terminalId, out var cached)
            ? cached
            : lastHeartbeatUtc;

        if (timestamp is null)
            return CounterConnectivityStatus.Offline;

        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var elapsed = now - timestamp.Value;
        if (elapsed.TotalSeconds <= 30)
            return CounterConnectivityStatus.Online;
        if (elapsed.TotalSeconds <= 90)
            return CounterConnectivityStatus.Disconnected;

        return CounterConnectivityStatus.Offline;
    }
}
