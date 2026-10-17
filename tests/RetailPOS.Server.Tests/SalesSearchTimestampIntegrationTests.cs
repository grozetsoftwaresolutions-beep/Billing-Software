using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class SalesSearchTimestampIntegrationTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string ownerToken, Guid terminalId)> SetupOwnerAndClientAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Setup Store & Owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Timestamp Store", "ts_owner", "Store Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "ts_owner", "OwnerPass123!"));
        var ownerToken = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 2. Register Terminal
        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("TS-T1", "Timestamp Terminal"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        return (factory, client, ownerToken, terminalId);
    }

    private static async Task SeedSaleInvoiceWithExactTimestampAsync(
        CustomWebApplicationFactory factory,
        string invoiceNumber,
        Guid terminalId,
        DateTime exactCreatedAtUtc)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();

        var invoiceId = Guid.NewGuid();
        var invoice = new SaleInvoice(
            id: invoiceId,
            invoiceNumber: invoiceNumber,
            clientTransactionId: Guid.NewGuid(),
            terminalId: terminalId,
            cashierId: Guid.NewGuid(),
            pricingModel: PricingModel.TaxInclusive,
            grossSubtotal: Money.FromMinorUnits(5000),
            totalLineDiscounts: Money.Zero(),
            invoiceDiscount: Money.Zero(),
            taxableSubtotal: Money.FromMinorUnits(5000),
            totalTax: Money.Zero(),
            roundingAdjustment: Money.Zero(),
            finalPayable: Money.FromMinorUnits(5000));

        invoice.AddPayment(new PaymentRecord(
            Guid.NewGuid(), invoiceId, PaymentMethod.Cash, Money.FromMinorUnits(5000), Money.FromMinorUnits(5000), Money.Zero()));

        db.SalesInvoices.Add(invoice);
        // Explicitly override CreatedAtUtc on the EF Core entry before saving
        db.Entry(invoice).Property(s => s.CreatedAtUtc).CurrentValue = exactCreatedAtUtc;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TimestampPrecision_EmpiricalPipelineProof_TestsBoundaryHorizon()
    {
        var (factory, client, _, terminalId) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            // Store Timezone: India Standard Time (UTC+05:30)
            var ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var targetDay = new DateOnly(2026, 10, 16);

            // Calculate start and next-day UTC instants
            var startLocal = targetDay.ToDateTime(TimeOnly.MinValue); // 2026-10-16 00:00:00.0000000
            var nextDayLocal = targetDay.AddDays(1).ToDateTime(TimeOnly.MinValue); // 2026-10-17 00:00:00.0000000

            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, ist); // 2026-10-15 18:30:00.0000000 UTC
            var nextDayUtc = TimeZoneInfo.ConvertTimeToUtc(nextDayLocal, ist); // 2026-10-16 18:30:00.0000000 UTC

            // Seed test sales at exact boundary points:
            // S1: Exactly at start boundary (00:00:00.000 IST)
            var s1Utc = fromUtc;
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-TS-START", terminalId, s1Utc);

            // S2: Late in the day - exactly 1 second before midnight (23:59:59.000 IST)
            var s2Utc = nextDayUtc.AddSeconds(-1);
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-TS-LATE-SEC", terminalId, s2Utc);

            // S3: Late in the day - exactly 1 millisecond before midnight (23:59:59.999 IST)
            var s3Utc = nextDayUtc.AddMilliseconds(-1);
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-TS-LATE-MS", terminalId, s3Utc);

            // S4: Late in the day - exactly 1 tick before midnight (23:59:59.9999999 IST)
            var s4Utc = nextDayUtc.AddTicks(-1);
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-TS-LATE-TICK", terminalId, s4Utc);

            // S5: Exactly at midnight of the NEXT calendar day (00:00:00.000 IST on 2026-10-17)
            var s5Utc = nextDayUtc;
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-TS-NEXT-DAY-MIDNIGHT", terminalId, s5Utc);

            // Query using toUtc = nextDayUtc.AddTicks(-1)
            var toUtc = nextDayUtc.AddTicks(-1);
            var url = $"/api/v1/sales?fromUtc={Uri.EscapeDataString(fromUtc.ToString("o"))}&toUtc={Uri.EscapeDataString(toUtc.ToString("o"))}&pageSize=50";

            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var pagedResult = (await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<SaleSummaryDto>>>())!.Data!;
            var returnedInvoices = pagedResult.Items.Select(x => x.InvoiceNumber).ToHashSet();

            // S1 (Start of day) MUST be included
            Assert.Contains("INV-TS-START", returnedInvoices);

            // S2 (1s before midnight) MUST be included
            Assert.Contains("INV-TS-LATE-SEC", returnedInvoices);

            // S3 (1ms before midnight) MUST be included
            Assert.Contains("INV-TS-LATE-MS", returnedInvoices);

            // S4 (1 tick before midnight) MUST be included if tick precision is preserved
            Assert.Contains("INV-TS-LATE-TICK", returnedInvoices);

            // S5 (Next-day midnight) MUST be strictly excluded
            Assert.DoesNotContain("INV-TS-NEXT-DAY-MIDNIGHT", returnedInvoices);
        }
    }

    [Fact]
    public async Task TimestampPrecision_MultiDayRange_CorrectlyIncludesSpannedDaysAndExcludesBoundaryOverflow()
    {
        var (factory, client, _, terminalId) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            var ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var startDay = new DateOnly(2026, 10, 14);
            var endDay = new DateOnly(2026, 10, 16);

            var startLocal = startDay.ToDateTime(TimeOnly.MinValue);
            var nextDayLocal = endDay.AddDays(1).ToDateTime(TimeOnly.MinValue);

            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, ist);
            var nextDayUtc = TimeZoneInfo.ConvertTimeToUtc(nextDayLocal, ist);
            var toUtc = nextDayUtc.AddTicks(-1);

            // Prior to range: Oct 13 23:59:59.999 IST
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-MULTI-BEFORE", terminalId, fromUtc.AddTicks(-1));

            // Start of range: Oct 14 00:00:00.000 IST
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-MULTI-DAY1-START", terminalId, fromUtc);

            // Middle of range: Oct 15 12:00:00.000 IST
            var midUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Unspecified), ist);
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-MULTI-MID", terminalId, midUtc);

            // End of range: Oct 16 23:59:59.9999999 IST
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-MULTI-END-TICK", terminalId, toUtc);

            // After range: Oct 17 00:00:00.000 IST
            await SeedSaleInvoiceWithExactTimestampAsync(factory, "INV-MULTI-AFTER", terminalId, nextDayUtc);

            var url = $"/api/v1/sales?fromUtc={Uri.EscapeDataString(fromUtc.ToString("o"))}&toUtc={Uri.EscapeDataString(toUtc.ToString("o"))}&pageSize=50";
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var pagedResult = (await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<SaleSummaryDto>>>())!.Data!;
            var returnedInvoices = pagedResult.Items.Select(x => x.InvoiceNumber).ToHashSet();

            Assert.DoesNotContain("INV-MULTI-BEFORE", returnedInvoices);
            Assert.Contains("INV-MULTI-DAY1-START", returnedInvoices);
            Assert.Contains("INV-MULTI-MID", returnedInvoices);
            Assert.Contains("INV-MULTI-END-TICK", returnedInvoices);
            Assert.DoesNotContain("INV-MULTI-AFTER", returnedInvoices);
        }
    }
}
