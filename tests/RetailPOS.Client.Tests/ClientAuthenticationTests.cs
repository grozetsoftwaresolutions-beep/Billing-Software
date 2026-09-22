using System;
using System.Globalization;
using System.Threading.Tasks;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using Xunit;

namespace RetailPOS.Client.Tests;

public class ClientAuthenticationTests
{
    [Fact]
    public void SessionManager_InitialState_IsNotAuthenticated()
    {
        var session = ClientSessionManager.Instance;
        session.ClearSession();

        Assert.Equal(ClientSessionState.NotAuthenticated, session.State);
        Assert.False(session.IsAuthenticated);
        Assert.False(session.IsLocked);
        Assert.Null(session.CurrentToken);
        Assert.Null(session.CurrentUsername);
        Assert.Null(session.CurrentRole);
    }

    [Fact]
    public void SessionManager_SetActiveSession_PopulatesFieldsAndTransitionsToActive()
    {
        var session = ClientSessionManager.Instance;
        var token = "jwt-mock-token-xyz";
        var expiresAt = DateTime.UtcNow.AddHours(8);
        var response = new LoginResponse(
            token,
            Guid.NewGuid(),
            "cashier1",
            "Cashier One",
            RoleType.Cashier,
            Guid.NewGuid(),
            expiresAt);

        session.SetActiveSession(response);

        Assert.Equal(ClientSessionState.Active, session.State);
        Assert.True(session.IsAuthenticated);
        Assert.False(session.IsLocked);
        Assert.Equal(token, session.CurrentToken);
        Assert.Equal("cashier1", session.CurrentUsername);
        Assert.Equal("Cashier One", session.CurrentFullName);
        Assert.Equal(RoleType.Cashier, session.CurrentRole);
        Assert.Equal(expiresAt, session.ExpiresAtUtc);
    }

    [Fact]
    public void SessionManager_TransitionToLocked_PreservesTokenAndUserContext()
    {
        var session = ClientSessionManager.Instance;
        var token = "jwt-locked-preserve-token";
        var response = new LoginResponse(
            token,
            Guid.NewGuid(),
            "manager1",
            "Store Manager",
            RoleType.Manager,
            null,
            DateTime.UtcNow.AddHours(4));

        session.SetActiveSession(response);
        session.TransitionToLocked();

        Assert.Equal(ClientSessionState.Locked, session.State);
        Assert.True(session.IsAuthenticated);
        Assert.True(session.IsLocked);
        Assert.Equal(token, session.CurrentToken); // Critical invariant: Token MUST NOT be destroyed!
        Assert.Equal("manager1", session.CurrentUsername);
        Assert.Equal("Store Manager", session.CurrentFullName);
    }

    [Fact]
    public void SessionManager_UnlockFromLocked_ReturnsToActive()
    {
        var session = ClientSessionManager.Instance;
        var token = "jwt-unlock-token";
        var response = new LoginResponse(
            token,
            Guid.NewGuid(),
            "admin",
            "System Admin",
            RoleType.Admin,
            null,
            DateTime.UtcNow.AddHours(2));

        session.SetActiveSession(response);
        session.TransitionToLocked();
        Assert.True(session.IsLocked);

        session.TransitionToActive();
        Assert.Equal(ClientSessionState.Active, session.State);
        Assert.True(session.IsAuthenticated);
        Assert.False(session.IsLocked);
        Assert.Equal(token, session.CurrentToken);
    }

    [Fact]
    public void SessionManager_ClearSession_DestroysTokenAndClearsCredentials()
    {
        var session = ClientSessionManager.Instance;
        var response = new LoginResponse(
            "temp-token",
            Guid.NewGuid(),
            "user",
            "Temp User",
            RoleType.Cashier,
            null,
            DateTime.UtcNow.AddHours(1));

        session.SetActiveSession(response);
        session.ClearSession();

        Assert.Equal(ClientSessionState.NotAuthenticated, session.State);
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.CurrentToken);
        Assert.Null(session.CurrentUsername);
        Assert.Null(session.CurrentRole);
    }

    [Fact]
    public void AuthenticationClient_RejectsPlainHttp_ForCredentials()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new AuthenticationClient(null, "http://localhost:5100");
        });
    }

    [Fact]
    public void AuthenticationClient_AuthoritativePort_IsHttps5101()
    {
        var client = new AuthenticationClient();
        Assert.Contains(WindowsServiceDeploymentSpecification.HttpsPort.ToString(CultureInfo.InvariantCulture), client.HttpsBaseUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", client.HttpsBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RolePermissions_EnforcesRoleBoundaries()
    {
        // Cashier permissions
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.ProcessSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Cashier, Permission.SearchSales));
        Assert.False(RolePermissions.HasPermission(RoleType.Cashier, Permission.ViewStock));
        Assert.False(RolePermissions.HasPermission(RoleType.Cashier, Permission.ViewReports));
        Assert.False(RolePermissions.HasPermission(RoleType.Cashier, Permission.ManageUsers));

        // Manager permissions
        Assert.True(RolePermissions.HasPermission(RoleType.Manager, Permission.ProcessSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Manager, Permission.ViewStock));
        Assert.True(RolePermissions.HasPermission(RoleType.Manager, Permission.ViewReports));
        Assert.False(RolePermissions.HasPermission(RoleType.Manager, Permission.ManageUsers));

        // Admin permissions
        Assert.True(RolePermissions.HasPermission(RoleType.Admin, Permission.ProcessSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Admin, Permission.ViewStock));
        Assert.True(RolePermissions.HasPermission(RoleType.Admin, Permission.ViewReports));
        Assert.True(RolePermissions.HasPermission(RoleType.Admin, Permission.ManageUsers));
        Assert.False(RolePermissions.HasPermission(RoleType.Admin, Permission.RestoreDatabase));

        // Owner permissions
        Assert.True(RolePermissions.HasPermission(RoleType.Owner, Permission.ProcessSale));
        Assert.True(RolePermissions.HasPermission(RoleType.Owner, Permission.ManageUsers));
        Assert.True(RolePermissions.HasPermission(RoleType.Owner, Permission.RestoreDatabase));
    }

    [Fact]
    public async Task LoginViewModel_RejectsEmptyUsernameOrPassword()
    {
        var vm = new LoginViewModel();
        vm.IsPinMode = false;
        vm.Username = "";

        var result = await vm.LoginAsync("secret123");
        Assert.False(result);
        Assert.True(vm.HasError);
        Assert.Contains("username", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        vm.Username = "validuser";
        result = await vm.LoginAsync("");
        Assert.False(result);
        Assert.True(vm.HasError);
        Assert.Contains("password", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginViewModel_PinModeWithoutEnrolledTerminal_IsPrevented()
    {
        var vm = new LoginViewModel();
        vm.IsPinMode = true;
        vm.IsTerminalEnrolled = false;
        vm.Username = "cashier1";

        var result = await vm.LoginAsync("1234");
        Assert.False(result);
        Assert.True(vm.HasError);
        Assert.Contains("enrolled", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LockViewModel_RejectsEmptyPin()
    {
        var vm = new LockViewModel();
        var result = await vm.UnlockAsync("");
        Assert.False(result);
        Assert.True(vm.HasError);
        Assert.Contains("PIN", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
