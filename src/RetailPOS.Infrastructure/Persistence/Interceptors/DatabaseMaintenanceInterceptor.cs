namespace RetailPOS.Infrastructure.Persistence.Interceptors;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

public sealed class DatabaseMaintenanceInterceptor : DbCommandInterceptor
{
    private readonly IDatabaseOperationGate _gate;
    private readonly IMaintenanceExecutionScope _scope;

    public DatabaseMaintenanceInterceptor(IDatabaseOperationGate gate, IMaintenanceExecutionScope scope)
    {
        _gate = gate;
        _scope = scope;
    }

    private void CheckAllowed()
    {
        if (_scope.IsInMaintenanceScope)
        {
            return;
        }

        var state = _gate.CurrentState;
        if (state != DatabaseMaintenanceState.Available)
        {
            throw new DomainException($"Database command execution blocked. Server maintenance state: {state}", "DATABASE_MAINTENANCE_BLOCKED");
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        CheckAllowed();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        CheckAllowed();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        CheckAllowed();
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        CheckAllowed();
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        CheckAllowed();
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        CheckAllowed();
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
