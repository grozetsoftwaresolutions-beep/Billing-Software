namespace RetailPOS.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IBackupOperationCoordinator
{
    bool IsOperationInProgress { get; }
    string? CurrentOperationName { get; }
    Task<IDisposable> AcquireExclusiveLockAsync(string operationName, CancellationToken ct = default);
}
