namespace RetailPOS.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Enums;

public sealed class DatabaseMaintenanceCoordinator : IDatabaseMaintenanceCoordinator
{
    private readonly IDatabaseOperationGate _gate;

    public DatabaseMaintenanceCoordinator(IDatabaseOperationGate gate)
    {
        _gate = gate;
    }

    public DatabaseMaintenanceState CurrentState => _gate.CurrentState;

    public async Task<IDisposable> EnterMaintenanceAsync(string reason, CancellationToken ct = default)
    {
        await _gate.TransitionToDrainingAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return new MaintenanceSession(_gate);
    }

    public void SetEmergencyLockdown(string reason)
    {
        _gate.TransitionToEmergencyLockdown(reason);
    }

    private sealed class MaintenanceSession : IDisposable
    {
        private readonly IDatabaseOperationGate _gate;
        private bool _disposed;

        public MaintenanceSession(IDatabaseOperationGate gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _gate.TransitionToAvailable();
            }
        }
    }
}
