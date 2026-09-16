using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;

namespace RetailPOS.Application.Persistence;

public interface IDbTransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<IDbTransactionScope> BeginSerializableTransactionAsync(CancellationToken cancellationToken = default);
    bool IsTransient(Exception ex);
    void ResetChangeTracker();
}

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<bool> HasAnyOwnerAsync(CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
}

public interface ITerminalRepository
{
    Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default);
    Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default);
}

public interface IAuditLogRepository
{
    Task RecordAsync(string entityName, string action, string performedBy, string details, CancellationToken cancellationToken = default);
    Task RecordAsync(RetailPOS.Domain.Entities.Audit.AuditLog log, CancellationToken cancellationToken = default);
    Task<RetailPOS.Contracts.Common.PagedResult<RetailPOS.Domain.Entities.Audit.AuditLog>> GetAuditLogsAsync(RetailPOS.Contracts.Audit.AuditLogQueryFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RetailPOS.Domain.Entities.Audit.AuditLog>> GetAllForExportAsync(RetailPOS.Contracts.Audit.AuditLogQueryFilter filter, CancellationToken cancellationToken = default);
    Task<RetailPOS.Domain.Entities.Audit.AuditLog?> GetFailsafeClearAuditAsync(Guid failsafeInstanceId, Guid clearOperationId, CancellationToken cancellationToken = default);
}

public interface IStoreRepository
{
    Task<Store?> GetStoreAsync(CancellationToken cancellationToken = default);
    Task UpdateStoreNameAsync(string storeName, CancellationToken cancellationToken = default);
    Task UpdateStoreSettingsAsync(string? storeName, string? address, string? phone, string? taxRegistrationNumber, string? receiptFooterMessage, bool? cashRoundingEnabled, string? timeZoneId, CancellationToken cancellationToken = default);
}
