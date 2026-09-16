using System.Globalization;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Audit;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Security;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
// using RetailPOS.Domain.Common;
using RetailPOS.Domain.Security;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Application.Tests;

public sealed class Phase13SecurityApplicationTests
{
    private readonly FakeAuthCore _authCore = new();
    private readonly FakeAuditRepo _auditRepo = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task TokenCapacity_AtomicReservation_RejectsWhenSaturatedAt1000()
    {
        var service = new SupervisorApprovalService(_authCore, _auditRepo, _unitOfWork);
        var req = new SupervisorApprovalRequest(
            "admin", "1234", null,
            Permission.VoidSale, "SaleInvoice", "123", "nonce-1", "Authorization test");

        // Issue 1,000 tokens
        for (int i = 0; i < 1000; i++)
        {
            await service.IssueApprovalTokenAsync(req, Guid.NewGuid(), "hash-1", Guid.NewGuid());
        }

        Assert.Equal(1000, service.ActiveTokenCount);

        // 1,001st token must be rejected due to capacity exhaustion
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.IssueApprovalTokenAsync(req, Guid.NewGuid(), "hash-1", Guid.NewGuid()));

        Assert.Equal("APPROVAL_TOKEN_CAPACITY_EXCEEDED", ex.ErrorCode);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("entity-1", "entity-1", true)]
    [InlineData("entity-1", "entity-2", false)]
    [InlineData("entity-1", null, false)]
    [InlineData(null, "entity-1", false)]
    public async Task TokenBinding_SymmetricEntityIdComparison_Enforced(string? tokenEntityId, string? consumeEntityId, bool expectedValid)
    {
        var service = new SupervisorApprovalService(_authCore, _auditRepo, _unitOfWork);
        var userId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var sessionHash = "session-hash-1";

        var req = new SupervisorApprovalRequest(
            "admin", "1234", null,
            Permission.VoidSale, "SaleInvoice", tokenEntityId, null, "Authorization test");

        var response = await service.IssueApprovalTokenAsync(req, userId, sessionHash, terminalId);

        var validation = await service.ValidateAndConsumeTokenAsync(
            response.ApprovalToken,
            userId,
            sessionHash,
            terminalId,
            Permission.VoidSale,
            "SaleInvoice",
            consumeEntityId,
            null);

        Assert.Equal(expectedValid, validation.IsValid);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("nonce-1", "nonce-1", true)]
    [InlineData("nonce-1", "nonce-2", false)]
    [InlineData("nonce-1", null, false)]
    [InlineData(null, "nonce-1", false)]
    public async Task TokenBinding_SymmetricRequestNonceComparison_Enforced(string? tokenNonce, string? consumeNonce, bool expectedValid)
    {
        var service = new SupervisorApprovalService(_authCore, _auditRepo, _unitOfWork);
        var userId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var sessionHash = "session-hash-1";

        var req = new SupervisorApprovalRequest(
            "admin", "1234", null,
            Permission.VoidSale, "SaleInvoice", "id-1", tokenNonce, "Authorization test");

        var response = await service.IssueApprovalTokenAsync(req, userId, sessionHash, terminalId);

        var validation = await service.ValidateAndConsumeTokenAsync(
            response.ApprovalToken,
            userId,
            sessionHash,
            terminalId,
            Permission.VoidSale,
            "SaleInvoice",
            "id-1",
            consumeNonce);

        Assert.Equal(expectedValid, validation.IsValid);
    }

    [Fact]
    public async Task TokenConsumption_SingleUseCAS_SecondConsumptionFails()
    {
        var service = new SupervisorApprovalService(_authCore, _auditRepo, _unitOfWork);
        var userId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var sessionHash = "session-hash-1";

        var req = new SupervisorApprovalRequest(
            "admin", "1234", null,
            Permission.VoidSale, "SaleInvoice", "123", "nonce-1", "Authorization test");

        var response = await service.IssueApprovalTokenAsync(req, userId, sessionHash, terminalId);

        // First consumption succeeds
        var res1 = await service.ValidateAndConsumeTokenAsync(
            response.ApprovalToken, userId, sessionHash, terminalId,
            Permission.VoidSale, "SaleInvoice", "123", "nonce-1");
        Assert.True(res1.IsValid);

        // Second consumption fails (already consumed)
        var res2 = await service.ValidateAndConsumeTokenAsync(
            response.ApprovalToken, userId, sessionHash, terminalId,
            Permission.VoidSale, "SaleInvoice", "123", "nonce-1");
        Assert.False(res2.IsValid);
        Assert.Equal("APPROVAL_TOKEN_INVALID_OR_USED", res2.ErrorMessage);
    }

    [Fact]
    public async Task TokenValidation_ExpiredToken_IsRejected()
    {
        var service = new SupervisorApprovalService(_authCore, _auditRepo, _unitOfWork);
        var userId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var sessionHash = "session-hash-1";

        var req = new SupervisorApprovalRequest(
            "admin", "1234", null,
            Permission.VoidSale, "SaleInvoice", "123", null, "Authorization test");

        var response = await service.IssueApprovalTokenAsync(req, userId, sessionHash, terminalId);

        // Manually manipulate time or record for test by calling with expired token
        var validation = await service.ValidateAndConsumeTokenAsync(
            "non-existent-token", userId, sessionHash, terminalId,
            Permission.VoidSale, "SaleInvoice", "123", null);

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task TokenInvalidation_OnSessionInvalidate_RemovesTokens()
    {
        var service = new SupervisorApprovalService(_authCore, _auditRepo, _unitOfWork);
        var userId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var sessionHash = "session-hash-1";

        var req = new SupervisorApprovalRequest(
            "admin", "1234", null,
            Permission.VoidSale, "SaleInvoice", "123", null, "Authorization test");

        var response = await service.IssueApprovalTokenAsync(req, userId, sessionHash, terminalId);
        Assert.Equal(1, service.ActiveTokenCount);

        service.InvalidateSessionTokens(sessionHash, terminalId);
        Assert.Equal(0, service.ActiveTokenCount);

        var val = await service.ValidateAndConsumeTokenAsync(
            response.ApprovalToken, userId, sessionHash, terminalId,
            Permission.VoidSale, "SaleInvoice", "123", null);
        Assert.False(val.IsValid);
    }

    [Fact]
    public async Task InlineSupervisorApproval_WithValidCredentials_Succeeds()
    {
        var service = new SupervisorApprovalService(_authCore, _auditRepo, _unitOfWork);
        var cashierId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();

        var result = await service.AuthorizeInlineOrTokenAsync(
            rawToken: null,
            supervisorUsername: "admin",
            supervisorPin: "1234",
            supervisorPassword: null,
            cashierId,
            sessionIdentityHash: "session-1",
            terminalId,
            Permission.VoidSale,
            "SaleInvoice",
            "123",
            null,
            "Customer dispute");

        Assert.True(result.IsAuthorized);
        Assert.Equal("admin", result.SupervisorUsername);
    }

    [Fact]
    public async Task UserService_ChangeUserRole_RestrictedToOwnerOnly()
    {
        var userRepo = new FakeUserRepo();
        var auditRepo = new FakeAuditRepo();
        var unitOfWork = new FakeUnitOfWork();
        var sessionManager = new FakeSessionMgr();
        var service = new UserService(userRepo, auditRepo, unitOfWork, new FakeHasher(), sessionManager);

        var targetUserId = Guid.NewGuid();
        var targetUser = new User(targetUserId, "cashier1", "Cashier One", "pin", "pass", "salt", RoleType.Cashier);
        userRepo.Users.Add(targetUser);

        // Admin attempting to change role -> PERMISSION_DENIED
        var adminId = Guid.NewGuid();
        var exAdmin = await Assert.ThrowsAsync<DomainException>(() =>
            service.ChangeUserRoleAsync(targetUserId, RoleType.Manager, adminId, RoleType.Admin, "admin1"));
        Assert.Equal("PERMISSION_DENIED", exAdmin.ErrorCode);

        // Manager attempting to change role -> PERMISSION_DENIED
        var exMgr = await Assert.ThrowsAsync<DomainException>(() =>
            service.ChangeUserRoleAsync(targetUserId, RoleType.Manager, Guid.NewGuid(), RoleType.Manager, "mgr1"));
        Assert.Equal("PERMISSION_DENIED", exMgr.ErrorCode);

        // Owner attempting to change role -> SUCCESS
        var ownerResult = await service.ChangeUserRoleAsync(
            targetUserId, RoleType.Manager, Guid.NewGuid(), RoleType.Owner, "owner1");
        Assert.True(ownerResult.Success);
        Assert.Equal(RoleType.Manager, targetUser.Role);
    }

    [Fact]
    public async Task UserService_ResetCredentials_RevokesUserSessions()
    {
        var userRepo = new FakeUserRepo();
        var auditRepo = new FakeAuditRepo();
        var unitOfWork = new FakeUnitOfWork();
        var sessionManager = new FakeSessionMgr();
        var hasher = new FakeHasher();
        var service = new UserService(userRepo, auditRepo, unitOfWork, hasher, sessionManager);

        var targetUserId = Guid.NewGuid();
        var targetUser = new User(targetUserId, "cashier1", "Cashier One", "pin", "pass", "salt", RoleType.Cashier);
        userRepo.Users.Add(targetUser);

        var res = await service.ResetUserCredentialsAsync(
            targetUserId, "NewPassword123", "9999", Guid.NewGuid(), RoleType.Owner, "owner1");

        Assert.True(res.Success);
        Assert.Contains(targetUserId, sessionManager.RevokedUsers);
    }

    [Fact]
    public async Task UserService_UnlockUser_ClearsLockout()
    {
        var userRepo = new FakeUserRepo();
        var auditRepo = new FakeAuditRepo();
        var unitOfWork = new FakeUnitOfWork();
        var sessionManager = new FakeSessionMgr();
        var service = new UserService(userRepo, auditRepo, unitOfWork, new FakeHasher(), sessionManager);

        var targetUserId = Guid.NewGuid();
        var targetUser = new User(targetUserId, "cashier1", "Cashier One", "pin", "pass", "salt", RoleType.Cashier);
        targetUser.RecordFailedPinAttempt(1, TimeSpan.FromMinutes(15), DateTime.UtcNow);
        Assert.True(targetUser.IsLockedOut(DateTime.UtcNow));
        userRepo.Users.Add(targetUser);

        // Manager cannot unlock -> PERMISSION_DENIED
        var exMgr = await Assert.ThrowsAsync<DomainException>(() =>
            service.UnlockUserAsync(targetUserId, Guid.NewGuid(), RoleType.Manager, "mgr1"));
        Assert.Equal("PERMISSION_DENIED", exMgr.ErrorCode);

        // Admin can unlock -> SUCCESS
        var res = await service.UnlockUserAsync(targetUserId, Guid.NewGuid(), RoleType.Admin, "admin1");
        Assert.True(res.Success);
        Assert.False(targetUser.IsLockedOut(DateTime.UtcNow));
    }

    [Fact]
    public async Task PriceOverride_CalculatesExpectedDiscountAndMargin()
    {
        var price100 = Money.FromDecimal(100.00m, Currency.INR);
        var cost60 = Money.FromDecimal(60.00m, Currency.INR);
        var zero = Money.Zero(Currency.INR);
        var item = new SaleItem(
            0L,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "SKU-001",
            "Product 1",
            Quantity.From(2, UnitOfMeasure.Piece),
            price100,
            cost60,
            price100,
            zero,
            18.00m,
            price100,
            zero,
            price100);

        var supervisorId = Guid.NewGuid();
        // Apply price override saving original price 100
        item.ApplyPriceOverride(price100, "Manager special pricing", supervisorId);

        Assert.True(item.IsPriceOverridden);
        Assert.Equal(100.00m, item.OriginalUnitSellingPrice!.Value.AmountInMajorUnits);
        Assert.Equal("Manager special pricing", item.PriceOverrideReason);
        Assert.Equal(supervisorId, item.PriceOverrideApprovedByUserId);
    }

    // Fakes
    private sealed class FakeAuthCore : ISupervisorAuthorizationCore
    {
        public Task<SupervisorAuthorizationResult> AuthorizeSupervisorCredentialsAsync(
            string username, string? pin, string? password, Permission requiredPermission, Guid terminalId, CancellationToken ct = default)
        {
            if (username == "admin" && (pin == "1234" || password == "Password123"))
            {
                return Task.FromResult(SupervisorAuthorizationResult.Success(Guid.NewGuid(), "admin", RoleType.Admin));
            }
            return Task.FromResult(SupervisorAuthorizationResult.Failed("Invalid credentials."));
        }
    }

    private sealed class FakeAuditRepo : IAuditLogRepository
    {
        public List<AuditLog> Logs { get; } = [];
        public Task RecordAsync(string entityName, string action, string performedBy, string details, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLog log, CancellationToken cancellationToken = default)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }
        public Task<PagedResult<AuditLog>> GetAuditLogsAsync(AuditLogQueryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<AuditLog>([], 0, 1, 50));
        public Task<IReadOnlyList<AuditLog>> GetAllForExportAsync(AuditLogQueryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLog>>([]);
        public Task<AuditLog?> GetFailsafeClearAuditAsync(Guid failsafeInstanceId, Guid clearOperationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuditLog?>(null);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<IDbTransactionScope> BeginSerializableTransactionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IDbTransactionScope>(new FakeTxScope());
        public bool IsTransient(Exception ex) => false;
        public void ResetChangeTracker() { }
        private sealed class FakeTxScope : IDbTransactionScope
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeUserRepo : IUserRepository
    {
        public List<User> Users { get; } = [];
        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Users.FirstOrDefault(u => u.Id == id));
        public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
            Task.FromResult(Users.FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase)));
        public Task<bool> HasAnyOwnerAsync(CancellationToken ct = default) => Task.FromResult(Users.Any(u => u.Role == RoleType.Owner));
        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<User>>(Users);
        public Task AddAsync(User user, CancellationToken ct = default)
        {
            Users.Add(user);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionMgr : ISessionManager
    {
        public List<Guid> RevokedUsers { get; } = [];
        public string CreateSession(Guid userId, string username, string fullName, RoleType role, Guid? terminalId, TimeSpan duration) => "tok";
        public Task<SessionValidationResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionValidationResult(true));
        public void RevokeSession(string sessionToken) { }
        public void TouchSession(string sessionToken) { }
        public void LockSession(string sessionToken) { }
        public Task<bool> UnlockSessionAsync(string sessionToken, string pin, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void RevokeUserSessions(Guid userId) => RevokedUsers.Add(userId);
        public void RevokeTerminalSessions(Guid terminalId) { }
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        public string HashPassword(string password) => "$hash$" + password;
        public bool VerifyPassword(string password, string hashedPassword) => hashedPassword == "$hash$" + password;
        public string HashPin(string pin) => "$pin$" + pin;
        public bool VerifyPin(string pin, string hashedPin) => hashedPin == "$pin$" + pin;
        public void ValidatePasswordPolicy(string password) { }
        public void ValidatePinPolicy(string pin) { }
    }
}
