namespace RetailPOS.Infrastructure.Tests;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Persistence.Interceptors;
using RetailPOS.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class Phase11BackupServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataDir;
    private readonly string _backupsDir;
    private readonly string _quarantineDir;
    private readonly string _securityDir;
    private readonly string _configDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public Phase11BackupServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"grozet_p11_svc_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_testDir, "Data");
        _backupsDir = Path.Combine(_testDir, "Backups");
        _quarantineDir = Path.Combine(_testDir, "Quarantine");
        _securityDir = Path.Combine(_testDir, "Security");
        _configDir = Path.Combine(_testDir, "Config");

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_backupsDir);
        Directory.CreateDirectory(_quarantineDir);
        Directory.CreateDirectory(_securityDir);
        Directory.CreateDirectory(_configDir);

        _dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        using var context = new RetailDbContext(_options);
        context.Database.EnsureCreated();
        SqliteConnectionConfigurator.ApplyPragmas(context);
        DatabaseInitializer.InitializeDatabaseAsync(context).GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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

    private static void CreateValidSqliteFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS dummy (id INT PRIMARY KEY); INSERT OR REPLACE INTO dummy VALUES (1);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private (BackupService Service, StoreIdentityService StoreIdentity, BackupManifestService ManifestService, RetailDbContext Context) CreateTestEnvironment()
    {
        var context = new RetailDbContext(_options);
        var backupRepo = new EfBackupRepository(context);
        var unitOfWork = new EfUnitOfWork(context);
        var auditRepo = new EfAuditLogRepository(context);

        var gate = new DatabaseOperationGate();
        var maintenanceCoord = new DatabaseMaintenanceCoordinator(gate);
        var opCoord = new BackupOperationCoordinator();
        var scopeFactory = new MaintenanceScopeFactory();

        var storeIdentity = new StoreIdentityService(_securityDir);
        storeIdentity.InitializeOrLoadAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Supermarket Central").GetAwaiter().GetResult();

        var manifestService = new BackupManifestService();
        var settingsPath = Path.Combine(_configDir, "backup_settings.json");
        var settingsStore = new JsonBackupSettingsStore(settingsPath);
        var retentionService = new BackupRetentionService(backupRepo, settingsStore, unitOfWork);

        var service = new BackupService(
            backupRepo,
            settingsStore,
            retentionService,
            maintenanceCoord,
            opCoord,
            scopeFactory,
            storeIdentity,
            manifestService,
            auditRepo,
            unitOfWork,
            context,
            customDataDir: _dataDir,
            customQuarantineDir: _quarantineDir,
            customRecoveryMarkerPath: Path.Combine(_testDir, "restore_state.json"));

        return (service, storeIdentity, manifestService, context);
    }

    [Fact]
    public async Task BackupCreation_Manual_ExecutesFullPipeline()
    {
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var record = await service.CreateBackupAsync(
                BackupType.Manual,
                "USER-1",
                "Manager Alice",
                "Routine pre-upgrade backup");

            Assert.NotNull(record);
            Assert.Equal("Manual", record.Type);
            Assert.Equal("Succeeded", record.Status);
            Assert.True(File.Exists(record.FilePath));
            Assert.True(File.Exists($"{record.FilePath}.manifest"));

            // Check manifest signature
            var manifest = await manifestService.LoadManifestAsync($"{record.FilePath}.manifest");
            Assert.NotNull(manifest);
            Assert.True(manifestService.VerifySignature(manifest, storeIdentity.GetStoreSigningKey()));
        }
    }

    [Fact]
    public async Task BackupCreation_ScheduledDaily_ExecutesFullPipeline()
    {
        var (service, _, _, context) = CreateTestEnvironment();
        using (context)
        {
            var record = await service.CreateBackupAsync(
                BackupType.ScheduledDaily,
                "SYSTEM_SCHEDULER",
                "Daily Cron Service");

            Assert.NotNull(record);
            Assert.Equal("ScheduledDaily", record.Type);
            Assert.Equal("Succeeded", record.Status);
            Assert.True(File.Exists(record.FilePath));
        }
    }

    [Fact]
    public async Task BackupCreation_ScheduledHourly_ExecutesFullPipeline()
    {
        var (service, _, _, context) = CreateTestEnvironment();
        using (context)
        {
            var record = await service.CreateBackupAsync(
                BackupType.ScheduledHourly,
                "SYSTEM_SCHEDULER",
                "Hourly Cron Service");

            Assert.NotNull(record);
            Assert.Equal("ScheduledHourly", record.Type);
            Assert.Equal("Succeeded", record.Status);
            Assert.True(File.Exists(record.FilePath));
        }
    }

    [Fact]
    public async Task UploadIngestion_SafeBasename_PreservesExactSignedFileName()
    {
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var tempDb = Path.Combine(_testDir, "test_upload.db");
            CreateValidSqliteFile(tempDb);

            var opId = Guid.NewGuid();
            var manifest = await manifestService.GenerateManifestAsync(
                tempDb,
                opId,
                BackupType.Manual,
                storeIdentity.StoreId,
                storeIdentity.StoreName,
                storeIdentity.GetStoreSigningKey());

            manifest.FileName = "grozet_pos_uploaded_exact_name.db";
            var canonical = manifestService.CreateCanonicalString(manifest);
            manifest.Signature = manifestService.ComputeSignature(canonical, storeIdentity.GetStoreSigningKey());

            using var dbStream = File.OpenRead(tempDb);
            var manifestJson = JsonSerializer.Serialize(manifest);
            using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));

            var result = await service.IngestExternalBackupAsync(
                dbStream,
                "grozet_pos_uploaded_exact_name.db",
                manifestStream,
                "grozet_pos_uploaded_exact_name.db.manifest",
                "U1",
                "Admin");

            Assert.Equal("grozet_pos_uploaded_exact_name.db", result.FileName);
            Assert.Equal("UploadedExternal", result.Origin);
            Assert.True(File.Exists(result.FilePath));
        }
    }

    [Fact]
    public async Task UploadIngestion_PathTraversal_DotDotRejectedWith400()
    {
        var (service, _, _, context) = CreateTestEnvironment();
        using (context)
        {
            using var dbStream = new MemoryStream(new byte[] { 1, 2, 3 });
            using var mStream = new MemoryStream(new byte[] { 1, 2, 3 });

            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                service.IngestExternalBackupAsync(
                    dbStream,
                    "../../sneaky.db",
                    mStream,
                    "manifest.json",
                    "U1",
                    "Admin"));

            Assert.Equal("INVALID_FILENAME", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task UploadIngestion_MissingManifest_RejectedWith422()
    {
        var (service, _, _, context) = CreateTestEnvironment();
        using (context)
        {
            using var dbStream = new MemoryStream(new byte[] { 1, 2, 3 });
            using var mStream = new MemoryStream(Encoding.UTF8.GetBytes(""));

            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                service.IngestExternalBackupAsync(
                    dbStream,
                    "valid_name.db",
                    mStream,
                    "valid_name.db.manifest",
                    "U1",
                    "Admin"));

            Assert.Equal("INVALID_MANIFEST", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task UploadIngestion_InvalidHmacSignature_RejectedWith422()
    {
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var tempDb = Path.Combine(_testDir, "test_hmac_fail.db");
            CreateValidSqliteFile(tempDb);

            var manifest = await manifestService.GenerateManifestAsync(
                tempDb,
                Guid.NewGuid(),
                BackupType.Manual,
                storeIdentity.StoreId,
                storeIdentity.StoreName,
                storeIdentity.GetStoreSigningKey());

            manifest.FileName = "grozet_pos_bad_sig.db";
            manifest.Signature = "DEADBEEF00000000000000000000000000000000000000000000000000000000";

            using var dbStream = File.OpenRead(tempDb);
            using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));

            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                service.IngestExternalBackupAsync(
                    dbStream,
                    "grozet_pos_bad_sig.db",
                    manifestStream,
                    "grozet_pos_bad_sig.db.manifest",
                    "U1",
                    "Admin"));

            Assert.Equal("INVALID_SIGNATURE", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task UploadIngestion_StoreIdMismatch_RejectedWith422()
    {
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var tempDb = Path.Combine(_testDir, "test_store_mismatch.db");
            CreateValidSqliteFile(tempDb);

            var foreignStoreId = Guid.NewGuid();
            var manifest = await manifestService.GenerateManifestAsync(
                tempDb,
                Guid.NewGuid(),
                BackupType.Manual,
                foreignStoreId,
                "Foreign Store",
                storeIdentity.GetStoreSigningKey());

            manifest.FileName = "grozet_pos_foreign.db";
            var canonical = manifestService.CreateCanonicalString(manifest);
            manifest.Signature = manifestService.ComputeSignature(canonical, storeIdentity.GetStoreSigningKey());

            using var dbStream = File.OpenRead(tempDb);
            using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));

            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                service.IngestExternalBackupAsync(
                    dbStream,
                    "grozet_pos_foreign.db",
                    manifestStream,
                    "grozet_pos_foreign.db.manifest",
                    "U1",
                    "Admin"));

            Assert.Equal("STORE_ID_MISMATCH", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task UploadIngestion_ChecksumMismatch_RejectedWith422()
    {
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var tempDb = Path.Combine(_testDir, "test_sha_mismatch.db");
            CreateValidSqliteFile(tempDb);

            var manifest = await manifestService.GenerateManifestAsync(
                tempDb,
                Guid.NewGuid(),
                BackupType.Manual,
                storeIdentity.StoreId,
                storeIdentity.StoreName,
                storeIdentity.GetStoreSigningKey());

            manifest.FileName = "grozet_pos_bad_sha.db";
            manifest.DatabaseSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
            var canonical = manifestService.CreateCanonicalString(manifest);
            manifest.Signature = manifestService.ComputeSignature(canonical, storeIdentity.GetStoreSigningKey());

            using var dbStream = File.OpenRead(tempDb);
            using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));

            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                service.IngestExternalBackupAsync(
                    dbStream,
                    "grozet_pos_bad_sha.db",
                    manifestStream,
                    "grozet_pos_bad_sha.db.manifest",
                    "U1",
                    "Admin"));

            Assert.Equal("CHECKSUM_MISMATCH", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task UploadIngestion_Collision_DifferentSha_RejectsWith409()
    {
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var existingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrozetPOS", "Backups", "grozet_pos_collide.db");
            CreateValidSqliteFile(existingPath);

            var tempDb = Path.Combine(_testDir, "incoming_different.db");
            CreateValidSqliteFile(tempDb);
            File.AppendAllText(tempDb, "different bytes");

            var manifest = await manifestService.GenerateManifestAsync(
                tempDb,
                Guid.NewGuid(),
                BackupType.Manual,
                storeIdentity.StoreId,
                storeIdentity.StoreName,
                storeIdentity.GetStoreSigningKey());

            manifest.FileName = "grozet_pos_collide.db";
            var canonical = manifestService.CreateCanonicalString(manifest);
            manifest.Signature = manifestService.ComputeSignature(canonical, storeIdentity.GetStoreSigningKey());

            using var dbStream = File.OpenRead(tempDb);
            using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));

            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                service.IngestExternalBackupAsync(
                    dbStream,
                    "grozet_pos_collide.db",
                    manifestStream,
                    "grozet_pos_collide.db.manifest",
                    "U1",
                    "Admin"));

            Assert.Equal("BACKUP_FILENAME_COLLISION", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task UploadIngestion_Collision_SameSha_ExistingPairValid_ReturnsIdempotentSuccess()
    {
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var tempDb = Path.Combine(_testDir, "test_idempotent.db");
            CreateValidSqliteFile(tempDb);

            var opId = Guid.NewGuid();
            var createdAt = DateTime.UtcNow;
            var manifest = await manifestService.GenerateManifestAsync(
                tempDb,
                opId,
                BackupType.Manual,
                storeIdentity.StoreId,
                storeIdentity.StoreName,
                storeIdentity.GetStoreSigningKey());

            manifest.FileName = "grozet_pos_idempotent.db";
            manifest.CreatedAtUtc = createdAt;
            var canonical = manifestService.CreateCanonicalString(manifest);
            manifest.Signature = manifestService.ComputeSignature(canonical, storeIdentity.GetStoreSigningKey());

            // First upload: creates it
            using (var db1 = File.OpenRead(tempDb))
            using (var m1 = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest))))
            {
                var r1 = await service.IngestExternalBackupAsync(db1, "grozet_pos_idempotent.db", m1, "grozet_pos_idempotent.db.manifest", "U1", "Admin");
                Assert.Equal("grozet_pos_idempotent.db", r1.FileName);
            }

            // Second upload of identical content and identity: should return idempotent success (200 OK)
            using (var db2 = File.OpenRead(tempDb))
            using (var m2 = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest))))
            {
                var r2 = await service.IngestExternalBackupAsync(db2, "grozet_pos_idempotent.db", m2, "grozet_pos_idempotent.db.manifest", "U1", "Admin");
                Assert.Equal(opId, r2.BackupOperationId);
                Assert.Equal("grozet_pos_idempotent.db", r2.FileName);
            }
        }
    }

    [Fact]
    public async Task UploadIngestion_Collision_SameSha_ValidExistingPair_DifferentIdentity_PreservesExistingAndRejectsWith409()
    {
        // TARGETED CORRECTION 2 TEST:
        // Same SHA-256 + valid existing pair + different immutable identity (e.g. different BackupOperationId / CreatedAtUtc)
        // -> Collision rejection with 409, preserving existing valid backup untouched!
        var (service, storeIdentity, manifestService, context) = CreateTestEnvironment();
        using (context)
        {
            var tempDb = Path.Combine(_testDir, "test_diff_id.db");
            CreateValidSqliteFile(tempDb);

            var existingOpId = Guid.NewGuid();
            var existingCreatedAt = DateTime.UtcNow.AddHours(-2);
            var existingManifest = await manifestService.GenerateManifestAsync(
                tempDb,
                existingOpId,
                BackupType.Manual,
                storeIdentity.StoreId,
                storeIdentity.StoreName,
                storeIdentity.GetStoreSigningKey());

            existingManifest.FileName = "grozet_pos_diff_identity.db";
            existingManifest.CreatedAtUtc = existingCreatedAt;
            var canonical1 = manifestService.CreateCanonicalString(existingManifest);
            existingManifest.Signature = manifestService.ComputeSignature(canonical1, storeIdentity.GetStoreSigningKey());

            // First upload creates existing authoritative pair
            using (var db1 = File.OpenRead(tempDb))
            using (var m1 = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(existingManifest))))
            {
                await service.IngestExternalBackupAsync(db1, "grozet_pos_diff_identity.db", m1, "grozet_pos_diff_identity.db.manifest", "U1", "Admin");
            }

            var authoritativeDbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrozetPOS", "Backups", "grozet_pos_diff_identity.db");
            Assert.True(File.Exists(authoritativeDbPath));
            var originalCreationTime = File.GetLastWriteTimeUtc(authoritativeDbPath);

            // Now, create incoming upload with SAME SHA-256 and valid signature, but DIFFERENT BackupOperationId!
            var incomingOpId = Guid.NewGuid();
            var incomingManifest = await manifestService.GenerateManifestAsync(
                tempDb,
                incomingOpId,
                BackupType.Manual,
                storeIdentity.StoreId,
                storeIdentity.StoreName,
                storeIdentity.GetStoreSigningKey());

            incomingManifest.FileName = "grozet_pos_diff_identity.db";
            incomingManifest.CreatedAtUtc = DateTime.UtcNow; // Different timestamp and operation ID!
            var canonical2 = manifestService.CreateCanonicalString(incomingManifest);
            incomingManifest.Signature = manifestService.ComputeSignature(canonical2, storeIdentity.GetStoreSigningKey());

            // ACT & ASSERT: Must throw BACKUP_IDENTITY_COLLISION (409)
            using (var db2 = File.OpenRead(tempDb))
            using (var m2 = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(incomingManifest))))
            {
                var ex = await Assert.ThrowsAsync<DomainException>(() =>
                    service.IngestExternalBackupAsync(db2, "grozet_pos_diff_identity.db", m2, "grozet_pos_diff_identity.db.manifest", "U1", "Admin"));

                Assert.Equal("BACKUP_IDENTITY_COLLISION", ex.ErrorCode);
            }

            // CRITICAL INVARIANT: Existing backup pair is strictly preserved untouched on disk!
            Assert.True(File.Exists(authoritativeDbPath));
            var loadedManifest = await manifestService.LoadManifestAsync($"{authoritativeDbPath}.manifest");
            Assert.NotNull(loadedManifest);
            Assert.Equal(existingOpId, loadedManifest.BackupOperationId); // Still existing ID!
            Assert.NotEqual(incomingOpId, loadedManifest.BackupOperationId);
        }
    }

    [Fact]
    public async Task AuditLog_DatabaseRestored_DatabaseEnforcedUniqueness_PreventsDuplicateInserts()
    {
        using var context = new RetailDbContext(_options);

        var audit1 = new AuditLog(0, "DATABASE_RESTORED", "BackupRecord", "test_backup.db", null, null, "Restore 1", true);
        await context.AuditLogs.AddAsync(audit1);
        await context.SaveChangesAsync();

        var audit2 = new AuditLog(0, "DATABASE_RESTORED", "BackupRecord", "test_backup.db", null, null, "Restore 2 duplicate", true);
        await context.AuditLogs.AddAsync(audit2);

        // Database-level partial unique index IX_audit_logs_database_restored must reject duplicate!
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task DatabaseMaintenanceInterceptor_BlocksDirectSqlCommands_WhenMaintenanceActive()
    {
        var gate = new DatabaseOperationGate();
        var scope = new MaintenanceScopeFactory();
        var interceptor = new DatabaseMaintenanceInterceptor(gate, scope);

        var options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .AddInterceptors(interceptor)
            .Options;

        using var testContext = new RetailDbContext(options);

        // Normal state: Allowed
        Assert.Equal(DatabaseMaintenanceState.Available, gate.CurrentState);
        var count = await testContext.Stores.CountAsync();
        Assert.True(count >= 0);

        // Transition to InactiveOffline
        gate.TransitionToInactiveOffline();

        // Direct command without maintenance scope: BLOCKED!
        var ex = await Assert.ThrowsAsync<DomainException>(() => testContext.Stores.CountAsync());
        Assert.Equal("DATABASE_MAINTENANCE_BLOCKED", ex.ErrorCode);

        // Inside internal maintenance scope: ALLOWED!
        using (scope.CreateMaintenanceScope())
        {
            var countInScope = await testContext.Stores.CountAsync();
            Assert.True(countInScope >= 0);
        }
    }

    [Fact]
    public async Task BackupCreation_BackgroundRetention_UsesIndependentScope_DoesNotThrowObjectDisposedException()
    {
        // Arrange DI with Scoped RetailDbContext, Repositories, RetentionService, and BackupService
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<RetailDbContext>(opts =>
            opts.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        services.AddScoped<IBackupRepository, EfBackupRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditLogRepository, EfAuditLogRepository>();
        services.AddSingleton<IDatabaseOperationGate, DatabaseOperationGate>();
        services.AddSingleton<IDatabaseMaintenanceCoordinator, DatabaseMaintenanceCoordinator>();
        services.AddSingleton<IBackupOperationCoordinator, BackupOperationCoordinator>();
        services.AddSingleton<IMaintenanceScopeFactory, MaintenanceScopeFactory>();

        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Supermarket Central");
        services.AddSingleton<IStoreIdentityService>(storeIdentity);

        var manifestService = new BackupManifestService();
        services.AddSingleton<IBackupManifestService>(manifestService);

        var settingsPath = Path.Combine(_configDir, "backup_settings_lifetime.json");
        var settingsStore = new JsonBackupSettingsStore(settingsPath);
        await settingsStore.SaveSettingsAsync(new BackupConfiguration
        {
            BackupStoragePath = _backupsDir,
            DailyBackupEnabled = true,
            RetentionDailyCount = 1
        });
        services.AddSingleton<IBackupSettingsStore>(settingsStore);

        // Spy retention service to capture the context instance and observe any exception
        var pruneTaskCompletion = new TaskCompletionSource<bool>();
        RetailDbContext? retentionContextUsed = null;
        Exception? retentionException = null;

        services.AddScoped<BackupRetentionService>();
        services.AddScoped<IBackupRetentionService>(sp =>
        {
            var inner = sp.GetRequiredService<BackupRetentionService>();
            var db = sp.GetRequiredService<RetailDbContext>();
            return new RetentionSpyService(inner, db, (usedContext, ex) =>
            {
                retentionContextUsed = usedContext;
                retentionException = ex;
                if (ex != null)
                {
                    pruneTaskCompletion.TrySetException(ex);
                }
                else
                {
                    pruneTaskCompletion.TrySetResult(true);
                }
            });
        });

        services.AddScoped<IBackupService>(sp =>
        {
            return new BackupService(
                sp.GetRequiredService<IBackupRepository>(),
                sp.GetRequiredService<IBackupSettingsStore>(),
                sp.GetRequiredService<IBackupRetentionService>(),
                sp.GetRequiredService<IDatabaseMaintenanceCoordinator>(),
                sp.GetRequiredService<IBackupOperationCoordinator>(),
                sp.GetRequiredService<IMaintenanceScopeFactory>(),
                sp.GetRequiredService<IStoreIdentityService>(),
                sp.GetRequiredService<IBackupManifestService>(),
                sp.GetRequiredService<IAuditLogRepository>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<RetailDbContext>(),
                serviceScopeFactory: sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                customDataDir: _dataDir,
                customQuarantineDir: _quarantineDir,
                customRecoveryMarkerPath: Path.Combine(_testDir, "restore_state.json"));
        });

        using var rootProvider = services.BuildServiceProvider();

        RetailDbContext? requestDbContext = null;
        using (var requestScope = rootProvider.CreateScope())
        {
            requestDbContext = requestScope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var backupService = requestScope.ServiceProvider.GetRequiredService<IBackupService>();

            // Execute backup creation within request scope
            var record = await backupService.CreateBackupAsync(
                BackupType.ScheduledDaily,
                "SYSTEM_SCHEDULER",
                "Scheduler Service",
                "Daily snapshot");

            Assert.NotNull(record);
        } // requestScope disposed here: requestDbContext is disposed!

        // Verify requestDbContext is disposed
        Assert.NotNull(requestDbContext);
        Assert.Throws<ObjectDisposedException>(() => requestDbContext.Backups.Count());

        // Wait for background retention to complete
        var completed = await Task.WhenAny(pruneTaskCompletion.Task, Task.Delay(5000));
        Assert.Same(pruneTaskCompletion.Task, completed);
        await pruneTaskCompletion.Task;

        // Assert: Retention completed without error
        Assert.Null(retentionException);

        // Assert: Retention executed with a fresh, non-disposed context, NOT the requestDbContext
        Assert.NotNull(retentionContextUsed);
        Assert.NotSame(requestDbContext, retentionContextUsed);

        // Assert: Database record state is valid and verifiable via a fresh scope
        using (var verifyScope = rootProvider.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var count = await verifyDb.Backups.CountAsync();
            Assert.True(count >= 1);
        }
    }

    private sealed class RetentionSpyService : IBackupRetentionService
    {
        private readonly IBackupRetentionService _inner;
        private readonly RetailDbContext _dbContext;
        private readonly Action<RetailDbContext, Exception?> _callback;

        public RetentionSpyService(
            IBackupRetentionService inner,
            RetailDbContext dbContext,
            Action<RetailDbContext, Exception?> callback)
        {
            _inner = inner;
            _dbContext = dbContext;
            _callback = callback;
        }

        public async Task PruneAsync(CancellationToken ct = default)
        {
            try
            {
                await _inner.PruneAsync(ct);
                _callback(_dbContext, null);
            }
            catch (Exception ex)
            {
                _callback(_dbContext, ex);
                throw;
            }
        }
    }
}
