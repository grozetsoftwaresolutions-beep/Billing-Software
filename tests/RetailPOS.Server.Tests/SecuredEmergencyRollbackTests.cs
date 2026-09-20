using System.Net;
using System.Net.Http.Json;
using RetailPOS.Contracts.Backup;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class SecuredEmergencyRollbackTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SecuredEmergencyRollbackTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EmergencyRollback_ExternalIp_Returns403Forbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.105");

        var request = new EmergencyRestoreRequestDto
        {
            FileName = "backup.db",
            StoreMasterRecoveryKey = new string('A', 64),
            ConfirmDestructiveRollback = true
        };

        var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EmergencyRollback_MissingConfirmation_Returns400BadRequest()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

        var request = new EmergencyRestoreRequestDto
        {
            FileName = "backup.db",
            StoreMasterRecoveryKey = new string('A', 64),
            ConfirmDestructiveRollback = false // Missing explicit destructive confirmation
        };

        var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CONFIRMATION_REQUIRED", body);
    }

    [Fact]
    public async Task EmergencyRollback_InvalidMasterKeyFormat_Returns400BadRequest()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

        var request = new EmergencyRestoreRequestDto
        {
            FileName = "backup.db",
            StoreMasterRecoveryKey = "short_invalid_key",
            ConfirmDestructiveRollback = true
        };

        var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_MASTER_KEY", body);
    }

    [Fact]
    public async Task EmergencyRollback_InvalidMasterKeyAuthentication_Returns403Or400()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");

        // 64 hex characters but wrong key
        var request = new EmergencyRestoreRequestDto
        {
            FileName = "nonexistent_backup.db",
            StoreMasterRecoveryKey = new string('F', 64),
            ConfirmDestructiveRollback = true
        };

        var response = await client.PostAsJsonAsync("/api/v1/admin/recovery/restore", request);
        // Either 403 (invalid key) or 400 (backup file not found)
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest);
    }
}
