using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Configuration;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class SaleRoleAuthorizationTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string ownerToken, Guid terminalId, string cashierToken, string managerToken, string adminToken, Guid productId)> SetupRolesAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Setup Owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Retail POS", "pos_owner", "Store Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "pos_owner", "OwnerPass123!"));
        var ownerToken = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 2. Register Terminal
        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T1", "Main Terminal"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // 3. Create Product
        var prodRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
            "SKU-AUTH-01", "Auth Test Product", "pcs", 5000, 10000, 1, InitialStock: 10m));
        var productId = (await prodRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!.Id;

        // 4. Create Cashier
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_cashier", "Cashier User", "CashierPass123!", "1111", RoleType.Cashier));

        // 5. Create Manager
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_manager", "Manager User", "ManagerPass123!", "2222", RoleType.Manager));

        // 6. Create Admin
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_admin", "Admin User", "AdminPass123!", "3333", RoleType.Admin));

        // Login Cashier
        client.DefaultRequestHeaders.Authorization = null;
        var cashierLogin = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "test_cashier", "1111", terminalId));
        var cashierToken = (await cashierLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Manager
        var managerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_manager", "ManagerPass123!"));
        var managerToken = (await managerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // Login Admin
        var adminLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_admin", "AdminPass123!"));
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, ownerToken, terminalId, cashierToken, managerToken, adminToken, productId);
    }

    [Fact]
    public async Task Cashier_CanCalculateCart_ReturnsOk()
    {
        var (factory, client, _, _, cashierToken, _, _, productId) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

            var request = new CalculateSaleRequest([new CartItemRequest(productId, 1m)], DiscountType.None, 0m, PaymentMethod.Cash);
            var response = await client.PostAsJsonAsync("/api/v1/sales/calculate", request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ApiResponse<CartCalculationDto>>();
            Assert.NotNull(body);
            Assert.True(body.Success);
            Assert.Equal(10000, body.Data!.FinalPayablePaise);
        }
    }

    [Fact]
    public async Task Cashier_CannotRefundSale_Returns403Forbidden()
    {
        var (factory, client, _, terminalId, cashierToken, _, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var refundRequest = new RefundSaleRequest(
                Guid.NewGuid(),
                [new RefundItemRequest(1, 1m)],
                PaymentMethod.Cash,
                "Cashier attempting unauthorized refund");

            var response = await client.PostAsJsonAsync($"/api/v1/sales/{Guid.NewGuid()}/refund", refundRequest);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
        }
    }

    [Fact]
    public async Task Manager_CanAccessRefundEndpoint_DoesNotReturn403Forbidden()
    {
        var (factory, client, _, terminalId, _, managerToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var refundRequest = new RefundSaleRequest(
                Guid.NewGuid(),
                [new RefundItemRequest(1, 1m)],
                PaymentMethod.Cash,
                "Manager authorized refund");

            var response = await client.PostAsJsonAsync($"/api/v1/sales/{Guid.NewGuid()}/refund", refundRequest);

            // Passes role authorization check: either 400 or 404 (sale not found), but NOT 403 Forbidden!
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Cashier_CannotUpdateStoreSettings_Returns403Forbidden()
    {
        var (factory, client, _, _, cashierToken, _, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

            var updateReq = new UpdateStoreSettingsRequest(
                "Hacked Store", "New Address", "9999999999", "GSTIN123", "Thanks", false, "India Standard Time");

            var response = await client.PutAsJsonAsync("/api/v1/store/settings", updateReq);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
        }
    }

    [Fact]
    public async Task Admin_CanUpdateStoreSettings_Returns200Ok()
    {
        var (factory, client, _, _, _, _, adminToken, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            var updateReq = new UpdateStoreSettingsRequest(
                "Updated Store Name", "123 Main St", "9876543210", "GSTIN999", "Visit again!", true, "India Standard Time");

            var response = await client.PutAsJsonAsync("/api/v1/store/settings", updateReq);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ApiResponse<StoreSettingsDto>>();
            Assert.NotNull(body);
            Assert.True(body.Success);
            Assert.Equal("Updated Store Name", body.Data!.StoreName);
            Assert.Equal("India Standard Time", body.Data.TimeZoneId);
        }
    }
}
