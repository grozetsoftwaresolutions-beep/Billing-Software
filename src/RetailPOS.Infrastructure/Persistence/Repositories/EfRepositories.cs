using System.Data;
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
}
