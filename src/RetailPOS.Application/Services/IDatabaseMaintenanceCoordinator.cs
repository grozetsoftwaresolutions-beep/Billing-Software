namespace RetailPOS.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Enums;

public interface IDatabaseMaintenanceCoordinator
{
    DatabaseMaintenanceState CurrentState { get; }
    Task<IDisposable> EnterMaintenanceAsync(string reason, CancellationToken ct = default);
    void SetEmergencyLockdown(string reason);
}
