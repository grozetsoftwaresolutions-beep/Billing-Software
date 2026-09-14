using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class HardwareRoleAuthorizationTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string ownerToken, Guid terminalId, string cashierToken, string managerToken, string adminToken)> SetupRolesAsync()
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

        // 3. Create Cashier
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_cashier", "Cashier User", "CashierPass123!", "1111", RoleType.Cashier));

        // 4. Create Manager
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "test_manager", "Manager User", "ManagerPass123!", "2222", RoleType.Manager));

        // 5. Create Admin
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

        return (factory, client, ownerToken, terminalId, cashierToken, managerToken, adminToken);
    }

    [Fact]
    public async Task TerminalIsolation_TerminalACannotAccessTerminalBHardwareConfig()
    {
        var (factory, client, ownerToken, terminalId1, cashierToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            // Owner registers Terminal 2
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            var term2Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T2", "Second Terminal"));
            var terminalId2 = (await term2Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

            // Cashier bound to Terminal 1 attempts to read Terminal 2's hardware config -> FORBIDDEN (403 TERMINAL_MISMATCH)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId1.ToString());

            var mismatchRes = await client.GetAsync($"/api/v1/terminals/{terminalId2}/hardware-config");
            Assert.Equal(HttpStatusCode.Forbidden, mismatchRes.StatusCode);
            var mismatchBody = await mismatchRes.Content.ReadFromJsonAsync<ApiResponse<TerminalHardwareConfigDto>>();
            Assert.Equal("TERMINAL_MISMATCH", mismatchBody?.ErrorCode);

            // Cashier bound to Terminal 1 reads Terminal 1's hardware config -> ALLOWED (200 OK)
            var allowedRes = await client.GetAsync($"/api/v1/terminals/{terminalId1}/hardware-config");
            Assert.Equal(HttpStatusCode.OK, allowedRes.StatusCode);

            // Owner can read Terminal 2's config -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            var ownerReadRes = await client.GetAsync($"/api/v1/terminals/{terminalId2}/hardware-config");
            Assert.Equal(HttpStatusCode.OK, ownerReadRes.StatusCode);
        }
    }

    [Fact]
    public async Task DrawerKickRoleAuthorization_CashierForbidden_ManagerAndAdminAllowed()
    {
        var (factory, client, _, terminalId, cashierToken, managerToken, adminToken) = await SetupRolesAsync();
        using (factory)
        {
            // Cashier attempts manual drawer kick -> FORBIDDEN (403)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var cashierRes = await client.PostAsJsonAsync("/api/v1/hardware/drawer/test-kick", new DrawerKickRequestDto("Cashier manual kick", terminalId));
            Assert.Equal(HttpStatusCode.Forbidden, cashierRes.StatusCode);

            // Manager attempts manual drawer kick -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var managerRes = await client.PostAsJsonAsync("/api/v1/hardware/drawer/test-kick", new DrawerKickRequestDto("Manager drawer audit", terminalId));
            Assert.Equal(HttpStatusCode.OK, managerRes.StatusCode);

            // Admin attempts manual drawer kick -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var adminRes = await client.PostAsJsonAsync("/api/v1/hardware/drawer/test-kick", new DrawerKickRequestDto("Admin routine test", terminalId));
            Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
        }
    }

    [Fact]
    public async Task HardwareEndpoints_TestPrint_Scale_And_ConfigUpdateRoles()
    {
        var (factory, client, _, terminalId, cashierToken, _, adminToken) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            // 1. Cashier test print -> 200 OK
            var printRes = await client.PostAsJsonAsync("/api/v1/hardware/printer/test-print", new TestPrintRequestDto());
            Assert.Equal(HttpStatusCode.OK, printRes.StatusCode);

            // 2. Cashier scale weight -> 200 OK
            var weightRes = await client.GetAsync("/api/v1/hardware/scale/current-weight");
            Assert.Equal(HttpStatusCode.OK, weightRes.StatusCode);

            // 3. Cashier scale zero & tare -> 200 OK
            var zeroRes = await client.PostAsync("/api/v1/hardware/scale/zero", null);
            Assert.Equal(HttpStatusCode.OK, zeroRes.StatusCode);

            var tareRes = await client.PostAsync("/api/v1/hardware/scale/tare", null);
            Assert.Equal(HttpStatusCode.OK, tareRes.StatusCode);

            // 4. Cashier status -> 200 OK
            var statusRes = await client.GetAsync("/api/v1/hardware/status");
            Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);

            // 5. Cashier updates hardware config -> FORBIDDEN (403)
            var configReq = new UpdateTerminalHardwareConfigRequest(PaperWidth: PaperWidth.Width58mm);
            var updateRes = await client.PutAsJsonAsync($"/api/v1/terminals/{terminalId}/hardware-config", configReq);
            Assert.Equal(HttpStatusCode.Forbidden, updateRes.StatusCode);

            // 6. Admin updates hardware config -> ALLOWED (200 OK)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var adminUpdateRes = await client.PutAsJsonAsync($"/api/v1/terminals/{terminalId}/hardware-config", configReq);
            Assert.Equal(HttpStatusCode.OK, adminUpdateRes.StatusCode);
        }
    }

    [Fact]
    public async Task ConcurrentReprint_AtomicallyIncrementsReprintCountWithoutLostUpdates()
    {
        var (factory, client, _, terminalId, cashierToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            // Seed a sale invoice directly into the database
            var invoiceId = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
                var invoice = new SaleInvoice(
                    invoiceId, "INV-CONCURRENT-001", Guid.NewGuid(), terminalId, Guid.NewGuid(),
                    PricingModel.TaxInclusive, Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(),
                    Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(5000));

                invoice.AddPayment(new PaymentRecord(
                    Guid.NewGuid(), invoiceId, PaymentMethod.Cash, Money.FromMinorUnits(5000), Money.FromMinorUnits(5000), Money.Zero()));

                db.SalesInvoices.Add(invoice);
                await db.SaveChangesAsync();
            }

            // Verify initial state
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            var getRes = await client.GetAsync($"/api/v1/sales/{invoiceId}");
            Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
            var invDto = (await getRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            Assert.Equal(0, invDto.ReprintCount);

            // Fire 5 concurrent reprint confirmation requests
            var reprintTasks = Enumerable.Range(1, 5).Select(i =>
            {
                var clientInstance = factory.CreateClient();
                clientInstance.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
                clientInstance.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());
                return clientInstance.PostAsJsonAsync($"/api/v1/sales/{invoiceId}/confirm-reprint", new ConfirmReprintRequest($"Concurrent reprint {i}"));
            }).ToList();

            var responses = await Task.WhenAll(reprintTasks);

            // All 5 must succeed
            foreach (var r in responses)
            {
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            }

            // Verify final ReprintCount is exactly 5
            var finalRes = await client.GetAsync($"/api/v1/sales/{invoiceId}");
            Assert.Equal(HttpStatusCode.OK, finalRes.StatusCode);
            var finalInv = (await finalRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            Assert.Equal(5, finalInv.ReprintCount);
            Assert.NotNull(finalInv.LastReprintedAtUtc);
        }
    }

    [Fact]
    public async Task TerminalHardwareConfig_OmittedHeader_NonAdminForbidden()
    {
        var (factory, client, ownerToken, terminalId1, cashierToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            // Register a second terminal with Owner role
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            var term2Res = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T2", "Second Terminal"));
            var terminalId2 = (await term2Res.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

            // Cashier logs in (session bound to Terminal 1)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

            // Cashier calls Terminal 2 config WITH NO X-Terminal-ID header -> MUST be rejected (403)
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            var resNoHeader = await client.GetAsync($"/api/v1/terminals/{terminalId2}/hardware-config");
            Assert.Equal(HttpStatusCode.Forbidden, resNoHeader.StatusCode);

            // Cashier calls Terminal 2 config WITH X-Terminal-Id = Terminal 1 -> MUST be rejected (403 TERMINAL_MISMATCH)
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId1.ToString());
            var resMismatch = await client.GetAsync($"/api/v1/terminals/{terminalId2}/hardware-config");
            Assert.Equal(HttpStatusCode.Forbidden, resMismatch.StatusCode);
        }
    }

    [Fact]
    public async Task ConcurrentReprint_CaseA_10SuccessfulConfirmations_IncrementsBy10()
    {
        var (factory, client, _, terminalId, cashierToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            var invoiceId = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
                var invoice = new SaleInvoice(
                    invoiceId, "INV-CONC-A-001", Guid.NewGuid(), terminalId, Guid.NewGuid(),
                    PricingModel.TaxInclusive, Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(),
                    Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(5000));
                db.SalesInvoices.Add(invoice);
                await db.SaveChangesAsync();
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            // 10 concurrent successful reprint confirmation requests
            var tasks = Enumerable.Range(1, 10).Select(i =>
            {
                var c = factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
                c.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());
                return c.PostAsJsonAsync($"/api/v1/sales/{invoiceId}/confirm-reprint", new ConfirmReprintRequest($"Case A reprint {i}"));
            }).ToList();

            var responses = await Task.WhenAll(tasks);
            foreach (var r in responses)
            {
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            }

            var finalRes = await client.GetAsync($"/api/v1/sales/{invoiceId}");
            var invDto = (await finalRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            Assert.Equal(10, invDto.ReprintCount);
            Assert.NotNull(invDto.LastReprintedAtUtc);

            // Verify audit logs contain 10 RECEIPT_REPRINTED
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
                var count = db.AuditLogs.Count(a => a.Action == "RECEIPT_REPRINTED" && a.Details != null && a.Details.Contains(invDto.InvoiceNumber));
                Assert.Equal(10, count);
            }
        }
    }

    [Fact]
    public async Task ConcurrentReprint_CaseB_10FailedConfirmations_RemainsUnchanged()
    {
        var (factory, client, _, terminalId, cashierToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            var invoiceId = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
                var invoice = new SaleInvoice(
                    invoiceId, "INV-CONC-B-001", Guid.NewGuid(), terminalId, Guid.NewGuid(),
                    PricingModel.TaxInclusive, Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(),
                    Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(5000));
                db.SalesInvoices.Add(invoice);
                await db.SaveChangesAsync();
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            // 10 concurrent failed reprint reports
            var tasks = Enumerable.Range(1, 10).Select(i =>
            {
                var c = factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
                c.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());
                return c.PostAsJsonAsync($"/api/v1/sales/{invoiceId}/report-reprint-failed", new ReportReprintFailedRequest("Printer paper jammed"));
            }).ToList();

            var responses = await Task.WhenAll(tasks);
            foreach (var r in responses)
            {
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            }

            var finalRes = await client.GetAsync($"/api/v1/sales/{invoiceId}");
            var invDto = (await finalRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            Assert.Equal(0, invDto.ReprintCount);
            Assert.Null(invDto.LastReprintedAtUtc);

            // Verify audit logs contain 10 REPRINT_FAILED
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
                var count = db.AuditLogs.Count(a => a.Action == "REPRINT_FAILED" && a.Details != null && a.Details.Contains(invDto.InvoiceNumber));
                Assert.Equal(10, count);
            }
        }
    }

    [Fact]
    public async Task ConcurrentReprint_CaseC_6SuccessfulAnd4Failed_IncrementsBy6()
    {
        var (factory, client, _, terminalId, cashierToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            var invoiceId = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
                var invoice = new SaleInvoice(
                    invoiceId, "INV-CONC-C-001", Guid.NewGuid(), terminalId, Guid.NewGuid(),
                    PricingModel.TaxInclusive, Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(),
                    Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(5000));
                db.SalesInvoices.Add(invoice);
                await db.SaveChangesAsync();
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Remove("X-Terminal-Id");
            client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());

            // 6 success + 4 failure concurrent requests
            var successTasks = Enumerable.Range(1, 6).Select(i =>
            {
                var c = factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
                c.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());
                return c.PostAsJsonAsync($"/api/v1/sales/{invoiceId}/confirm-reprint", new ConfirmReprintRequest($"Case C success {i}"));
            });

            var failTasks = Enumerable.Range(1, 4).Select(i =>
            {
                var c = factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
                c.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString());
                return c.PostAsJsonAsync($"/api/v1/sales/{invoiceId}/report-reprint-failed", new ReportReprintFailedRequest("Out of paper"));
            });

            var allTasks = successTasks.Concat(failTasks).ToList();
            var responses = await Task.WhenAll(allTasks);

            foreach (var r in responses)
            {
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            }

            var finalRes = await client.GetAsync($"/api/v1/sales/{invoiceId}");
            var invDto = (await finalRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            Assert.Equal(6, invDto.ReprintCount);
            Assert.NotNull(invDto.LastReprintedAtUtc);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
                var successAudit = db.AuditLogs.Count(a => a.Action == "RECEIPT_REPRINTED" && a.Details != null && a.Details.Contains(invDto.InvoiceNumber));
                var failAudit = db.AuditLogs.Count(a => a.Action == "REPRINT_FAILED" && a.Details != null && a.Details.Contains(invDto.InvoiceNumber));
                Assert.Equal(6, successAudit);
                Assert.Equal(4, failAudit);
            }
        }
    }
}
