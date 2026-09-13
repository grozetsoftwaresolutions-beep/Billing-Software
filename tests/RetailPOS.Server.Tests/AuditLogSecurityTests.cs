using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class AuditLogSecurityTests
{
    [Fact]
    public async Task SecurityEvents_AreAudited_AndNeverContainPlaintextCredentialsOrTokens()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        const string rawPassword = "SuperSecretPassword123!";
        const string rawPin = "9876";

        // 1. Setup owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Security Store", "audit_owner", "Audit Owner", rawPassword, rawPin));

        // 2. Failed login attempt
        await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "audit_owner", "WrongPassword123!"));

        // 3. Successful login
        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "audit_owner", rawPassword));
        var token = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 4. Register terminal
        await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-AUDIT", "Audit Counter"));

        // 5. Create user
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "cashier_aud", "Audit Cashier", "CashierSecret99!", "4433", RoleType.Cashier));

        // 6. Inspect audit logs directly in database
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
        var logs = await db.AuditLogs.ToListAsync();

        Assert.NotEmpty(logs);

        // Verify actions exist
        var actions = logs.Select(l => l.Action).ToList();
        Assert.Contains("OWNER_SETUP", actions);
        Assert.Contains("LOGIN_FAILURE", actions);
        Assert.Contains("LOGIN_SUCCESS", actions);
        Assert.Contains("TERMINAL_REGISTERED", actions);
        Assert.Contains("USER_CREATED", actions);

        // Verify NO credentials or tokens are stored in audit logs
        foreach (var log in logs)
        {
            Assert.DoesNotContain(rawPassword, log.Details ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(rawPin, log.Details ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("CashierSecret99!", log.Details ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("4433", log.Details ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(token, log.Details ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
