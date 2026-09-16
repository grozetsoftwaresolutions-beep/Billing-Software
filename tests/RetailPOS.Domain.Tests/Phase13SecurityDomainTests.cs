using Xunit;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Tests;

public sealed class Phase13SecurityDomainTests
{
    [Fact]
    public void RolePermissions_TotalPermissionCount_IsExactly32()
    {
        Assert.Equal(32, RolePermissions.AllPermissions.Count);
        Assert.Equal(32, Enum.GetValues<Permission>().Length);
    }

    [Fact]
    public void RolePermissions_Owner_HasExactly32Permissions()
    {
        var permissions = RolePermissions.GetPermissions(RoleType.Owner);
        Assert.Equal(32, permissions.Count);
        foreach (var perm in RolePermissions.AllPermissions)
        {
            Assert.True(RolePermissions.HasPermission(RoleType.Owner, perm));
        }
    }

    [Fact]
    public void RolePermissions_Admin_HasExactly29Permissions()
    {
        var permissions = RolePermissions.GetPermissions(RoleType.Admin);
        Assert.Equal(29, permissions.Count);

        Assert.False(RolePermissions.HasPermission(RoleType.Admin, Permission.RestoreDatabase));
        Assert.False(RolePermissions.HasPermission(RoleType.Admin, Permission.ChangeUserRole));
        Assert.False(RolePermissions.HasPermission(RoleType.Admin, Permission.ManageStoreSettings));

        Assert.True(RolePermissions.HasPermission(RoleType.Admin, Permission.ProcessSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Admin, Permission.VoidSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Admin, Permission.ManageUsers));
    }

    [Fact]
    public void RolePermissions_Manager_HasExactly22Permissions()
    {
        var permissions = RolePermissions.GetPermissions(RoleType.Manager);
        Assert.Equal(22, permissions.Count);

        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.CancelPurchase));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ManageUsers));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ChangeUserRole));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ResetUserCredentials));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ManageStoreSettings));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ManageTerminals));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.CreateBackup));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.RestoreDatabase));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ViewAuditLogs));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ExportAuditLogs));

        Assert.True(RolePermissions.HasPermission(RoleType.Manager, Permission.SupervisorOverride));
        Assert.True(RolePermissions.HasPermission(RoleType.Manager, Permission.VoidSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Manager, Permission.RefundSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Manager, Permission.PriceOverride));
    }

    [Fact]
    public void RolePermissions_Cashier_HasExactly7Permissions()
    {
        var permissions = RolePermissions.GetPermissions(RoleType.Cashier);
        Assert.Equal(7, permissions.Count);

        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.ProcessSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.SearchSales));
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.ReprintReceipt));
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.ViewCustomers));
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.ManageCustomers));
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.OpenCashSession));
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.CloseCashSession));

        Assert.False(RolePermissions.HasPermission(RoleType.Cashier, Permission.VoidSale));
        Assert.False(RolePermissions.HasPermission(RoleType.Cashier, Permission.RefundSale));
        Assert.False(RolePermissions.HasPermission(RoleType.Cashier, Permission.PriceOverride));
        Assert.False(RolePermissions.HasPermission(RoleType.Cashier, Permission.SupervisorOverride));
    }

    [Fact]
    public void RolePermissions_Viewer_HasExactly8Permissions()
    {
        var permissions = RolePermissions.GetPermissions(RoleType.Viewer);
        Assert.Equal(8, permissions.Count);

        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.SearchSales));
        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.ViewStock));
        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.ViewPurchases));
        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.ViewSuppliers));
        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.ViewCustomers));
        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.ViewCashSessions));
        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.ViewReports));
        Assert.True(RolePermissions.HasPermission(RoleType.Viewer, Permission.ExportReports));

        Assert.False(RolePermissions.HasPermission(RoleType.Viewer, Permission.ProcessSale));
        Assert.False(RolePermissions.HasPermission(RoleType.Viewer, Permission.VoidSale));
        Assert.False(RolePermissions.HasPermission(RoleType.Viewer, Permission.ManageUsers));
    }

    [Fact]
    public void AuditLog_Instantiation_CapturesAllMandatoryFields()
    {
        var failsafeId = Guid.NewGuid();
        var clearOpId = Guid.NewGuid();
        var sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var log = new AuditLog(
            1,
            "SECURITY_SETTINGS_FAILSAFE_CLEARED",
            "SecuritySettings",
            "failsafe",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Failsafe cleared by Store Owner",
            true,
            userName: "owner1",
            reason: "Settings verified and published",
            beforeJson: "{\"mode\":\"failsafe\"}",
            afterJson: "{\"mode\":\"normal\"}",
            ipAddress: "192.168.1.50",
            correlationId: "corr-12345",
            failsafeInstanceId: failsafeId,
            clearOperationId: clearOpId,
            targetSettingsSha256: sha256);

        Assert.Equal(1, log.Id);
        Assert.Equal("SECURITY_SETTINGS_FAILSAFE_CLEARED", log.Action);
        Assert.Equal("SecuritySettings", log.EntityName);
        Assert.Equal("owner1", log.UserName);
        Assert.Equal("Settings verified and published", log.Reason);
        Assert.Equal("192.168.1.50", log.IpAddress);
        Assert.Equal("corr-12345", log.CorrelationId);
        Assert.Equal(failsafeId, log.FailsafeInstanceId);
        Assert.Equal(clearOpId, log.ClearOperationId);
        Assert.Equal(sha256, log.TargetSettingsSha256);
        Assert.True(log.Success);
    }

    [Fact]
    public void SaleInvoice_Void_TransitionsCompletedToVoided()
    {
        var invoice = CreateTestInvoice();
        var userId = Guid.NewGuid();

        invoice.Void(userId, "admin1", "Customer changed mind");

        Assert.Equal(SaleStatus.Voided, invoice.Status);
        Assert.Equal(userId, invoice.VoidedByUserId);
        Assert.Equal("admin1", invoice.VoidedByUsername);
        Assert.Equal("Customer changed mind", invoice.VoidReason);
        Assert.NotNull(invoice.VoidedAtUtc);
    }

    [Fact]
    public void SaleInvoice_Void_AlreadyVoided_ThrowsDomainException()
    {
        var invoice = CreateTestInvoice();
        invoice.Void(Guid.NewGuid(), "admin1", "First void");

        var ex = Assert.Throws<DomainException>(() => invoice.Void(Guid.NewGuid(), "admin2", "Second void"));
        Assert.Equal("SALE_ALREADY_VOIDED", ex.ErrorCode);
    }

    [Fact]
    public void SaleInvoice_Void_PartiallyRefunded_ThrowsDomainException()
    {
        var invoice = CreateTestInvoice();
        invoice.MarkPartiallyRefunded();

        var ex = Assert.Throws<DomainException>(() => invoice.Void(Guid.NewGuid(), "admin1", "Void refund"));
        Assert.Equal("CANNOT_VOID_REFUNDED_SALE", ex.ErrorCode);
    }

    [Fact]
    public void User_Lockout_ExceedingMaxFailedPinAttempts_SetsLockoutTimer()
    {
        var user = new User(Guid.NewGuid(), "cashier1", "Cashier One", "hash", "pwhash", "salt", RoleType.Cashier);
        var now = DateTime.UtcNow;

        user.RecordFailedPinAttempt(3, TimeSpan.FromMinutes(15), now);
        Assert.False(user.IsLockedOut(now));
        Assert.Equal(1, user.FailedPinAttempts);

        user.RecordFailedPinAttempt(3, TimeSpan.FromMinutes(15), now);
        Assert.False(user.IsLockedOut(now));
        Assert.Equal(2, user.FailedPinAttempts);

        user.RecordFailedPinAttempt(3, TimeSpan.FromMinutes(15), now);
        Assert.True(user.IsLockedOut(now));
        Assert.Equal(3, user.FailedPinAttempts);
        Assert.NotNull(user.LockoutEndUtc);
    }

    [Fact]
    public void User_Unlock_ResetsFailedAttemptsAndTimer()
    {
        var user = new User(Guid.NewGuid(), "cashier1", "Cashier One", "hash", "pwhash", "salt", RoleType.Cashier);
        var now = DateTime.UtcNow;

        user.RecordFailedPinAttempt(3, TimeSpan.FromMinutes(15), now);
        user.RecordFailedPinAttempt(3, TimeSpan.FromMinutes(15), now);
        user.RecordFailedPinAttempt(3, TimeSpan.FromMinutes(15), now);
        Assert.True(user.IsLockedOut(now));

        user.ResetPinLockout();
        Assert.False(user.IsLockedOut(now));
        Assert.Equal(0, user.FailedPinAttempts);
        Assert.Null(user.LockoutEndUtc);
    }

    private static SaleInvoice CreateTestInvoice()
    {
        return new SaleInvoice(
            Guid.NewGuid(),
            "INV-2026-0001",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            PricingModel.TaxInclusive,
            Money.FromDecimal(100.00m, Currency.INR),
            Money.Zero(Currency.INR),
            Money.Zero(Currency.INR),
            Money.FromDecimal(100.00m, Currency.INR),
            Money.FromDecimal(18.00m, Currency.INR),
            Money.Zero(Currency.INR),
            Money.FromDecimal(118.00m, Currency.INR));
    }
}
