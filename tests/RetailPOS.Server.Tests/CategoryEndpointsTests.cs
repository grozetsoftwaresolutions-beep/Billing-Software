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

public sealed class CategoryEndpointsTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string managerToken, string cashierToken)> SetupUsersAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Store", "cat_owner", "Owner", "OwnerPass123!", "1234"));

        var ownerLoginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "cat_owner", "OwnerPass123!"));
        var ownerToken = (await ownerLoginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-03", "Counter 3", "Printer 3"));
        var termDto = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!;

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("cat_mgr", "Manager", "MgrPass123!", "1111", RoleType.Manager));
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("cat_csh", "Cashier", "CshPass123!", "3333", RoleType.Cashier));

        var mgrLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("cat_mgr", "MgrPass123!"));
        var managerToken = (await mgrLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        var cshLogin = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest("cat_csh", "3333", termDto.Id));
        var cashierToken = (await cshLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, managerToken, cashierToken);
    }

    [Fact]
    public async Task Category_CaseInsensitiveUniqueness_Enforced()
    {
        var (factory, client, managerToken, _) = await SetupUsersAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

            var res1 = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("Beverages"));
            Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

            // Lowercase variant should be rejected
            var res2 = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("beverages"));
            Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
            var body2 = await res2.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.Equal("CATEGORY_ALREADY_EXISTS", body2?.ErrorCode);

            // Uppercase variant should also be rejected
            var res3 = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("BEVERAGES"));
            Assert.Equal(HttpStatusCode.BadRequest, res3.StatusCode);
            var body3 = await res3.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.Equal("CATEGORY_ALREADY_EXISTS", body3?.ErrorCode);
        }
    }

    [Fact]
    public async Task Brand_CaseInsensitiveUniqueness_Enforced()
    {
        var (factory, client, managerToken, _) = await SetupUsersAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

            var res1 = await client.PostAsJsonAsync("/api/v1/brands", new CreateBrandRequest("Britannia"));
            Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

            // Lowercase duplicate rejected
            var res2 = await client.PostAsJsonAsync("/api/v1/brands", new CreateBrandRequest("britannia"));
            Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
            var body2 = await res2.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.Equal("BRAND_ALREADY_EXISTS", body2?.ErrorCode);

            // Uppercase duplicate rejected
            var res3 = await client.PostAsJsonAsync("/api/v1/brands", new CreateBrandRequest("BRITANNIA"));
            Assert.Equal(HttpStatusCode.BadRequest, res3.StatusCode);
            var body3 = await res3.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.Equal("BRAND_ALREADY_EXISTS", body3?.ErrorCode);
        }
    }

    [Fact]
    public async Task CategoryAndBrand_RoleAuthorization_CashierCanReadButCannotCreate()
    {
        var (factory, client, managerToken, cashierToken) = await SetupUsersAsync();
        using (factory)
        {
            // Manager creates one category and one brand
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("Snacks"));
            await client.PostAsJsonAsync("/api/v1/brands", new CreateBrandRequest("Lays"));

            // Switch to Cashier
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

            // Cashier reads categories -> 200 OK
            var catGet = await client.GetAsync("/api/v1/categories");
            Assert.Equal(HttpStatusCode.OK, catGet.StatusCode);
            var catList = await catGet.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<CategoryDto>>>();
            Assert.NotNull(catList?.Data);
            Assert.Contains(catList.Data, c => c.Name == "Snacks");

            // Cashier reads brands -> 200 OK
            var brandGet = await client.GetAsync("/api/v1/brands");
            Assert.Equal(HttpStatusCode.OK, brandGet.StatusCode);
            var brandList = await brandGet.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<BrandDto>>>();
            Assert.NotNull(brandList?.Data);
            Assert.Contains(brandList.Data, b => b.Name == "Lays");

            // Cashier attempts to create category -> 403 Forbidden
            var catCreate = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("HackedCategory"));
            Assert.Equal(HttpStatusCode.Forbidden, catCreate.StatusCode);

            // Cashier attempts to create brand -> 403 Forbidden
            var brandCreate = await client.PostAsJsonAsync("/api/v1/brands", new CreateBrandRequest("HackedBrand"));
            Assert.Equal(HttpStatusCode.Forbidden, brandCreate.StatusCode);
        }
    }
}