using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class ProductSearchValidationTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client)> SetupAuthClientAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Store", "search_owner", "Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "search_owner", "OwnerPass123!"));
        var body = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Data!.Token);

        return (factory, client);
    }

    [Fact]
    public async Task SearchPos_QueryExceeding100Chars_Returns400WithQueryTooLong()
    {
        var (factory, client) = await SetupAuthClientAsync();
        using (factory)
        {
            var longQuery = new string('A', 101);
            var response = await client.GetAsync($"/api/v1/products/search?query={longQuery}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.False(body.Success);
            Assert.Equal("QUERY_TOO_LONG", body.ErrorCode);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchPos_EmptyOrWhitespaceQuery_Returns400WithQueryRequired(string emptyQuery)
    {
        var (factory, client) = await SetupAuthClientAsync();
        using (factory)
        {
            var response = await client.GetAsync($"/api/v1/products/search?query={Uri.EscapeDataString(emptyQuery)}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.False(body.Success);
            Assert.Equal("QUERY_REQUIRED", body.ErrorCode);
        }
    }

    [Fact]
    public async Task SearchPos_PageSizeAbove50_ClampedTo50()
    {
        var (factory, client) = await SetupAuthClientAsync();
        using (factory)
        {
            // Create a valid product to search
            await client.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                "CLAMP-01", "Clamp Item", "PC", 100, 150, 1));

            var response = await client.GetAsync("/api/v1/products/search?query=CLAMP&pageSize=100");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProductPosDto>>>();
            Assert.NotNull(body?.Data);
            Assert.Equal(50, body.Data.PageSize);
        }
    }
}