using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class SecuredEmergencyRollbackTests
{
    private static async Task<(CustomWebApplicationFactory factory, HttpClient client, string ownerToken, string recoveryKey, Guid terminalId)> SetupOwnerAndClientAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var setupRes = await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Rollback Secure Store", "sec_owner", "Security Owner", "OwnerPass123!", "1234"));
        var setupEnvelope = await setupRes.Content.ReadFromJsonAsync<ApiResponse<SetupOwnerResponse>>();
        var recoveryKey = setupEnvelope!.Data!.StoreMasterRecoveryKey;

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "sec_owner", "OwnerPass123!"));
        var loginEnvelope = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        var ownerToken = loginEnvelope!.Data!.Token;

        // Register terminal so other users (e.g. Cashier) can log in
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-SEC-1", "Security Terminal"));
        var termEnvelope = await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>();
        var terminalId = termEnvelope!.Data!.Id;

        return (factory, client, ownerToken, recoveryKey, terminalId);
    }

    private static async Task<string> CreateCashierTokenAsync(HttpClient client, string ownerToken, Guid terminalId)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "sec_cashier", "Sec Cashier", "CashierPass123!", "5555", RoleType.Cashier));

        client.DefaultRequestHeaders.Authorization = null;
        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "sec_cashier", "5555", terminalId));
        var loginEnvelope = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        return loginEnvelope!.Data!.Token;
    }

    [Fact]
    public async Task EmergencyRollback_ExternalIp_Returns403Forbidden()
    {
        var (factory, client, ownerToken, recoveryKey, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.105");

            var request = new EmergencyRestoreRequestDto
            {
                FileName = "backup.db",
                StoreMasterRecoveryKey = recoveryKey,
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = EmergencyRestoreRequestDto.RequiredConfirmationPhrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task EmergencyRollback_Unauthenticated_Returns401Unauthorized()
    {
        var (factory, client, _, recoveryKey, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            var request = new EmergencyRestoreRequestDto
            {
                FileName = "backup.db",
                StoreMasterRecoveryKey = recoveryKey,
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = EmergencyRestoreRequestDto.RequiredConfirmationPhrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("UNAUTHENTICATED", body.ErrorCode);
        }
    }

    [Fact]
    public async Task EmergencyRollback_AuthenticatedNonOwner_Returns403Forbidden()
    {
        var (factory, client, ownerToken, recoveryKey, terminalId) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            var cashierToken = await CreateCashierTokenAsync(client, ownerToken, terminalId);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            var request = new EmergencyRestoreRequestDto
            {
                FileName = "backup.db",
                StoreMasterRecoveryKey = recoveryKey,
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = EmergencyRestoreRequestDto.RequiredConfirmationPhrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
        }
    }

    [Fact]
    public async Task EmergencyRollback_MissingDestructiveConfirmation_Returns400BadRequest()
    {
        var (factory, client, ownerToken, recoveryKey, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            var request = new EmergencyRestoreRequestDto
            {
                FileName = "backup.db",
                StoreMasterRecoveryKey = recoveryKey,
                ConfirmDestructiveRollback = false,
                ConfirmationPhrase = EmergencyRestoreRequestDto.RequiredConfirmationPhrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("CONFIRMATION_REQUIRED", body);
        }
    }

    [Fact]
    public async Task EmergencyRollback_MissingConfirmationPhrase_Returns400BadRequest()
    {
        var (factory, client, ownerToken, recoveryKey, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            var request = new EmergencyRestoreRequestDto
            {
                FileName = "backup.db",
                StoreMasterRecoveryKey = recoveryKey,
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = "" // Missing phrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("CONFIRMATION_PHRASE_INVALID", body);
        }
    }

    [Fact]
    public async Task EmergencyRollback_IncorrectConfirmationPhrase_Returns400BadRequest()
    {
        var (factory, client, ownerToken, recoveryKey, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            var request = new EmergencyRestoreRequestDto
            {
                FileName = "backup.db",
                StoreMasterRecoveryKey = recoveryKey,
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = "INCORRECT_CONFIRMATION_PHRASE" // Incorrect phrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("CONFIRMATION_PHRASE_INVALID", body);
        }
    }

    [Fact]
    public async Task EmergencyRollback_InvalidMasterKeyFormat_Returns400BadRequest()
    {
        var (factory, client, ownerToken, _, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            var request = new EmergencyRestoreRequestDto
            {
                FileName = "backup.db",
                StoreMasterRecoveryKey = "short_invalid_key",
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = EmergencyRestoreRequestDto.RequiredConfirmationPhrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("INVALID_MASTER_KEY", body);
        }
    }

    [Fact]
    public async Task EmergencyRollback_InvalidMasterKeyAuthentication_Returns403Forbidden()
    {
        var (factory, client, ownerToken, _, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            // 64 hex characters but wrong key
            var request = new EmergencyRestoreRequestDto
            {
                FileName = "any_backup.db",
                StoreMasterRecoveryKey = new string('F', 64),
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = EmergencyRestoreRequestDto.RequiredConfirmationPhrase
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task EmergencyRollback_OwnerWithValidKeyAndConfirmationPhrase_CanProceed()
    {
        var (factory, client, ownerToken, recoveryKey, _) = await SetupOwnerAndClientAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

            // 1. Create a legitimate backup
            var backupRes = await client.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto { Reason = "Pre-rollback test backup" });
            Assert.True(backupRes.IsSuccessStatusCode);
            var backupData = (await backupRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>())!.Data!;

            // 2. Perform emergency restore with Owner token + loopback + valid Recovery Key + exact phrase
            var request = new EmergencyRestoreRequestDto
            {
                FileName = backupData.FileName,
                StoreMasterRecoveryKey = recoveryKey,
                ConfirmDestructiveRollback = true,
                ConfirmationPhrase = EmergencyRestoreRequestDto.RequiredConfirmationPhrase
            };

            var restoreRes = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
            Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);

            var body = await restoreRes.Content.ReadAsStringAsync();
            Assert.Contains("Restored", body);
        }
    }
}
