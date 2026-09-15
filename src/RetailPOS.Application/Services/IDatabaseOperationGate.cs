namespace RetailPOS.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Enums;

public interface IOperationLease : IDisposable
{
    CancellationToken CancellationToken { get; }
}

public interface IDatabaseOperationGate
{
    DatabaseMaintenanceState CurrentState { get; }
    int ActiveExecutionLeaseCount { get; }
    int ActiveCommitLeaseCount { get; }

    Task<IOperationLease> AcquireExecutionLeaseAsync(CancellationToken ct = default);
    Task<IOperationLease> AcquireCommitLeaseAsync(CancellationToken ct = default);
    Task TransitionToDrainingAsync(TimeSpan timeout, CancellationToken ct = default);
    void TransitionToAvailable();
    void TransitionToInactiveOffline();
    void TransitionToEmergencyLockdown(string reason);
}
