using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class ProductSecurityAndDataLeakTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string managerToken, string cashierToken)> SetupUsersAndTokensAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Setup Owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Retail", "test_owner", "Owner Admin", "OwnerPass123!", "1234"));

        var ownerLoginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "test_owner", "OwnerPass123!"));
        var ownerBody = await ownerLoginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var ownerToken = ownerBody!.Data!.Token;

        // Authorize as Owner to register terminal and users
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest(
            "POS-01", "Counter 1", "Receipt Printer 1"));
        var termDto = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!;

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "mgr_user", "Store Manager", "MgrPass123!", "1111", RoleType.Manager));

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "csh_user", "Store Cashier", "CshPass123!", "5555", RoleType.Cashier));

        // 2. Login Manager with password
        var mgrLoginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "mgr_user", "MgrPass123!"));
        var mgrBody = await mgrLoginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var managerToken = mgrBody!.Data!.Token;

        // 3. Login Cashier with PIN at POS-01
        var cshLoginRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "csh_user", "5555", termDto.Id));
        var cshBody = await cshLoginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var cashierToken = cshBody!.Data!.Token;

        return (factory, client, managerToken, cashierToken);
    }

    [Fact]
    public async Task Cashier_PosEndpoints_MustNeverLeakInventoryQuantitiesOrCostPrice()
    {
        var (factory, client, managerToken, cashierToken) = await SetupUsersAndTokensAsync();
        using (factory)
        {
            // Manager creates product
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            var createRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                Sku: "LEAK-TEST-01",
                Name: "Leak Test Item",
                UnitCode: "PC",
                CostPricePaise: 1250, // 12.50 INR cost
                SellingPricePaise: 2000, // 20.00 INR selling
                TaxCategoryId: 1,
                CategoryId: null,
                BrandId: null,
                PrimaryBarcode: "8901234567890",
                InitialStock: 50m,
                ReorderLevel: 10m,
                Notes: "Confidential wholesale supplier"));
            Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
            var createdProduct = (await createRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

            // Now switch to Cashier session
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

            // 1. POS Search endpoint
            var searchRes = await client.GetAsync("/api/v1/products/search?query=LEAK");
            Assert.Equal(HttpStatusCode.OK, searchRes.StatusCode);
            var searchJson = await searchRes.Content.ReadAsStringAsync();

            // Strict data-leak assertions on raw wire JSON
            Assert.DoesNotContain("costPrice", searchJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("currentStock", searchJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reorderLevel", searchJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isLowStock", searchJson, StringComparison.OrdinalIgnoreCase);

            var searchResult = await searchRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductPosDto>>>();
            Assert.NotNull(searchResult?.Data?.Items);
            Assert.Single(searchResult.Data.Items);
            var posItem = searchResult.Data.Items[0];
            Assert.Equal("LEAK-TEST-01", posItem.Sku);
            Assert.Equal(2000, posItem.SellingPricePaise);
            Assert.True(posItem.InStock);
            Assert.True(posItem.IsActive);
            Assert.Equal("8901234567890", posItem.PrimaryBarcode);

            // 2. POS Barcode resolution endpoint
            var barcodeRes = await client.GetAsync("/api/v1/products/barcode/8901234567890");
            Assert.Equal(HttpStatusCode.OK, barcodeRes.StatusCode);
            var barcodeJson = await barcodeRes.Content.ReadAsStringAsync();

            Assert.DoesNotContain("costPrice", barcodeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("currentStock", barcodeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reorderLevel", barcodeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isLowStock", barcodeJson, StringComparison.OrdinalIgnoreCase);

            // 3. Cashier forbidden from Management endpoints
            var getMgmtRes = await client.GetAsync($"/api/v1/products/{createdProduct.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, getMgmtRes.StatusCode);

            var postProductRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                "CSH-PROD", "Cashier Prod", "PC", 100, 150, 1));
            Assert.Equal(HttpStatusCode.Forbidden, postProductRes.StatusCode);

            var putProductRes = await client.PutAsJsonAsync($"/api/v1/products/{createdProduct.Id}", new UpdateProductRequest(
                "Hacked", "PC", 100, 150, 1));
            Assert.Equal(HttpStatusCode.Forbidden, putProductRes.StatusCode);
        }
    }

    [Fact]
    public async Task Manager_ReceivesFullManagementDto_WithStockAndCostPrice()
    {
        var (factory, client, managerToken, _) = await SetupUsersAndTokensAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

            var createRes = await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                Sku: "MGR-PROD-01",
                Name: "Manager Product",
                UnitCode: "PC",
                CostPricePaise: 4500,
                SellingPricePaise: 6000,
                TaxCategoryId: 1,
                InitialStock: 15m,
                ReorderLevel: 5m));
            Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
            var created = (await createRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

            var getRes = await client.GetAsync($"/api/v1/products/{created.Id}");
            Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

            var mgmtDto = (await getRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;
            Assert.Equal(4500, mgmtDto.CostPricePaise);
            Assert.Equal(6000, mgmtDto.SellingPricePaise);
            Assert.Equal(15m, mgmtDto.CurrentStock);
            Assert.Equal(5m, mgmtDto.ReorderLevel);
            Assert.False(mgmtDto.IsLowStock);
        }
    }
}