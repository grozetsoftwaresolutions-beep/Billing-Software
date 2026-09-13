using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class InventoryEndpointsTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string managerToken, string cashierToken)> SetupUsersAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Store", "inv_owner", "Owner", "OwnerPass123!", "1234"));

        var ownerLoginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "inv_owner", "OwnerPass123!"));
        var ownerBody = await ownerLoginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var ownerToken = ownerBody!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-02", "Counter 2", "Printer 2"));
        var termDto = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!;

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("inv_mgr", "Manager", "MgrPass123!", "1111", RoleType.Manager));
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("inv_csh", "Cashier", "CshPass123!", "2222", RoleType.Cashier));

        var mgrLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("inv_mgr", "MgrPass123!"));
        var managerToken = (await mgrLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        var cshLogin = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest("inv_csh", "2222", termDto.Id));
        var cashierToken = (await cshLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, managerToken, cashierToken);
    }

    [Fact]
    public async Task Cashier_ForbiddenFromAllInventoryEndpoints()
    {
        var (factory, client, managerToken, cashierToken) = await SetupUsersAsync();
        using (factory)
        {
            // Manager creates product
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            var prodRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                "INV-SEC-01", "Inventory Security Item", "PC", 100, 150, 1, InitialStock: 10m, ReorderLevel: 5m));
            var product = (await prodRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

            // Switch to Cashier
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

            // 1. Stock balance
            var stockRes = await client.GetAsync($"/api/v1/inventory/stock/{product.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, stockRes.StatusCode);

            // 2. Adjustments
            var adjRes = await client.PostAsJsonAsync("/api/v1/inventory/adjustments", new StockAdjustmentRequest(
                product.Id, ManualStockAdjustmentType.Increase, 1m, "Unauthorized test"));
            Assert.Equal(HttpStatusCode.Forbidden, adjRes.StatusCode);

            // 3. Low stock listing
            var lowStockRes = await client.GetAsync("/api/v1/inventory/low-stock");
            Assert.Equal(HttpStatusCode.Forbidden, lowStockRes.StatusCode);

            // 4. Movements listing
            var movRes = await client.GetAsync("/api/v1/inventory/movements");
            Assert.Equal(HttpStatusCode.Forbidden, movRes.StatusCode);
        }
    }

    [Fact]
    public async Task LowStockQuery_ReturnsOnlyProductsAtOrBelowReorderLevel()
    {
        var (factory, client, managerToken, _) = await SetupUsersAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

            // Product 1: Stock 10, Reorder 5 -> Not low stock
            await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                "NORMAL-01", "Normal Item", "PC", 100, 150, 1, InitialStock: 10m, ReorderLevel: 5m));

            // Product 2: Stock 4, Reorder 5 -> Low stock
            await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                "LOW-01", "Low Stock Item", "PC", 100, 150, 1, InitialStock: 4m, ReorderLevel: 5m));

            var response = await client.GetAsync("/api/v1/inventory/low-stock");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<StockBalanceDto>>>();
            Assert.NotNull(body?.Data?.Items);
            Assert.Contains(body.Data.Items, item => item.Sku == "LOW-01" && item.IsLowStock);
            Assert.DoesNotContain(body.Data.Items, item => item.Sku == "NORMAL-01");
        }
    }

    [Fact]
    public async Task StockMovements_AppendOnlyLedger_RecordsAdjustmentHistory()
    {
        var (factory, client, managerToken, _) = await SetupUsersAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

            var createRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                "HIST-01", "History Item", "PC", 100, 150, 1, InitialStock: 10m, ReorderLevel: 5m));
            var product = (await createRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

            // Make adjustment
            await client.PostAsJsonAsync("/api/v1/inventory/adjustments", new StockAdjustmentRequest(
                product.Id, ManualStockAdjustmentType.Increase, 5m, "Received shipment"));

            var histRes = await client.GetAsync($"/api/v1/inventory/movements?productId={product.Id}");
            Assert.Equal(HttpStatusCode.OK, histRes.StatusCode);

            var histBody = await histRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<StockMovementDto>>>();
            Assert.NotNull(histBody?.Data?.Items);
            Assert.Equal(2, histBody.Data.Items.Count); // 1 initial + 1 adjustment
            Assert.Contains(histBody.Data.Items, m => m.Reason == "Received shipment" && m.QuantityChanged == 5m);
        }
    }
}