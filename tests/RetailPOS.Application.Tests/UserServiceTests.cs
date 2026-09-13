using RetailPOS.Application.Services;
using RetailPOS.Application.Tests.Fakes;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Application.Tests;

public sealed class UserServiceTests
{
    private readonly FakeUserRepository _userRepo = new();
    private readonly FakeAuditLogRepository _auditRepo = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly UserService _service;

    public UserServiceTests()
    {
        _service = new UserService(_userRepo, _auditRepo, _unitOfWork, _hasher);
    }

    [Fact]
    public async Task Manager_CanCreateCashier_Succeeds()
    {
        var managerId = Guid.NewGuid();
        var request = new CreateUserRequest("newcashier", "New Cashier", "Password123#", "1234", RoleType.Cashier);

        var result = await _service.CreateUserAsync(request, managerId, RoleType.Manager, "manager1");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("newcashier", result.Data.Username);
        Assert.Equal(RoleType.Cashier, result.Data.Role);
        Assert.False(result.Data.IsLocked);
    }

    [Theory]
    [InlineData(RoleType.Manager)]
    [InlineData(RoleType.Admin)]
    [InlineData(RoleType.Owner)]
    public async Task Manager_AttemptingToCreateManagerAdminOrOwner_ThrowsInsufficientPermissions(RoleType targetRole)
    {
        var managerId = Guid.NewGuid();
        var request = new CreateUserRequest("user_test", "Test User", "Password123#", "1234", targetRole);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.CreateUserAsync(request, managerId, RoleType.Manager, "manager1"));

        Assert.Equal("INSUFFICIENT_ROLE_PERMISSIONS", ex.ErrorCode);
    }

    [Fact]
    public async Task Admin_AttemptingToCreateOwner_ThrowsInsufficientPermissions()
    {
        var adminId = Guid.NewGuid();
        var request = new CreateUserRequest("newowner", "New Owner", "Password123#", "1234", RoleType.Owner);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.CreateUserAsync(request, adminId, RoleType.Admin, "admin1"));

        Assert.Equal("INSUFFICIENT_ROLE_PERMISSIONS", ex.ErrorCode);
    }

    [Fact]
    public async Task Owner_CanCreateAnyRole_Succeeds()
    {
        var ownerId = Guid.NewGuid();
        var request = new CreateUserRequest("newadmin", "New Admin", "Password123#", "1234", RoleType.Admin);

        var result = await _service.CreateUserAsync(request, ownerId, RoleType.Owner, "owner1");

        Assert.True(result.Success);
        Assert.Equal(RoleType.Admin, result.Data!.Role);
    }

    [Fact]
    public async Task User_CannotDeactivateThemselves()
    {
        var adminId = Guid.NewGuid();
        var admin = new User(adminId, "admin1", "Admin", "pinhash", "passhash", "salt", RoleType.Admin);
        _userRepo.Users.Add(admin);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.DeactivateUserAsync(adminId, adminId, RoleType.Admin, "admin1"));

        Assert.Equal("CANNOT_DEACTIVATE_SELF", ex.ErrorCode);
    }

    [Fact]
    public async Task CannotDeactivateLastRemainingOwner()
    {
        var ownerId = Guid.NewGuid();
        var owner = new User(ownerId, "owner1", "Owner One", "pinhash", "passhash", "salt", RoleType.Owner);
        _userRepo.Users.Add(owner);

        var otherAdminId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.DeactivateUserAsync(ownerId, otherAdminId, RoleType.Owner, "owner2"));

        Assert.Equal("CANNOT_DEACTIVATE_LAST_OWNER", ex.ErrorCode);
    }
}
