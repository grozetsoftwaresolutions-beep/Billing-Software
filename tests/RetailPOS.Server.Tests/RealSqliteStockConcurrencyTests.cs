using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class RealSqliteStockConcurrencyTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string token)> SetupOwnerClientAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Concurrency Store", "conc_owner", "Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "conc_owner", "OwnerPass123!"));
        var body = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var token = body!.Data!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (factory, client, token);
    }

    [Fact]
    public async Task ConcurrentStockDecreases_MustPreventNegativeStockAndLostUpdates()
    {
        var (factory, client, token) = await SetupOwnerClientAsync();
        using (factory)
        {
            // Create product with initial stock = 10
            var createRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                Sku: "CONC-PROD-01",
                Name: "Concurrent Test Product",
                UnitCode: "PC",
                CostPricePaise: 500,
                SellingPricePaise: 1000,
                TaxCategoryId: 1,
                InitialStock: 10m,
                ReorderLevel: 2m));

            Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
            var product = (await createRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

            // Prepare 2 separate HTTP clients with the same token to fire truly concurrent requests
            using var clientA = factory.CreateClient();
            using var clientB = factory.CreateClient();
            clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var reqA = new StockAdjustmentRequest(
                product.Id,
                ManualStockAdjustmentType.Decrease,
                Quantity: 7m,
                Reason: "Concurrent Sale A");

            var reqB = new StockAdjustmentRequest(
                product.Id,
                ManualStockAdjustmentType.Decrease,
                Quantity: 7m,
                Reason: "Concurrent Sale B");

            // Dispatch both requests concurrently
            var taskA = clientA.PostAsJsonAsync("/api/v1/inventory/adjustments", reqA);
            var taskB = clientB.PostAsJsonAsync("/api/v1/inventory/adjustments", reqB);

            var responses = await Task.WhenAll(taskA, taskB);
            var statusCodes = responses.Select(r => r.StatusCode).ToList();

            // Exactly ONE should succeed with 200 OK, and exactly ONE should fail with 400 Bad Request
            Assert.Contains(HttpStatusCode.OK, statusCodes);
            Assert.Contains(HttpStatusCode.Conflict, statusCodes);

            var failedResponse = responses.First(r => r.StatusCode == HttpStatusCode.Conflict);
            var failedBody = await failedResponse.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(failedBody);
            Assert.Equal("INSUFFICIENT_STOCK", failedBody.ErrorCode);

            // Authoritative verify: Stock balance must be exactly 3 (10 - 7 = 3)
            var balanceRes = await client.GetAsync($"/api/v1/inventory/stock/{product.Id}");
            Assert.Equal(HttpStatusCode.OK, balanceRes.StatusCode);
            var balanceBody = await balanceRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>();
            Assert.NotNull(balanceBody?.Data);
            Assert.Equal(3m, balanceBody.Data.CurrentStock);
            Assert.False(balanceBody.Data.IsLowStock); // 3 > 2 (reorder level was 2)
        }
    }

    [Fact]
    public async Task ConcurrentStockIncreases_ShouldAccumulateCorrectlyWithoutLostUpdates()
    {
        var (factory, client, token) = await SetupOwnerClientAsync();
        using (factory)
        {
            var createRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                Sku: "CONC-INC-01",
                Name: "Concurrent Increase Item",
                UnitCode: "PC",
                CostPricePaise: 500,
                SellingPricePaise: 1000,
                TaxCategoryId: 1,
                InitialStock: 0m,
                ReorderLevel: 5m));

            Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
            var product = (await createRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

            using var client1 = factory.CreateClient();
            using var client2 = factory.CreateClient();
            client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var req1 = new StockAdjustmentRequest(product.Id, ManualStockAdjustmentType.Increase, 15m, "Batch 1 received");
            var req2 = new StockAdjustmentRequest(product.Id, ManualStockAdjustmentType.Increase, 25m, "Batch 2 received");

            var task1 = client1.PostAsJsonAsync("/api/v1/inventory/adjustments", req1);
            var task2 = client2.PostAsJsonAsync("/api/v1/inventory/adjustments", req2);

            var responses = await Task.WhenAll(task1, task2);
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

            var balanceRes = await client.GetAsync($"/api/v1/inventory/stock/{product.Id}");
            Assert.Equal(HttpStatusCode.OK, balanceRes.StatusCode);
            var balanceBody = await balanceRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>();
            Assert.NotNull(balanceBody?.Data);
            Assert.Equal(40m, balanceBody.Data.CurrentStock); // 0 + 15 + 25 = 40
            Assert.False(balanceBody.Data.IsLowStock);
        }
    }
}