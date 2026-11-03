namespace RetailPOS.Server.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

public sealed class BackupAuthorizationTests
{
    private static async Task<(
        CustomWebApplicationFactory factory,
        HttpClient client,
        string ownerToken,
        string adminToken,
        string managerToken,
        string cashierToken,
        string viewerToken)> SetupRolesAsync()
    {
        var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Setup Store & Owner
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Retail", "backup_owner", "Store Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "backup_owner", "OwnerPass123!"));
        var ownerToken = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 2. Register Terminal for PIN logins
        var termRes = await client.PostAsJsonAsync("/api/v1/terminals", new RegisterTerminalRequest("POS-T1", "Main Terminal"));
        var terminalId = (await termRes.Content.ReadFromJsonAsync<ApiResponse<TerminalDto>>())!.Data!.Id;

        // 3. Create Users with different roles
        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "backup_admin", "Admin User", "AdminPass123!", "2222", RoleType.Admin));

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "backup_manager", "Manager User", "ManagerPass123!", "3333", RoleType.Manager));

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "backup_cashier", "Cashier User", "CashierPass123!", "4444", RoleType.Cashier));

        await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "backup_viewer", "Viewer User", "ViewerPass123!", "5555", RoleType.Viewer));

        // 4. Authenticate each role
        client.DefaultRequestHeaders.Authorization = null;

        var adminLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "backup_admin", "AdminPass123!"));
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        var managerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "backup_manager", "ManagerPass123!"));
        var managerToken = (await managerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        var cashierLogin = await client.PostAsJsonAsync("/api/v1/auth/login-pin", new LoginPinRequest(
            "backup_cashier", "4444", terminalId));
        var cashierToken = (await cashierLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        var viewerLogin = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "backup_viewer", "ViewerPass123!"));
        var viewerToken = (await viewerLogin.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        return (factory, client, ownerToken, adminToken, managerToken, cashierToken, viewerToken);
    }

    private static MultipartFormDataContent CreateDummyUploadContent()
    {
        var content = new MultipartFormDataContent("----Boundary" + Guid.NewGuid().ToString("N"));
        content.Add(new ByteArrayContent(new byte[] { 0x53, 0x51, 0x4c }), "dbFile", "test.db");
        content.Add(new ByteArrayContent(new byte[] { 0x7b, 0x7d }), "manifestFile", "test.db.manifest");
        return content;
    }

    [Fact]
    public async Task UnauthenticatedRequests_ToAllBackupEndpoints_Return401Unauthorized()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;

        var endpoints = new (HttpMethod Method, string Url, Func<HttpContent?> ContentFactory)[]
        {
            (HttpMethod.Get, "/api/v1/backups", () => null),
            (HttpMethod.Post, "/api/v1/backups", () => JsonContent.Create(new CreateBackupRequestDto())),
            (HttpMethod.Get, $"/api/v1/backups/{Guid.NewGuid()}", () => null),
            (HttpMethod.Post, "/api/v1/backups/verify", () => JsonContent.Create(new RestoreBackupRequestDto { FileName = "test.db" })),
            (HttpMethod.Post, "/api/v1/backups/upload", CreateDummyUploadContent),
            (HttpMethod.Post, "/api/v1/backups/restore", () => JsonContent.Create(new RestoreBackupRequestDto { FileName = "test.db" })),
            (HttpMethod.Get, "/api/v1/backups/settings", () => null),
            (HttpMethod.Put, "/api/v1/backups/settings", () => JsonContent.Create(new UpdateBackupSettingsDto())),
            (HttpMethod.Post, "/api/v1/backups/export-dr", () => JsonContent.Create(new ExportDisasterRecoveryRequestDto { StoreMasterRecoveryKey = new string('A', 64) })),
            (HttpMethod.Post, "/api/v1/backups/bare-metal-recovery", () => JsonContent.Create(new BareMetalRecoveryRequestDto()))
        };

        foreach (var (method, url, contentFactory) in endpoints)
        {
            using var request = new HttpRequestMessage(method, url) { Content = contentFactory() };
            using var response = await client.SendAsync(request);

            var raw = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Endpoint {method} {url} returned {response.StatusCode}: {raw}");

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.False(body.Success);
            Assert.Equal("UNAUTHENTICATED", body.ErrorCode);
        }
    }

    [Fact]
    public async Task InvalidBearerToken_Returns401Unauthorized()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid_forged_token_xyz");

        var response = await client.GetAsync("/api/v1/backups");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Equal("UNAUTHENTICATED", body.ErrorCode);
    }

    [Fact]
    public async Task CashierToken_ToAllBackupEndpoints_Returns403Forbidden()
    {
        var (factory, client, _, _, _, cashierToken, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

            var endpoints = new (HttpMethod Method, string Url, Func<HttpContent?> ContentFactory)[]
            {
                (HttpMethod.Get, "/api/v1/backups", () => null),
                (HttpMethod.Post, "/api/v1/backups", () => JsonContent.Create(new CreateBackupRequestDto())),
                (HttpMethod.Get, $"/api/v1/backups/{Guid.NewGuid()}", () => null),
                (HttpMethod.Post, "/api/v1/backups/verify", () => JsonContent.Create(new RestoreBackupRequestDto { FileName = "test.db" })),
                (HttpMethod.Post, "/api/v1/backups/upload", CreateDummyUploadContent),
                (HttpMethod.Post, "/api/v1/backups/restore", () => JsonContent.Create(new RestoreBackupRequestDto { FileName = "test.db" })),
                (HttpMethod.Get, "/api/v1/backups/settings", () => null),
                (HttpMethod.Put, "/api/v1/backups/settings", () => JsonContent.Create(new UpdateBackupSettingsDto())),
                (HttpMethod.Post, "/api/v1/backups/export-dr", () => JsonContent.Create(new ExportDisasterRecoveryRequestDto { StoreMasterRecoveryKey = new string('A', 64) })),
                (HttpMethod.Post, "/api/v1/backups/bare-metal-recovery", () => JsonContent.Create(new BareMetalRecoveryRequestDto()))
            };

            foreach (var (method, url, contentFactory) in endpoints)
            {
                using var request = new HttpRequestMessage(method, url) { Content = contentFactory() };
                using var response = await client.SendAsync(request);

                var raw = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Endpoint {method} {url} returned {response.StatusCode}: {raw}");

                var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
                Assert.NotNull(body);
                Assert.False(body.Success);
                Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
            }
        }
    }

    [Fact]
    public async Task ViewerToken_ToBackupEndpoints_Returns403Forbidden()
    {
        var (factory, client, _, _, _, _, viewerToken) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);

            var resGet = await client.GetAsync("/api/v1/backups");
            Assert.Equal(HttpStatusCode.Forbidden, resGet.StatusCode);

            var resPost = await client.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto());
            Assert.Equal(HttpStatusCode.Forbidden, resPost.StatusCode);

            var resSettings = await client.GetAsync("/api/v1/backups/settings");
            Assert.Equal(HttpStatusCode.Forbidden, resSettings.StatusCode);

            var resUpload = await client.PostAsync("/api/v1/backups/upload", CreateDummyUploadContent());
            Assert.Equal(HttpStatusCode.Forbidden, resUpload.StatusCode);

            var resExportDr = await client.PostAsJsonAsync("/api/v1/backups/export-dr", new ExportDisasterRecoveryRequestDto { StoreMasterRecoveryKey = new string('A', 64) });
            Assert.Equal(HttpStatusCode.Forbidden, resExportDr.StatusCode);

            var resRestore = await client.PostAsJsonAsync("/api/v1/backups/restore", new RestoreBackupRequestDto { FileName = "dummy.db" });
            Assert.Equal(HttpStatusCode.Forbidden, resRestore.StatusCode);

            var resBareMetal = await client.PostAsJsonAsync("/api/v1/backups/bare-metal-recovery", new BareMetalRecoveryRequestDto());
            Assert.Equal(HttpStatusCode.Forbidden, resBareMetal.StatusCode);
        }
    }

    [Fact]
    public async Task ManagerToken_ToBackupEndpoints_Returns403Forbidden()
    {
        var (factory, client, _, _, managerToken, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

            var resGet = await client.GetAsync("/api/v1/backups");
            Assert.Equal(HttpStatusCode.Forbidden, resGet.StatusCode);

            var resPost = await client.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto());
            Assert.Equal(HttpStatusCode.Forbidden, resPost.StatusCode);

            var resSettings = await client.GetAsync("/api/v1/backups/settings");
            Assert.Equal(HttpStatusCode.Forbidden, resSettings.StatusCode);

            var resPutSettings = await client.PutAsJsonAsync("/api/v1/backups/settings", new UpdateBackupSettingsDto());
            Assert.Equal(HttpStatusCode.Forbidden, resPutSettings.StatusCode);

            var resRestore = await client.PostAsJsonAsync("/api/v1/backups/restore", new RestoreBackupRequestDto { FileName = "dummy.db" });
            Assert.Equal(HttpStatusCode.Forbidden, resRestore.StatusCode);

            var resUpload = await client.PostAsync("/api/v1/backups/upload", CreateDummyUploadContent());
            Assert.Equal(HttpStatusCode.Forbidden, resUpload.StatusCode);

            var resExportDr = await client.PostAsJsonAsync("/api/v1/backups/export-dr", new ExportDisasterRecoveryRequestDto { StoreMasterRecoveryKey = new string('A', 64) });
            Assert.Equal(HttpStatusCode.Forbidden, resExportDr.StatusCode);

            var resBareMetal = await client.PostAsJsonAsync("/api/v1/backups/bare-metal-recovery", new BareMetalRecoveryRequestDto());
            Assert.Equal(HttpStatusCode.Forbidden, resBareMetal.StatusCode);
        }
    }

    [Fact]
    public async Task AdminToken_CanCreateBackup_ListHistory_AndManageSettings()
    {
        var (factory, client, _, adminToken, _, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            // 1. Create manual backup as Admin
            var createRes = await client.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto
            {
                Reason = "Admin automated pre-maintenance backup"
            });
            Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

            var createEnv = await createRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>();
            Assert.NotNull(createEnv?.Data);
            Assert.True(createEnv.Success);
            Assert.Equal("backup_admin", createEnv.Data.TriggeredByUserName);
            Assert.NotEqual("SYSTEM", createEnv.Data.TriggeredByUserName);
            Assert.NotEqual("Administrator", createEnv.Data.TriggeredByUserName);

            // 2. List backup history as Admin
            var listRes = await client.GetAsync("/api/v1/backups");
            Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

            var listEnv = await listRes.Content.ReadFromJsonAsync<ApiResponse<List<BackupRecordDto>>>();
            Assert.NotNull(listEnv?.Data);
            Assert.True(listEnv.Success);
            Assert.NotEmpty(listEnv.Data);

            // 3. Get and update backup settings as Admin
            var getSettingsRes = await client.GetAsync("/api/v1/backups/settings");
            Assert.Equal(HttpStatusCode.OK, getSettingsRes.StatusCode);

            var updateSettingsRes = await client.PutAsJsonAsync("/api/v1/backups/settings", new UpdateBackupSettingsDto
            {
                DailyBackupEnabled = true,
                DailyBackupTime = "03:00",
                RetentionDailyCount = 14
            });
            Assert.Equal(HttpStatusCode.OK, updateSettingsRes.StatusCode);

            // 4. Admin authorized for upload (passes auth, reaches file validation rather than 401/403)
            var uploadRes = await client.PostAsync("/api/v1/backups/upload", CreateDummyUploadContent());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, uploadRes.StatusCode);

            // 5. Admin authorized for disaster recovery export (passes auth, reaches parameter validation)
            var exportRes = await client.PostAsJsonAsync("/api/v1/backups/export-dr", new ExportDisasterRecoveryRequestDto
            {
                StoreMasterRecoveryKey = "short_invalid_key"
            });
            Assert.Equal(HttpStatusCode.BadRequest, exportRes.StatusCode);
        }
    }

    [Fact]
    public async Task AdminToken_AttemptingRestore_Returns403Forbidden()
    {
        var (factory, client, _, adminToken, _, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            // Admin is allowed CreateBackup, but Permission.RestoreDatabase is strictly Owner-only
            var restoreRes = await client.PostAsJsonAsync("/api/v1/backups/restore", new RestoreBackupRequestDto
            {
                FileName = "existing_backup.db"
            });

            Assert.Equal(HttpStatusCode.Forbidden, restoreRes.StatusCode);
            var body = await restoreRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.False(body.Success);
            Assert.Equal("INSUFFICIENT_PERMISSIONS", body.ErrorCode);
        }
    }

    [Fact]
    public async Task OwnerToken_CanPerformAllBackupOperations_IncludingRestore()
    {
        var (factory, client, ownerToken, _, _, _, _) = await SetupRolesAsync();
        using (factory)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

            // 1. Create backup as Owner
            var createRes = await client.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto
            {
                Reason = "Owner full system backup"
            });
            Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

            var createEnv = await createRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>();
            Assert.NotNull(createEnv?.Data);
            Assert.Equal("backup_owner", createEnv.Data.TriggeredByUserName);
            Assert.NotEqual("SYSTEM", createEnv.Data.TriggeredByUserName);

            var backupFileName = createEnv.Data.FileName;

            // 2. Verify backup
            var verifyRes = await client.PostAsJsonAsync("/api/v1/backups/verify", new RestoreBackupRequestDto
            {
                FileName = backupFileName
            });
            Assert.Equal(HttpStatusCode.OK, verifyRes.StatusCode);

            // 3. Restore backup as Owner (Owner has Permission.RestoreDatabase)
            var restoreRes = await client.PostAsJsonAsync("/api/v1/backups/restore", new RestoreBackupRequestDto
            {
                FileName = backupFileName
            });
            var restoreRaw = await restoreRes.Content.ReadAsStringAsync();
            Assert.True(restoreRes.StatusCode == HttpStatusCode.OK, $"Restore failed with status {restoreRes.StatusCode} and body: {restoreRaw}");

            var restoreEnv = await restoreRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>();
            Assert.NotNull(restoreEnv?.Data);
            Assert.Equal("Succeeded", restoreEnv.Data.Status);
            Assert.Equal("backup_owner", restoreEnv.Data.TriggeredByUserName);
        }
    }

    [Fact]
    public async Task DirectEndpointCall_BypassingClientUI_IsRejected()
    {
        // Direct caller attempting to bypass client UI by posting raw HTTP without valid auth
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // 1. Raw attempt with no auth
        var res1 = await client.PostAsJsonAsync("/api/v1/backups", new { Reason = "Direct bypass attempt" });
        Assert.Equal(HttpStatusCode.Unauthorized, res1.StatusCode);

        // 2. Raw attempt with basic auth (wrong scheme)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        var res2 = await client.PostAsJsonAsync("/api/v1/backups", new { Reason = "Direct bypass attempt" });
        Assert.Equal(HttpStatusCode.Unauthorized, res2.StatusCode);
    }

    [Fact]
    public async Task UploadRoute_EnforcesMultipartBodyLengthLimit_RejectsOversizedUpload()
    {
        using var smallLimitFactory = new CustomWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ServerConfiguration:MaxRequestBodySizeBytes"] = "1024"
                });
            });
        });

        var client = smallLimitFactory.CreateClient();

        // Setup Owner on this factory
        await client.PostAsJsonAsync("/api/v1/auth/setup-owner", new SetupOwnerRequest(
            "Grozet Retail", "backup_owner", "Store Owner", "OwnerPass123!", "1234"));

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(
            "backup_owner", "OwnerPass123!"));
        var ownerToken = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!.Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 1. Within boundary (small payload: ~200 bytes total)
        // Passes request size limits and reaches file validation (returns 422 UnprocessableEntity, not size error)
        var smallContent = CreateDummyUploadContent();
        var smallRes = await client.PostAsync("/api/v1/backups/upload", smallContent);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, smallRes.StatusCode);

        // 2. Exceeds boundary (4096 bytes of dummy data > 1024 limit)
        var oversizedContent = new MultipartFormDataContent("----Boundary" + Guid.NewGuid().ToString("N"));
        oversizedContent.Add(new ByteArrayContent(new byte[4096]), "dbFile", "oversized.db");
        oversizedContent.Add(new ByteArrayContent(new byte[100]), "manifestFile", "oversized.db.manifest");

        var oversizedRes = await client.PostAsync("/api/v1/backups/upload", oversizedContent);
        // FormOptions MultipartBodyLengthLimit rejects the oversized multipart body with HTTP 400 Bad Request
        Assert.True(oversizedRes.StatusCode == HttpStatusCode.BadRequest ||
                    oversizedRes.StatusCode == HttpStatusCode.InternalServerError,
                    $"Expected rejection due to size limit, got {oversizedRes.StatusCode}");
    }
}
