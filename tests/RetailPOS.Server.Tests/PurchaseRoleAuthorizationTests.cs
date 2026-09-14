using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class PurchaseRoleAuthorizationTests
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
            "SKU-AUTH-P7", "Auth Purchase Product", "pcs", 5000, 10000, 1, InitialStock: 10m));
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
    public async Task Cashier_IsForbidden_FromAllSupplierAndPurchaseEndpoints()
    {
        var (factory, client, _, terminalId, cashierToken, _, _, productId) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            // 1. Supplier endpoints
            var supGetRes = await client.GetAsync("/api/v1/suppliers");
            Assert.Equal(HttpStatusCode.Forbidden, supGetRes.StatusCode);

            var supPostRes = await client.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest("Hacked Supplier"));
            Assert.Equal(HttpStatusCode.Forbidden, supPostRes.StatusCode);

            var supPayRes = await client.PostAsJsonAsync($"/api/v1/suppliers/{Guid.NewGuid()}/payments",
                new RecordSupplierPaymentRequest(null, 1000, PaymentMethod.Cash, null, null, false, "memo", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, supPayRes.StatusCode);

            // 2. Purchase endpoints
            var purGetRes = await client.GetAsync("/api/v1/purchases");
            Assert.Equal(HttpStatusCode.Forbidden, purGetRes.StatusCode);

            var purPostRes = await client.PostAsJsonAsync("/api/v1/purchases", new CreatePurchaseRequest(
                Guid.NewGuid(), "INV-HACK", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(productId, 1m, 5000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "hack", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, purPostRes.StatusCode);

            var purCancelRes = await client.PostAsJsonAsync($"/api/v1/purchases/{Guid.NewGuid()}/cancel",
                new CancelPurchaseRequest("hack cancel", RefundSettlementMode.StoreCredit, null, null, Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, purCancelRes.StatusCode);
        }
    }

    [Fact]
    public async Task Manager_CanInwardAndPay_ButForbiddenFromCancellingPurchaseOrDeactivatingSupplier()
    {
        var (factory, client, _, terminalId, _, managerToken, _, productId) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            // 1. Manager creates supplier -> 201 Created
            var supCreateRes = await client.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest("Manager Supplier", "Bob", "9876543210"));
            Assert.Equal(HttpStatusCode.Created, supCreateRes.StatusCode);
            var supplierId = (await supCreateRes.Content.ReadFromJsonAsync<ApiResponse<SupplierDto>>())!.Data!.Id;

            // 2. Manager creates purchase invoice -> 201 Created
            var purCreateRes = await client.PostAsJsonAsync("/api/v1/purchases", new CreatePurchaseRequest(
                supplierId, "SUP-MGR-001", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(productId, 5m, 5000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "Mgr purchase", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Created, purCreateRes.StatusCode);
            var purchaseId = (await purCreateRes.Content.ReadFromJsonAsync<ApiResponse<PurchaseInvoiceDto>>())!.Data!.Id;

            // 3. Manager records supplier payment -> 201 Created
            var payRes = await client.PostAsJsonAsync($"/api/v1/suppliers/{supplierId}/payments",
                new RecordSupplierPaymentRequest(purchaseId, 10000, PaymentMethod.Cash, null, null, false, "Mgr payment", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Created, payRes.StatusCode);

            // 4. Manager attempts to cancel purchase -> FORBIDDEN (403)
            var cancelRes = await client.PostAsJsonAsync($"/api/v1/purchases/{purchaseId}/cancel",
                new CancelPurchaseRequest("Defective", RefundSettlementMode.StoreCredit, null, null, Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, cancelRes.StatusCode);

            // 5. Manager attempts to deactivate supplier -> FORBIDDEN (403)
            var deactRes = await client.DeleteAsync($"/api/v1/suppliers/{supplierId}");
            Assert.Equal(HttpStatusCode.Forbidden, deactRes.StatusCode);
        }
    }

    [Fact]
    public async Task AdminAndOwner_CanCancelPurchasesAndDeactivateSuppliers()
    {
        var (factory, client, ownerToken, terminalId, _, managerToken, adminToken, productId) = await SetupRolesAsync();
        using (factory)
        {
            // 1. Manager creates a purchase
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var supRes = await client.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest("Admin Test Supplier"));
            var supplierId = (await supRes.Content.ReadFromJsonAsync<ApiResponse<SupplierDto>>())!.Data!.Id;

            var purRes = await client.PostAsJsonAsync("/api/v1/purchases", new CreatePurchaseRequest(
                supplierId, "SUP-ADM-001", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(productId, 2m, 5000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "To cancel", Guid.NewGuid()));
            var purchaseId = (await purRes.Content.ReadFromJsonAsync<ApiResponse<PurchaseInvoiceDto>>())!.Data!.Id;

            // 2. Admin cancels purchase -> 200 OK
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var cancelRes = await client.PostAsJsonAsync($"/api/v1/purchases/{purchaseId}/cancel",
                new CancelPurchaseRequest("Admin authorization override", RefundSettlementMode.StoreCredit, null, null, Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

            // 3. Owner deactivates supplier -> 200 OK
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            var deactRes = await client.DeleteAsync($"/api/v1/suppliers/{supplierId}");
            Assert.Equal(HttpStatusCode.OK, deactRes.StatusCode);
        }
    }
}
