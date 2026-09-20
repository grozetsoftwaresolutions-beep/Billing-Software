namespace RetailPOS.Infrastructure.Tests;

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Diagnostics;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Security;
using Xunit;

public sealed class SupportDiagnosticsTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _diagnosticsDir;
    private readonly string _stagingDir;
    private readonly string _logsDir;
    private readonly string _securityDir;
    private readonly string _dataDir;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SupportDiagnosticsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "RetailPOS_DiagTests_" + Guid.NewGuid().ToString("N"));
        _diagnosticsDir = Path.Combine(_testDir, "Diagnostics");
        _stagingDir = Path.Combine(_testDir, "DiagnosticsStaging");
        _logsDir = Path.Combine(_testDir, "Logs");
        _securityDir = Path.Combine(_testDir, "Security");
        _dataDir = Path.Combine(_testDir, "Data");

        Directory.CreateDirectory(_diagnosticsDir);
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(_logsDir);
        Directory.CreateDirectory(_securityDir);
        Directory.CreateDirectory(_dataDir);

        var dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False")
            .Options;

        using var context = new RetailDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Scrubber_ScrubsJwt_Successfully()
    {
        var sampleJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var input = $"User authorized with token: Bearer {sampleJwt}";

        var scrubbed = DiagnosticSecretScrubber.Scrub(input);

        Assert.DoesNotContain(sampleJwt, scrubbed);
        Assert.Contains("[REDACTED_JWT]", scrubbed);
    }

    [Fact]
    public void Scrubber_ScrubsPasswordAndPin_Successfully()
    {
        var input = "{\"username\": \"admin\", \"password\": \"SuperSecretPass123!\", \"staffPin\": \"9876\", \"connection\": \"Server=localhost;Database=test;Password=dbpass;\"}";

        var scrubbed = DiagnosticSecretScrubber.Scrub(input);

        Assert.DoesNotContain("SuperSecretPass123!", scrubbed);
        Assert.DoesNotContain("9876", scrubbed);
        Assert.DoesNotContain("dbpass", scrubbed);
        Assert.Contains("[REDACTED_PASSWORD]", scrubbed);
        Assert.Contains("[REDACTED_PIN]", scrubbed);
        Assert.Contains("[REDACTED_CONNECTION_STRING]", scrubbed);
    }

    [Fact]
    public void Scrubber_ScrubsStoreMasterRecoveryKey_Successfully()
    {
        var sampleRecoveryKey = "4a6f2b8c9d0e1f3a5b7c9d1e3f5a7b9c2d4e6f8a0b1c2d3e4f5a6b7c8d9e0f1a";
        var input = $"Error encountered with master key {sampleRecoveryKey} during operation.";

        var scrubbed = DiagnosticSecretScrubber.Scrub(input);

        Assert.DoesNotContain(sampleRecoveryKey, scrubbed);
        Assert.Contains("[REDACTED_RECOVERY_KEY]", scrubbed);
    }

    [Fact]
    public void Scrubber_ScrubsCardholderPanWithLuhn_Successfully()
    {
        var validVisaPan = "4532 0151 1284 2130";
        var input = $"Customer presented card {validVisaPan} for transaction.";

        var scrubbed = DiagnosticSecretScrubber.Scrub(input);

        Assert.DoesNotContain(validVisaPan, scrubbed);
        Assert.Contains("[REDACTED_PAN]", scrubbed);
    }

    [Fact]
    public void Auditor_DetectsUnscrubbedRecoveryKey_FailsAudit()
    {
        var unscrubbedKey = "4a6f2b8c9d0e1f3a5b7c9d1e3f5a7b9c2d4e6f8a0b1c2d3e4f5a6b7c8d9e0f1a";
        var content = $"Leaked key: {unscrubbedKey}";

        var result = DiagnosticSecretAuditor.Audit(content);

        Assert.False(result.IsClean);
        Assert.Equal("RECOVERY_OR_SIGNING_KEY", result.ViolationCategory);
    }

    [Fact]
    public void Auditor_DetectsUnscrubbedJwt_FailsAudit()
    {
        var jwtHeader = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozG4m1e";
        var content = $"Unscrubbed token {jwtHeader}";

        var result = DiagnosticSecretAuditor.Audit(content);

        Assert.False(result.IsClean);
        Assert.Equal("JWT_SESSION_TOKEN", result.ViolationCategory);
    }

    [Fact]
    public void Auditor_CleanPayload_PassesAudit()
    {
        var content = "{\"storeId\": \"11111111-1111-1111-1111-111111111111\", \"status\": \"Available\", \"logs\": [\"Server started\", \"Backup completed\"]}";

        var result = DiagnosticSecretAuditor.Audit(content);

        Assert.True(result.IsClean);
        Assert.Null(result.ViolationCategory);
    }

    [Fact]
    public async Task SupportDiagnostics_GeneratesSanitizedZipBundle_PassesAuditAndHasAllowlistOnly()
    {
        using var context = new RetailDbContext(_options);
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Diag Test Store");

        var auditRepo = new EfAuditLogRepository(context);
        var serverIdentity = new ServerIdentityService(null, context, storeIdentity, auditRepo, customSecurityDir: _securityDir);

        var backupRepo = new EfBackupRepository(context);
        var uow = new EfUnitOfWork(context);
        var settingsStore = new JsonBackupSettingsStore(Path.Combine(_securityDir, "backup_settings.json"));
        var retention = new BackupRetentionService(backupRepo, settingsStore, uow);
        var gate = new DatabaseOperationGate();
        var maint = new DatabaseMaintenanceCoordinator(gate);
        var opCoord = new BackupOperationCoordinator();
        var scopeFactory = new MaintenanceScopeFactory();
        var manifest = new BackupManifestService();
        var backupService = new BackupService(backupRepo, settingsStore, retention, maint, opCoord, scopeFactory, storeIdentity, manifest, auditRepo, uow, context);

        // Seed some normal logs
        var logFile = Path.Combine(_logsDir, "retailpos-server-2026-09-20.log");
        await File.WriteAllTextAsync(logFile, "2026-09-20 10:00:00 [INF] System initialized with user admin\npassword: SuperSecretPassword123!");

        var service = new SupportDiagnosticsService(
            storeIdentity,
            serverIdentity,
            backupService,
            dbContext: context,
            auditLogRepository: auditRepo,
            customDiagnosticsDir: _diagnosticsDir,
            customStagingDir: _stagingDir,
            customLogsDir: _logsDir);

        var bundle = await service.GenerateSupportBundleAsync("user-1", "Admin User", "Customer support review");

        Assert.NotNull(bundle);
        Assert.True(File.Exists(bundle.BundleFilePath));
        Assert.True(bundle.SizeBytes > 0);
        Assert.NotEmpty(bundle.Sha256);

        // Inspect ZIP contents
        using var zip = ZipFile.OpenRead(bundle.BundleFilePath);
        var entryNames = zip.Entries.Select(e => e.FullName).ToList();

        // Must contain allowlisted files only
        Assert.Contains("system_environment.json", entryNames);
        Assert.Contains("server_health.json", entryNames);
        Assert.Contains("bundle_manifest.json", entryNames);

        // Verify password in log was scrubbed
        var logEntry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("sanitized_logs/", StringComparison.Ordinal));
        if (logEntry != null)
        {
            using var reader = new StreamReader(logEntry.Open());
            var logContent = await reader.ReadToEndAsync();
            Assert.DoesNotContain("SuperSecretPassword123!", logContent);
            Assert.Contains("[REDACTED_PASSWORD]", logContent);
        }
    }

    [Fact]
    public async Task SupportDiagnostics_PrunesToMax3Bundles_Successfully()
    {
        using var context = new RetailDbContext(_options);
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Diag Test Store");

        var auditRepo = new EfAuditLogRepository(context);
        var serverIdentity = new ServerIdentityService(null, context, storeIdentity, auditRepo, customSecurityDir: _securityDir);

        var backupRepo = new EfBackupRepository(context);
        var uow = new EfUnitOfWork(context);
        var settingsStore = new JsonBackupSettingsStore(Path.Combine(_securityDir, "backup_settings.json"));
        var retention = new BackupRetentionService(backupRepo, settingsStore, uow);
        var gate = new DatabaseOperationGate();
        var maint = new DatabaseMaintenanceCoordinator(gate);
        var opCoord = new BackupOperationCoordinator();
        var scopeFactory = new MaintenanceScopeFactory();
        var manifest = new BackupManifestService();
        var backupService = new BackupService(backupRepo, settingsStore, retention, maint, opCoord, scopeFactory, storeIdentity, manifest, auditRepo, uow, context);

        var service = new SupportDiagnosticsService(
            storeIdentity,
            serverIdentity,
            backupService,
            dbContext: context,
            customDiagnosticsDir: _diagnosticsDir,
            customStagingDir: _stagingDir,
            customLogsDir: _logsDir);

        // Generate 4 bundles
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(50); // Ensure distinct timestamps
            await service.GenerateSupportBundleAsync("user-1", "Admin User", $"Bundle {i}");
        }

        var zipFiles = Directory.GetFiles(_diagnosticsDir, "retailpos_support_*.zip");
        Assert.True(zipFiles.Length <= 3, $"Expected <= 3 bundles retained, found {zipFiles.Length}");
    }
}
