namespace RetailPOS.Application.Services;

using System;
using System.Threading;

public sealed class MaintenanceScopeFactory : IMaintenanceScopeFactory, IMaintenanceExecutionScope
{
    private static readonly AsyncLocal<bool> _isInScope = new();

    public bool IsInMaintenanceScope => _isInScope.Value;

    public IDisposable CreateMaintenanceScope()
    {
        bool prev = _isInScope.Value;
        _isInScope.Value = true;
        return new ScopeReverter(prev);
    }

    private sealed class ScopeReverter : IDisposable
    {
        private readonly bool _prev;
        private bool _disposed;

        public ScopeReverter(bool prev)
        {
            _prev = prev;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _isInScope.Value = _prev;
            }
        }
    }
}
