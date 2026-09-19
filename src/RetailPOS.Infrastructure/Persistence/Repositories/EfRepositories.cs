using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfDbTransactionScope : IDbTransactionScope
{
    private readonly IDbContextTransaction _transaction;

    public EfDbTransactionScope(IDbContextTransaction transaction)
    {
        _transaction = transaction;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _transaction.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        _transaction.DisposeAsync();
}

public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly RetailDbContext _context;

    public EfUnitOfWork(RetailDbContext context)
    {
        _context = context;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);

    public async Task<IDbTransactionScope> BeginSerializableTransactionAsync(CancellationToken cancellationToken = default)
    {
        var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        return new EfDbTransactionScope(tx);
    }

    public bool IsTransient(Exception ex)
    {
        if (ex is SqliteException sqlEx && sqlEx.SqliteErrorCode == 5)
            return true;

        if (ex is DbUpdateException dbEx && dbEx.InnerException is SqliteException innerSql && innerSql.SqliteErrorCode == 5)
            return true;

        if (ex is DbUpdateConcurrencyException)
            return true;

        return false;
    }

    public void ResetChangeTracker()
    {
        _context.ChangeTracker.Clear();
    }
}

public sealed class EfUserRepository : IUserRepository
{
    private readonly RetailDbContext _context;

    public EfUserRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim().ToLowerInvariant();
        return await _context.Users.FirstOrDefaultAsync(u => u.Username == normalized, cancellationToken);
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _context.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(cancellationToken);

    public Task<bool> HasAnyOwnerAsync(CancellationToken cancellationToken = default) =>
        _context.Users.AnyAsync(u => u.Role == RoleType.Owner && u.IsActive, cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default) =>
        await _context.Users.AddAsync(user, cancellationToken);
}

public sealed class EfTerminalRepository : ITerminalRepository
{
    private readonly RetailDbContext _context;

    public EfTerminalRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default)
    {
        var normalized = terminalCode.Trim().ToUpperInvariant();
        return await _context.Terminals.FirstOrDefaultAsync(t => t.TerminalCode == normalized, cancellationToken);
    }

    public Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Terminals.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _context.Terminals.AsNoTracking().OrderBy(t => t.TerminalCode).ToListAsync(cancellationToken);

    public async Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default) =>
        await _context.Terminals.AddAsync(terminal, cancellationToken);
}

public sealed class EfAuditLogRepository : IAuditLogRepository
{
    private readonly RetailDbContext _context;

    public EfAuditLogRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task RecordAsync(string entityName, string action, string performedBy, string details, CancellationToken cancellationToken = default)
    {
        var log = new AuditLog(
            id: 0,
            action: action,
            entityName: entityName,
            entityId: performedBy,
            userId: null,
            terminalId: null,
            details: details,
            success: true);

        await _context.AuditLogs.AddAsync(log, cancellationToken);
    }

    public async Task RecordAsync(AuditLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        await _context.AuditLogs.AddAsync(log, cancellationToken);
    }

    public async Task<RetailPOS.Contracts.Common.PagedResult<AuditLog>> GetAuditLogsAsync(RetailPOS.Contracts.Audit.AuditLogQueryFilter filter, CancellationToken cancellationToken = default)
    {
        var query = ApplyAuditFilter(_context.AuditLogs.AsNoTracking(), filter);
        var totalCount = await query.CountAsync(cancellationToken);
        var pageNumber = Math.Max(1, filter.PageNumber);
        var pageSize = Math.Clamp(filter.PageSize, 1, 500);

        var items = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new RetailPOS.Contracts.Common.PagedResult<AuditLog>(items, totalCount, pageNumber, pageSize);
    }

    public async Task<IReadOnlyList<AuditLog>> GetAllForExportAsync(RetailPOS.Contracts.Audit.AuditLogQueryFilter filter, CancellationToken cancellationToken = default)
    {
        var query = ApplyAuditFilter(_context.AuditLogs.AsNoTracking(), filter);
        return await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<AuditLog?> GetFailsafeClearAuditAsync(Guid failsafeInstanceId, Guid clearOperationId, CancellationToken cancellationToken = default)
    {
        return await _context.AuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED" &&
                                      a.FailsafeInstanceId == failsafeInstanceId &&
                                      a.ClearOperationId == clearOperationId,
                                 cancellationToken);
    }

    private static IQueryable<AuditLog> ApplyAuditFilter(IQueryable<AuditLog> query, RetailPOS.Contracts.Audit.AuditLogQueryFilter filter)
    {
        if (filter.FromDateUtc.HasValue)
            query = query.Where(a => a.CreatedAtUtc >= filter.FromDateUtc.Value);
        if (filter.ToDateUtc.HasValue)
            query = query.Where(a => a.CreatedAtUtc <= filter.ToDateUtc.Value);
        if (filter.UserId.HasValue)
            query = query.Where(a => a.UserId == filter.UserId.Value);
        if (!string.IsNullOrWhiteSpace(filter.UserName))
        {
            var u = filter.UserName.Trim();
            query = query.Where(a => a.UserName != null && EF.Functions.Like(a.UserName, u));
        }
        if (filter.TerminalId.HasValue)
            query = query.Where(a => a.TerminalId == filter.TerminalId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            var act = filter.Action.Trim();
            query = query.Where(a => EF.Functions.Like(a.Action, act));
        }
        if (!string.IsNullOrWhiteSpace(filter.EntityName))
        {
            var ent = filter.EntityName.Trim();
            query = query.Where(a => EF.Functions.Like(a.EntityName, ent));
        }
        if (filter.Success.HasValue)
            query = query.Where(a => a.Success == filter.Success.Value);

        return query;
    }
}

public sealed class EfStoreRepository : IStoreRepository
{
    private readonly RetailDbContext _context;

    public EfStoreRepository(RetailDbContext context)
    {
        _context = context;
    }

    public Task<Store?> GetStoreAsync(CancellationToken cancellationToken = default) =>
        _context.Stores.FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

    public async Task UpdateStoreNameAsync(string storeName, CancellationToken cancellationToken = default)
    {
        var store = await GetStoreAsync(cancellationToken);
        if (store != null)
        {
            store.UpdateStoreName(storeName);
        }
        else
        {
            var newStore = new Store(1, storeName);
            await _context.Stores.AddAsync(newStore, cancellationToken);
        }
    }

    public async Task UpdateStoreSettingsAsync(
        string? storeName,
        string? address,
        string? phone,
        string? taxRegistrationNumber,
        string? receiptFooterMessage,
        bool? cashRoundingEnabled,
        string? timeZoneId,
        CancellationToken cancellationToken = default)
    {
        var store = await GetStoreAsync(cancellationToken);
        if (store == null)
        {
            store = new Store(1, storeName ?? "Retail POS Store");
            await _context.Stores.AddAsync(store, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(storeName))
        {
            store.UpdateStoreName(storeName);
        }

        store.UpdateDetails(address, phone, taxRegistrationNumber, receiptFooterMessage);

        if (cashRoundingEnabled.HasValue || !string.IsNullOrWhiteSpace(timeZoneId))
        {
            store.UpdateBillingSettings(cashRoundingEnabled ?? store.CashRoundingEnabled, timeZoneId ?? store.TimeZoneId);
        }
    }

    public async Task InitializeStoreSecurityAsync(
        Guid storeGuid,
        string storeName,
        string validatorHash,
        string wrappedKey,
        CancellationToken cancellationToken = default)
    {
        var store = await GetStoreAsync(cancellationToken);
        if (store == null)
        {
            store = new Store(1, storeName);
            store.InitializeSecurityRoot(storeGuid, validatorHash, wrappedKey);
            await _context.Stores.AddAsync(store, cancellationToken);
        }
        else
        {
            store.UpdateStoreName(storeName);
            store.InitializeSecurityRoot(storeGuid, validatorHash, wrappedKey);
        }
    }
}

