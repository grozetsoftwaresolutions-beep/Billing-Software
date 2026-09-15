namespace RetailPOS.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

public sealed class DatabaseOperationGate : IDatabaseOperationGate, IDisposable
{
    private readonly object _lock = new();
    private DatabaseMaintenanceState _state = DatabaseMaintenanceState.Available;
    private int _activeExecutions;
    private int _activeCommits;
    private CancellationTokenSource _drainingCts = new();
    private TaskCompletionSource<bool> _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public DatabaseMaintenanceState CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public int ActiveExecutionLeaseCount
    {
        get { lock (_lock) return _activeExecutions; }
    }

    public int ActiveCommitLeaseCount
    {
        get { lock (_lock) return _activeCommits; }
    }

    public Task<IOperationLease> AcquireExecutionLeaseAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_state != DatabaseMaintenanceState.Available)
            {
                throw new DomainException($"Database operations are currently unavailable. Server maintenance state: {_state}", "DATABASE_MAINTENANCE_BLOCKED");
            }

            _activeExecutions++;
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _drainingCts.Token);
            return Task.FromResult<IOperationLease>(new OperationLease(this, linkedCts, isCommit: false));
        }
    }

    public Task<IOperationLease> AcquireCommitLeaseAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_state != DatabaseMaintenanceState.Available && _state != DatabaseMaintenanceState.Draining)
            {
                throw new DomainException($"Database commit operations are blocked. Server maintenance state: {_state}", "DATABASE_MAINTENANCE_BLOCKED");
            }

            _activeCommits++;
            return Task.FromResult<IOperationLease>(new OperationLease(this, null, isCommit: true));
        }
    }

    public async Task TransitionToDrainingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task drainWaitTask;
        lock (_lock)
        {
            if (_state == DatabaseMaintenanceState.EmergencyLockdown)
            {
                return;
            }

            _state = DatabaseMaintenanceState.Draining;
            _drainingCts.Cancel();

            if (_activeExecutions == 0 && _activeCommits == 0)
            {
                _state = DatabaseMaintenanceState.InactiveOffline;
                return;
            }

            _drainTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            drainWaitTask = _drainTcs.Task;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await drainWaitTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation expired; proceed to InactiveOffline
        }

        lock (_lock)
        {
            _state = DatabaseMaintenanceState.InactiveOffline;
        }
    }

    public void TransitionToAvailable()
    {
        lock (_lock)
        {
            _drainingCts.Dispose();
            _drainingCts = new CancellationTokenSource();
            _state = DatabaseMaintenanceState.Available;
        }
    }

    public void TransitionToInactiveOffline()
    {
        lock (_lock)
        {
            _state = DatabaseMaintenanceState.InactiveOffline;
        }
    }

    public void TransitionToEmergencyLockdown(string reason)
    {
        lock (_lock)
        {
            _state = DatabaseMaintenanceState.EmergencyLockdown;
            _drainingCts.Cancel();
        }
    }

    private void ReleaseLease(bool isCommit)
    {
        lock (_lock)
        {
            if (isCommit)
            {
                _activeCommits = Math.Max(0, _activeCommits - 1);
            }
            else
            {
                _activeExecutions = Math.Max(0, _activeExecutions - 1);
            }

            if (_activeExecutions == 0 && _activeCommits == 0)
            {
                _drainTcs.TrySetResult(true);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _drainingCts.Dispose();
        }
    }

    private sealed class OperationLease : IOperationLease
    {
        private readonly DatabaseOperationGate _gate;
        private readonly CancellationTokenSource? _linkedCts;
        private readonly bool _isCommit;
        private bool _disposed;

        public OperationLease(DatabaseOperationGate gate, CancellationTokenSource? linkedCts, bool isCommit)
        {
            _gate = gate;
            _linkedCts = linkedCts;
            _isCommit = isCommit;
        }

        public CancellationToken CancellationToken => _linkedCts?.Token ?? CancellationToken.None;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _gate.ReleaseLease(_isCommit);
                _linkedCts?.Dispose();
            }
        }
    }
}
