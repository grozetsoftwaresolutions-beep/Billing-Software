using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Tests.Fakes;

public sealed class FakeTransactionScope : IDbTransactionScope
{
    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
    public Task<IDbTransactionScope> BeginSerializableTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IDbTransactionScope>(new FakeTransactionScope());
}

public sealed class FakeUserRepository : IUserRepository
{
    public List<User> Users { get; } = [];

    public Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim().ToLowerInvariant();
        return Task.FromResult(Users.FirstOrDefault(u => u.Username == normalized));
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == id));

    public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<User>>(Users);

    public Task<bool> HasAnyOwnerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.Any(u => u.Role == RoleType.Owner && u.IsActive));

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        Users.Add(user);
        return Task.CompletedTask;
    }
}

public sealed class FakeTerminalRepository : ITerminalRepository
{
    public List<Terminal> Terminals { get; } = [];

    public Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default)
    {
        var normalized = terminalCode.Trim().ToUpperInvariant();
        return Task.FromResult(Terminals.FirstOrDefault(t => t.TerminalCode == normalized));
    }

    public Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Terminals.FirstOrDefault(t => t.Id == id));

    public Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Terminal>>(Terminals);

    public Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        Terminals.Add(terminal);
        return Task.CompletedTask;
    }
}

public sealed class FakeAuditLogRepository : IAuditLogRepository
{
    public List<(string Entity, string Action, string User, string Details)> Logs { get; } = [];

    public Task RecordAsync(string entityName, string action, string performedBy, string details, CancellationToken cancellationToken = default)
    {
        Logs.Add((entityName, action, performedBy, details));
        return Task.CompletedTask;
    }
}

public sealed class FakeStoreRepository : IStoreRepository
{
    public Store? CurrentStore { get; set; }

    public Task<Store?> GetStoreAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CurrentStore);

    public Task UpdateStoreNameAsync(string storeName, CancellationToken cancellationToken = default)
    {
        if (CurrentStore == null)
            CurrentStore = new Store(1, storeName);
        else
            CurrentStore.UpdateStoreName(storeName);

        return Task.CompletedTask;
    }
}

public sealed class FakeSessionManager : ISessionManager
{
    public Dictionary<string, (Guid UserId, string Username, RoleType Role, Guid? TerminalId)> Sessions { get; } = [];

    public string CreateSession(Guid userId, string username, string fullName, RoleType role, Guid? terminalId, TimeSpan duration)
    {
        var token = "tok_" + Guid.NewGuid().ToString("N");
        Sessions[token] = (userId, username, role, terminalId);
        return token;
    }

    public Task<SessionValidationResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        if (Sessions.TryGetValue(sessionToken, out var s))
            return Task.FromResult(new SessionValidationResult(true, s.UserId, s.Username, s.Username, s.Role, s.TerminalId));

        return Task.FromResult(new SessionValidationResult(false, ErrorMessage: "Invalid token."));
    }

    public void RevokeSession(string sessionToken)
    {
        Sessions.Remove(sessionToken);
    }
}
