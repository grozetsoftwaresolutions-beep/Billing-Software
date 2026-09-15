namespace RetailPOS.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Common;

public sealed class BackupOperationCoordinator : IBackupOperationCoordinator, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string? _currentOperationName;
    private bool _disposed;

    public bool IsOperationInProgress => _currentOperationName != null;
    public string? CurrentOperationName => _currentOperationName;

    public async Task<IDisposable> AcquireExclusiveLockAsync(string operationName, CancellationToken ct = default)
    {
        bool acquired = await _semaphore.WaitAsync(0, ct).ConfigureAwait(false);
        if (!acquired)
        {
            throw new DomainException($"Another backup or restore operation is already in progress: {_currentOperationName ?? "Active Operation"}", "OPERATION_ALREADY_IN_PROGRESS");
        }

        _currentOperationName = operationName;
        return new LockReleaser(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _semaphore.Dispose();
        }
    }

    private sealed class LockReleaser : IDisposable
    {
        private readonly BackupOperationCoordinator _coordinator;
        private bool _disposed;

        public LockReleaser(BackupOperationCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _coordinator._currentOperationName = null;
                _coordinator._semaphore.Release();
            }
        }
    }
}
