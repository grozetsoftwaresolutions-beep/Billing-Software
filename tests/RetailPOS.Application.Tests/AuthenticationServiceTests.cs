using RetailPOS.Application.Services;
using RetailPOS.Application.Tests.Fakes;
using RetailPOS.Contracts.Auth;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Application.Tests;

public sealed class AuthenticationServiceTests
{
    private readonly FakeUserRepository _userRepo = new();
    private readonly FakeTerminalRepository _terminalRepo = new();
    private readonly FakeStoreRepository _storeRepo = new();
    private readonly FakeAuditLogRepository _auditRepo = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly FakeSessionManager _sessionManager = new();
    private readonly AuthenticationService _service;

    public AuthenticationServiceTests()
    {
        _service = new AuthenticationService(
            _userRepo,
            _terminalRepo,
            _auditRepo,
            _storeRepo,
            _unitOfWork,
            _hasher,
            _sessionManager);
    }

    [Fact]
    public async Task SetupStatus_WhenNoOwnerExists_ReturnsTrue()
    {
        var status = await _service.GetSetupStatusAsync();
        Assert.True(status.IsSetupRequired);
    }

    [Fact]
    public async Task SetupOwner_Success_CreatesOwnerAndStore()
    {
        var request = new SetupOwnerRequest("Fresh Mart", "StoreOwner", "Main Owner", "StoreOwnerPass#2026", "1234");

        var response = await _service.SetupOwnerAsync(request);

        Assert.True(response.Success);
        Assert.Single(_userRepo.Users);

        var owner = _userRepo.Users[0];
        Assert.Equal("storeowner", owner.Username); // Case-insensitive normalized to lowercase
        Assert.Equal(RoleType.Owner, owner.Role);
        Assert.True(owner.IsActive);
        Assert.True(_hasher.VerifyPassword("StoreOwnerPass#2026", owner.PasswordHash));
        Assert.True(_hasher.VerifyPin("1234", owner.PinHash));

        Assert.NotNull(_storeRepo.CurrentStore);
        Assert.Equal("Fresh Mart", _storeRepo.CurrentStore.StoreName);
        Assert.Contains(_auditRepo.Logs, l => l.Action == "OWNER_SETUP");
    }

    [Fact]
    public async Task SetupOwner_WhenOwnerAlreadyExists_ThrowsDomainException()
    {
        var request1 = new SetupOwnerRequest("Mart 1", "owner1", "Owner One", "Password123#", "1234");
        await _service.SetupOwnerAsync(request1);

        var request2 = new SetupOwnerRequest("Mart 2", "owner2", "Owner Two", "Password456#", "5678");
        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.SetupOwnerAsync(request2));

        Assert.Equal("SETUP_ALREADY_COMPLETED", ex.ErrorCode);
    }

    [Fact]
    public async Task LoginWithPassword_OwnerAdminManager_Succeeds()
    {
        var pass = "ManagerPass#123";
        var hash = _hasher.HashPassword(pass);
        var manager = new User(Guid.NewGuid(), "manager1", "Store Manager", "pinhash", hash, "salt", RoleType.Manager);
        _userRepo.Users.Add(manager);

        var request = new LoginPasswordRequest("MANAGER1", pass); // Case-insensitive username check
        var result = await _service.LoginWithPasswordAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("manager1", result.Data.Username);
        Assert.Equal(RoleType.Manager, result.Data.Role);
        Assert.NotEmpty(result.Data.Token);
    }

    [Fact]
    public async Task LoginWithPassword_CashierAttemptingPasswordLogin_FailsWithGenericAuthFailed()
    {
        var pass = "CashierPass#123";
        var hash = _hasher.HashPassword(pass);
        var cashier = new User(Guid.NewGuid(), "cashier1", "Cashier 1", "pinhash", hash, "salt", RoleType.Cashier);
        _userRepo.Users.Add(cashier);

        var request = new LoginPasswordRequest("cashier1", pass);
        var result = await _service.LoginWithPasswordAsync(request);

        Assert.False(result.Success);
        Assert.Equal("AUTH_FAILED", result.ErrorCode);
        Assert.Equal("Invalid credentials.", result.ErrorMessage);
    }

    [Fact]
    public async Task LoginWithPassword_WrongPassword_ReturnsGenericAuthFailed()
    {
        var hash = _hasher.HashPassword("CorrectPassword123");
        var admin = new User(Guid.NewGuid(), "admin1", "Admin", "pinhash", hash, "salt", RoleType.Admin);
        _userRepo.Users.Add(admin);

        var request = new LoginPasswordRequest("admin1", "WrongPassword999");
        var result = await _service.LoginWithPasswordAsync(request);

        Assert.False(result.Success);
        Assert.Equal("AUTH_FAILED", result.ErrorCode);
        Assert.Equal("Invalid credentials.", result.ErrorMessage);
    }

    [Fact]
    public async Task LoginWithPin_Cashier_WithValidTerminal_Succeeds()
    {
        var terminalId = Guid.NewGuid();
        var terminal = new Terminal(terminalId, "POS-01", "Counter 1");
        _terminalRepo.Terminals.Add(terminal);

        var pinHash = _hasher.HashPin("4321");
        var cashier = new User(Guid.NewGuid(), "poscashier", "Cashier POS", pinHash, "passhash", "salt", RoleType.Cashier);
        _userRepo.Users.Add(cashier);

        var request = new LoginPinRequest("poscashier", "4321", terminalId);
        var result = await _service.LoginWithPinAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(terminalId, result.Data.TerminalId);
        Assert.Equal(RoleType.Cashier, result.Data.Role);
    }

    [Fact]
    public async Task LoginWithPin_NonCashierAttemptingPinLogin_FailsWithGenericAuthFailed()
    {
        var terminalId = Guid.NewGuid();
        _terminalRepo.Terminals.Add(new Terminal(terminalId, "POS-01", "Counter 1"));

        var pinHash = _hasher.HashPin("9999");
        var manager = new User(Guid.NewGuid(), "manager2", "Manager Two", pinHash, "passhash", "salt", RoleType.Manager);
        _userRepo.Users.Add(manager);

        var request = new LoginPinRequest("manager2", "9999", terminalId);
        var result = await _service.LoginWithPinAsync(request);

        Assert.False(result.Success);
        Assert.Equal("AUTH_FAILED", result.ErrorCode);
        Assert.Equal("Invalid credentials.", result.ErrorMessage);
    }

    [Fact]
    public async Task LoginWithPin_InactiveTerminal_ReturnsGenericAuthFailed()
    {
        var terminalId = Guid.NewGuid();
        var inactiveTerminal = new Terminal(terminalId, "POS-01", "Counter 1");
        inactiveTerminal.Deactivate();
        _terminalRepo.Terminals.Add(inactiveTerminal);

        var pinHash = _hasher.HashPin("1234");
        var cashier = new User(Guid.NewGuid(), "cashier9", "Cashier Nine", pinHash, "passhash", "salt", RoleType.Cashier);
        _userRepo.Users.Add(cashier);

        var request = new LoginPinRequest("cashier9", "1234", terminalId);
        var result = await _service.LoginWithPinAsync(request);

        Assert.False(result.Success);
        Assert.Equal("AUTH_FAILED", result.ErrorCode);
        Assert.Equal("Invalid credentials.", result.ErrorMessage);
    }

    [Fact]
    public async Task LoginWithPin_FiveFailedAttempts_LocksOutAccount()
    {
        var terminalId = Guid.NewGuid();
        _terminalRepo.Terminals.Add(new Terminal(terminalId, "POS-01", "Counter 1"));

        var pinHash = _hasher.HashPin("5555");
        var cashier = new User(Guid.NewGuid(), "cashier_lock", "Lock Test Cashier", pinHash, "passhash", "salt", RoleType.Cashier);
        _userRepo.Users.Add(cashier);

        // Fail 5 times with wrong PIN
        for (int i = 0; i < 5; i++)
        {
            var res = await _service.LoginWithPinAsync(new LoginPinRequest("cashier_lock", "0000", terminalId));
            Assert.False(res.Success);
            Assert.Equal("AUTH_FAILED", res.ErrorCode);
        }

        Assert.True(cashier.IsLockedOut(DateTime.UtcNow));

        // Even with the CORRECT pin now, login is rejected due to lockout
        var lockedResult = await _service.LoginWithPinAsync(new LoginPinRequest("cashier_lock", "5555", terminalId));
        Assert.False(lockedResult.Success);
        Assert.Equal("AUTH_FAILED", lockedResult.ErrorCode);
    }

    [Fact]
    public async Task Logout_RevokesSession()
    {
        var token = _sessionManager.CreateSession(Guid.NewGuid(), "user1", "User One", RoleType.Cashier, null, TimeSpan.FromHours(1));

        var logoutResult = await _service.LogoutAsync(token);

        Assert.True(logoutResult.Success);
        var val = await _sessionManager.ValidateSessionAsync(token);
        Assert.False(val.IsValid);
    }
}
