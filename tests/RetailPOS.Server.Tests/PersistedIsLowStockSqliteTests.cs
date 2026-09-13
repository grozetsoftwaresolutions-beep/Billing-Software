using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class PersistedIsLowStockSqliteTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string managerToken, string dbPath)> SetupAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Setup owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Store", "low_owner", "Owner", "OwnerPass123!", "1234"));

        var ownerLoginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "low_owner", "OwnerPass123!"));
        var ownerToken = (await ownerLoginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 2. Create manager
        await client.PostAsJsonAsync("/api/v1/users", new RetailPOS.Contracts.Users.CreateUserRequest(
            "low_mgr", "Manager", "MgrPass123!", "1111", RoleType.Manager));

        var mgrLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "low_mgr", "MgrPass123!"));
        var managerToken = (await mgrLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, managerToken, factory.DatabasePath);
    }

    private static async Task<int> QueryPersistedIsLowStockRawAsync(string dbPath, Guid productId)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT is_low_stock FROM stock_balances WHERE ProductId = '{productId}' COLLATE NOCASE;";
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task PersistedIsLowStock_InRealSqliteDatabase_TracksThresholdCrossingsCorrectly()
    {
        var (factory, client, managerToken, dbPath) = await SetupAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

            // Step 1: Create Product with stock = 10, reorderLevel = 5
            var createRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                Sku: "PERSIST-LOW-01",
                Name: "Persisted Low Stock Item",
                UnitCode: "PC",
                CostPricePaise: 500,
                SellingPricePaise: 1000,
                TaxCategoryId: 1,
                InitialStock: 10m,
                ReorderLevel: 5m));
            Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
            var product = (await createRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

            // Verify initial persisted state in SQLite file: 10 > 5 -> is_low_stock = 0 (false)
            var initialLow = await QueryPersistedIsLowStockRawAsync(dbPath, product.Id);
            Assert.Equal(0, initialLow);

            // Step 2: Decrease stock across the reorder threshold (10 - 6 = 4 <= 5)
            var decRes = await client.PostAsJsonAsync("/api/v1/inventory/adjustments", new StockAdjustmentRequest(
                product.Id, ManualStockAdjustmentType.Decrease, 6m, "Sold items"));
            Assert.Equal(HttpStatusCode.OK, decRes.StatusCode);

            // Verify persisted state in SQLite file: 4 <= 5 -> is_low_stock = 1 (true)
            var decreasedLow = await QueryPersistedIsLowStockRawAsync(dbPath, product.Id);
            Assert.Equal(1, decreasedLow);

            // Step 3: Increase stock back above the reorder threshold (4 + 3 = 7 > 5)
            var incRes = await client.PostAsJsonAsync("/api/v1/inventory/adjustments", new StockAdjustmentRequest(
                product.Id, ManualStockAdjustmentType.Increase, 3m, "Restocked items"));
            Assert.Equal(HttpStatusCode.OK, incRes.StatusCode);

            // Verify persisted state in SQLite file: 7 > 5 -> is_low_stock = 0 (false)
            var increasedLow = await QueryPersistedIsLowStockRawAsync(dbPath, product.Id);
            Assert.Equal(0, increasedLow);

            // Step 4: Change reorder level across current stock level (raise reorder to 8: current stock 7 <= 8)
            var updateRes1 = await client.PutAsJsonAsync($"/api/v1/products/{product.Id}", new UpdateProductRequest(
                product.Name, product.UnitCode, product.CostPricePaise, product.SellingPricePaise,
                product.TaxCategoryId, null, null, ReorderLevel: 8m));
            Assert.Equal(HttpStatusCode.OK, updateRes1.StatusCode);

            // Verify persisted state in SQLite file: 7 <= 8 -> is_low_stock = 1 (true)
            var raisedReorderLow = await QueryPersistedIsLowStockRawAsync(dbPath, product.Id);
            Assert.Equal(1, raisedReorderLow);

            // Step 5: Change reorder level back below current stock (lower reorder to 4: current stock 7 > 4)
            var updateRes2 = await client.PutAsJsonAsync($"/api/v1/products/{product.Id}", new UpdateProductRequest(
                product.Name, product.UnitCode, product.CostPricePaise, product.SellingPricePaise,
                product.TaxCategoryId, null, null, ReorderLevel: 4m));
            Assert.Equal(HttpStatusCode.OK, updateRes2.StatusCode);

            // Verify persisted state in SQLite file: 7 > 4 -> is_low_stock = 0 (false)
            var loweredReorderLow = await QueryPersistedIsLowStockRawAsync(dbPath, product.Id);
            Assert.Equal(0, loweredReorderLow);
        }
    }
}