using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.E2E.Tests.Fixtures;

namespace RetailPOS.E2E.Tests.Helpers;

public record SeededStoreEnvironment(
    StoreServerFixture Fixture,
    string OwnerToken,
    string ManagerToken,
    VirtualPosTerminal Counter1,
    VirtualPosTerminal Counter2,
    VirtualPosTerminal Counter3,
    ProductManagementDto Product,
    Guid CustomerId,
    Guid SupplierId
);

public static class E2ETestSeedHelper
{
    public static async Task<SeededStoreEnvironment> SeedThreeCounterStoreAsync(
        StoreServerFixture fixture,
        decimal initialStock = 100m,
        long costPricePaise = 500,
        long sellingPricePaise = 1000,
        int taxCategoryId = 1,
        long customerCreditLimitPaise = 10000)
    {
        // 1. Setup Owner
        var ownerToken = await fixture.SetupOwnerAsync(
            storeName: "Pilot Store E2E",
            username: "pilot_owner",
            password: "OwnerPassword123!",
            pin: "1234");

        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 2. Create Manager user
        var createMgrRes = await ownerClient.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "mgr_user", "Store Manager", "MgrPassword123!", "2222", RoleType.Manager));
        createMgrRes.EnsureSuccessStatusCode();

        var loginMgrRes = await ownerClient.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("mgr_user", "MgrPassword123!"));
        loginMgrRes.EnsureSuccessStatusCode();
        var mgrToken = (await loginMgrRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        // 3. Create 3 Cashier users (one per counter for strict session-counter mapping)
        var createCashier1 = await ownerClient.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_1", "Cashier Counter 1", "CashierPass123!", "1111", RoleType.Cashier));
        createCashier1.EnsureSuccessStatusCode();

        var createCashier2 = await ownerClient.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_2", "Cashier Counter 2", "CashierPass123!", "2222", RoleType.Cashier));
        createCashier2.EnsureSuccessStatusCode();

        var createCashier3 = await ownerClient.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_3", "Cashier Counter 3", "CashierPass123!", "3333", RoleType.Cashier));
        createCashier3.EnsureSuccessStatusCode();

        // 4. Create 3 Virtual Terminals
        var c1 = fixture.CreateVirtualTerminal("COUNTER-01", "Counter 1", IPAddress.Parse("192.168.1.101"));
        var c2 = fixture.CreateVirtualTerminal("COUNTER-02", "Counter 2", IPAddress.Parse("192.168.1.102"));
        var c3 = fixture.CreateVirtualTerminal("COUNTER-03", "Counter 3", IPAddress.Parse("192.168.1.103"));

        await fixture.RegisterTerminalInDatabaseAsync(c1);
        await fixture.RegisterTerminalInDatabaseAsync(c2);
        await fixture.RegisterTerminalInDatabaseAsync(c3);

        // 5. Log in each cashier on their respective counter via PIN
        var login1 = await c1.LoginPinAsync("cashier_1", "1111");
        if (!login1) throw new InvalidOperationException("Failed to log in cashier 1 on Counter 1");

        var login2 = await c2.LoginPinAsync("cashier_2", "2222");
        if (!login2) throw new InvalidOperationException("Failed to log in cashier 2 on Counter 2");

        var login3 = await c3.LoginPinAsync("cashier_3", "3333");
        if (!login3) throw new InvalidOperationException("Failed to log in cashier 3 on Counter 3");

        // 6. Create Standard Product (GST 0% / TaxCategoryId = 1)
        var prodRes = await ownerClient.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
            Sku: $"SKU-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name: "Concurrency Standard Product",
            UnitCode: "PC",
            CostPricePaise: costPricePaise,
            SellingPricePaise: sellingPricePaise,
            TaxCategoryId: taxCategoryId,
            InitialStock: initialStock,
            ReorderLevel: 2m));
        prodRes.EnsureSuccessStatusCode();
        var product = (await prodRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;

        // 7. Create Customer with defined credit limit
        var custRes = await ownerClient.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest(
            Name: "Credit Test Customer",
            Phone: "9876543210",
            Email: "credit_cust@test.local",
            Address: "123 High Street",
            Gstin: null,
            CreditLimitPaise: customerCreditLimitPaise,
            Notes: "Concurrency test customer"));
        custRes.EnsureSuccessStatusCode();
        var customerId = (await custRes.Content.ReadFromJsonAsync<ApiResponse<CustomerDto>>())!.Data!.Id;

        // 8. Create Supplier
        var supRes = await ownerClient.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest(
            Name: "Concurrency Test Supplier",
            ContactPerson: "Supplier Agent",
            Phone: "9123456789",
            Email: "supplier@test.local",
            Address: "Industrial Area Depot",
            TaxNumber: "GSTIN-CONC-001",
            PaymentTermsDays: 30,
            Notes: "Test supplier"));
        supRes.EnsureSuccessStatusCode();
        var supplierId = (await supRes.Content.ReadFromJsonAsync<ApiResponse<SupplierDto>>())!.Data!.Id;

        return new SeededStoreEnvironment(
            fixture,
            ownerToken,
            mgrToken,
            c1,
            c2,
            c3,
            product,
            customerId,
            supplierId);
    }
}
