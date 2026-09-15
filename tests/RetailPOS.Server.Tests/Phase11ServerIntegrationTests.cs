namespace RetailPOS.Server.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;
using Xunit;

public class Phase11ServerIntegrationTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Phase11ServerIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _client.Dispose();
        _factory.Dispose();
    }

    private string? _cachedToken;

    private async Task<string> SetupOwnerAndGetTokenAsync()
    {
        if (_cachedToken != null)
        {
            return _cachedToken;
        }

        const string username = "phase11_owner";
        await _client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Phase 11 Supermarket", username, "Owner", "OwnerPass123!", "1234"));

        var loginRes = await _client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            username, "OwnerPass123!"));
        var loginEnvelope = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        _cachedToken = loginEnvelope?.Data?.Token ?? throw new InvalidOperationException("Failed to obtain owner token: " + loginEnvelope?.ErrorMessage);
        return _cachedToken;
    }

    [Fact]
    public async Task GetBackupHistory_Returns200WithList()
    {
        var token = await SetupOwnerAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/backups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<List<BackupRecordDto>>>();
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
    }

    [Fact]
    public async Task CreateBackup_Manual_Returns201Created()
    {
        var token = await SetupOwnerAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto
        {
            Reason = "Integration test manual backup"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>();
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        Assert.Equal("Manual", envelope.Data.Type);
        Assert.Equal("Succeeded", envelope.Data.Status);
    }

    [Fact]
    public async Task GetAndUpdateBackupSettings_Returns200()
    {
        var token = await SetupOwnerAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // GET settings
        var getRes = await _client.GetAsync("/api/v1/backups/settings");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var getEnv = await getRes.Content.ReadFromJsonAsync<ApiResponse<BackupSettingsDto>>();
        Assert.NotNull(getEnv?.Data);

        // PUT settings
        var updateRes = await _client.PutAsJsonAsync("/api/v1/backups/settings", new UpdateBackupSettingsDto
        {
            DailyBackupEnabled = true,
            DailyBackupTime = "02:30",
            HourlyBackupEnabled = true,
            HourlyIntervalHours = 2,
            RetentionDailyCount = 21,
            RetentionHourlyCount = 72,
            RetentionSnapshotCount = 7
        });

        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updateEnv = await updateRes.Content.ReadFromJsonAsync<ApiResponse<BackupSettingsDto>>();
        Assert.NotNull(updateEnv?.Data);
        Assert.Equal("02:30", updateEnv.Data.DailyBackupTime);
        Assert.Equal(2, updateEnv.Data.HourlyIntervalHours);
        Assert.Equal(21, updateEnv.Data.RetentionDailyCount);
    }

    [Fact]
    public async Task VerifyBackup_Returns200Report()
    {
        var token = await SetupOwnerAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var createRes = await _client.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto());
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var createEnv = await createRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>();
        Assert.NotNull(createEnv?.Data);

        var verifyRes = await _client.PostAsJsonAsync("/api/v1/backups/verify", new RestoreBackupRequestDto
        {
            FileName = createEnv.Data.FileName
        });

        Assert.Equal(HttpStatusCode.OK, verifyRes.StatusCode);
        var verifyEnv = await verifyRes.Content.ReadFromJsonAsync<ApiResponse<BackupIntegrityReportDto>>();
        Assert.NotNull(verifyEnv?.Data);
        Assert.True(verifyEnv.Data.IsValid, verifyEnv.Data.Details ?? "Verification failed");
        Assert.True(verifyEnv.Data.ManifestValid);
        Assert.True(verifyEnv.Data.ChecksumValid);
        Assert.True(verifyEnv.Data.SqliteIntegrityValid);
    }

    [Fact]
    public async Task EmergencyRecovery_Restore_WithInvalidKey_Returns403()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/recovery/restore", new EmergencyRestoreRequestDto
        {
            FileName = "any_backup.db",
            StoreMasterRecoveryKey = "0000000000000000000000000000000000000000000000000000000000000000"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseMaintenanceMiddleware_WhenStateInactiveOffline_Returns503_AndHealthBypasses()
    {
        var gate = _factory.Services.GetRequiredService<IDatabaseOperationGate>();

        try
        {
            // Transition to InactiveOffline
            gate.TransitionToInactiveOffline();

            // Normal business request returns 503
            var response = await _client.GetAsync("/api/v1/categories");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            // Health check bypasses maintenance gate
            var healthRes = await _client.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);
        }
        finally
        {
            gate.TransitionToAvailable();
        }

        // After restoring Available, normal requests succeed
        var normalRes = await _client.GetAsync("/api/v1/categories");
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, normalRes.StatusCode);
    }
}
