using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Audit;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class Phase13AuditPersistenceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RetailDbContext> _dbOptions;

    public Phase13AuditPersistenceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"phase13_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "test.db");

        _connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new RetailDbContext(_dbOptions);
        db.Database.EnsureCreated();
        DatabaseInitializer.InitializeDatabaseAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task AuditLog_EFCore_UpdateAttempt_ThrowsInvalidOperationException()
    {
        using var db = new RetailDbContext(_dbOptions);
        var audit = new AuditLog(0, "TEST_ACTION", "TestEntity", "1", Guid.NewGuid(), Guid.NewGuid(), "Details", true);
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync();

        // Attempt to update Details via EF Core ChangeTracker
        db.Entry(audit).State = EntityState.Modified;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditLog_EFCore_DeleteAttempt_ThrowsInvalidOperationException()
    {
        using var db = new RetailDbContext(_dbOptions);
        var audit = new AuditLog(0, "TEST_ACTION", "TestEntity", "2", Guid.NewGuid(), Guid.NewGuid(), "Details", true);
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync();

        db.AuditLogs.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void AuditLog_SqliteTrigger_DirectUpdate_Aborts()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_logs (Action, EntityName, Success, CreatedAtUtc, UpdatedAtUtc) VALUES ('DIRECT_TEST', 'Entity', 1, '2026-09-16 00:00:00', '2026-09-16 00:00:00');";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "UPDATE audit_logs SET Details = 'Hacked' WHERE Action = 'DIRECT_TEST';";
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditLog_SqliteTrigger_DirectDelete_Aborts()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_logs (Action, EntityName, Success, CreatedAtUtc, UpdatedAtUtc) VALUES ('DELETE_TEST', 'Entity', 1, '2026-09-16 00:00:00', '2026-09-16 00:00:00');";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM audit_logs WHERE Action = 'DELETE_TEST';";
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditLog_InsertAfterRestore_PassesWithoutTriggerConflict()
    {
        using var db = new RetailDbContext(_dbOptions);
        var audit = new AuditLog(0, "POST_RESTORE_ACTION", "Entity", "3", Guid.NewGuid(), Guid.NewGuid(), "Details", true);
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync();

        Assert.True(audit.Id > 0);
    }

    [Fact]
    public async Task AuditLog_FailsafeClear_PersistsFailsafeInstanceIdAndClearOperationId()
    {
        using var db = new RetailDbContext(_dbOptions);
        var failsafeId = Guid.NewGuid();
        var clearOpId = Guid.NewGuid();
        var sha = "abc123def456";

        var audit = new AuditLog(
            0, "SECURITY_SETTINGS_FAILSAFE_CLEARED", "SecuritySettings", clearOpId.ToString(),
            Guid.NewGuid(), Guid.Empty, "Failsafe cleared", true,
            failsafeInstanceId: failsafeId, clearOperationId: clearOpId, targetSettingsSha256: sha);

        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync();

        var repo = new EfAuditLogRepository(db);
        var retrieved = await repo.GetFailsafeClearAuditAsync(failsafeId, clearOpId);

        Assert.NotNull(retrieved);
        Assert.Equal(failsafeId, retrieved.FailsafeInstanceId);
        Assert.Equal(clearOpId, retrieved.ClearOperationId);
        Assert.Equal(sha, retrieved.TargetSettingsSha256);
    }

    [Fact]
    public async Task AuditLog_FailsafeClear_DuplicateClearOperation_IsRejectedOrIdempotent()
    {
        using var db1 = new RetailDbContext(_dbOptions);
        var failsafeId = Guid.NewGuid();
        var clearOpId = Guid.NewGuid();

        var audit1 = new AuditLog(
            0, "SECURITY_SETTINGS_FAILSAFE_CLEARED", "SecuritySettings", clearOpId.ToString(),
            Guid.NewGuid(), Guid.Empty, "First commit", true,
            failsafeInstanceId: failsafeId, clearOperationId: clearOpId);

        db1.AuditLogs.Add(audit1);
        await db1.SaveChangesAsync();

        using var db2 = new RetailDbContext(_dbOptions);
        var audit2 = new AuditLog(
            0, "SECURITY_SETTINGS_FAILSAFE_CLEARED", "SecuritySettings", clearOpId.ToString(),
            Guid.NewGuid(), Guid.Empty, "Duplicate commit", true,
            failsafeInstanceId: failsafeId, clearOperationId: clearOpId);

        db2.AuditLogs.Add(audit2);
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public void AuditSnapshot_Projections_NeverContainPasswordOrPinHashes()
    {
        var snapshot = new UserAuditSnapshot(
            Guid.NewGuid(), "cashier1", "Cashier One", RoleType.Cashier, true);

        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("salt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"username\": \"test\", \"password\": \"secret123\"}", "\"password\": \"[REDACTED]\"")]
    [InlineData("{\"user\": \"a\", \"pin\": \"1234\"}", "\"pin\": \"[REDACTED]\"")]
    [InlineData("{\"token\": \"tok-xyz\", \"key\": \"secret-key\"}", "\"token\": \"[REDACTED]\"")]
    public void AuditJsonSanitizer_ProhibitedKeys_AreRedacted(string rawJson, string expectedSubstring)
    {
        var sanitized = AuditJsonSanitizer.Sanitize(rawJson);
        Assert.NotNull(sanitized);
        Assert.Contains(expectedSubstring, sanitized);
    }

    [Fact]
    public async Task SecuritySettingsStore_FileReplace_AtomicPublicationSucceeds()
    {
        var storeDir = Path.Combine(_testDir, "store1");
        using var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));

        await store.InitializeAsync();
        Assert.False(store.IsInFailsafeMode);

        var newSettings = new SecuritySettings(IdleTimeoutMinutes: 30, MaxFailedPinAttempts: 4);
        await store.UpdateSettingsAsync(newSettings, Guid.NewGuid(), "owner");

        var loaded = await store.GetSettingsAsync();
        Assert.Equal(30, loaded.IdleTimeoutMinutes);
        Assert.Equal(4, loaded.MaxFailedPinAttempts);
    }

    [Fact]
    public async Task SecuritySettingsStore_CorruptFile_EntersFailsafeSafeMode()
    {
        var storeDir = Path.Combine(_testDir, "store2");
        Directory.CreateDirectory(storeDir);
        var settingsFile = Path.Combine(storeDir, "security_settings.json");
        await File.WriteAllTextAsync(settingsFile, "{ corrupted json !@# ");

        using var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));
        await store.InitializeAsync();

        Assert.True(store.IsInFailsafeMode);
        Assert.NotNull(store.CurrentFailsafeMarker);
        Assert.Equal("CORRUPTED_SETTINGS_FILE", store.CurrentFailsafeMarker.EntryReason);
        Assert.True(File.Exists(store.MarkerFilePath));
    }

    [Fact]
    public async Task SecuritySettingsStore_CorruptFileWithDbOffline_RemainsInFailsafe()
    {
        var storeDir = Path.Combine(_testDir, "store3");
        Directory.CreateDirectory(storeDir);
        var settingsFile = Path.Combine(storeDir, "security_settings.json");
        await File.WriteAllTextAsync(settingsFile, "{ corrupted ");

        using var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));
        store.SetDatabaseOffline();
        await store.InitializeAsync();

        Assert.True(store.IsInFailsafeMode);
        Assert.NotNull(store.CurrentFailsafeMarker);
    }

    [Fact]
    public async Task SecuritySettingsMarker_ImmutableIdentity_NeverChangesAcrossRecovery()
    {
        var storeDir = Path.Combine(_testDir, "store4");
        using var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));

        store.EnterFailsafeMode("TEST_EPISODE");
        var initialMarker = store.CurrentFailsafeMarker!;
        var originalInstanceId = initialMarker.FailsafeInstanceId;
        var originalTimestamp = initialMarker.EntryTimestampUtc;
        var originalReason = initialMarker.EntryReason;

        // Simulate failed/crashed clear operation
        store.OnBeforeFileReplace = () => throw new InvalidOperationException("Simulated crash");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ClearFailsafeAsync(new SecuritySettings(), Guid.NewGuid(), "owner"));

        // Restart store
        using var restartedStore = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));
        await restartedStore.InitializeAsync();

        Assert.True(restartedStore.IsInFailsafeMode);
        var postRecoveryMarker = restartedStore.CurrentFailsafeMarker!;

        Assert.Equal(originalInstanceId, postRecoveryMarker.FailsafeInstanceId);
        Assert.Equal(originalTimestamp, postRecoveryMarker.EntryTimestampUtc);
        Assert.Equal(originalReason, postRecoveryMarker.EntryReason);
    }

    [Fact]
    public async Task SecuritySettingsMarker_MutablePendingClear_UpdatesCrashSafely()
    {
        var storeDir = Path.Combine(_testDir, "store5");
        using var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));
        store.EnterFailsafeMode("TEST_MUTABLE");

        var clearOpId = Guid.NewGuid();
        store.OnBeforeFileReplace = () => throw new InvalidOperationException("Crash before replace");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ClearFailsafeAsync(new SecuritySettings(), Guid.NewGuid(), "owner", clearOpId));

        var markerJson = await File.ReadAllTextAsync(store.MarkerFilePath);
        var marker = JsonSerializer.Deserialize<FailsafeActiveMarker>(markerJson);

        Assert.NotNull(marker);
        Assert.NotNull(marker.PendingClear);
        Assert.Equal(clearOpId, marker.PendingClear.ClearOperationId);
    }

    [Fact]
    public async Task SecuritySettingsMarker_CorruptOrPartialWrite_RemainsFailsafe()
    {
        var storeDir = Path.Combine(_testDir, "store6");
        Directory.CreateDirectory(storeDir);
        var markerPath = Path.Combine(storeDir, "security_failsafe.active");
        await File.WriteAllTextAsync(markerPath, "{ partial marker write... ");

        using var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));
        await store.InitializeAsync();

        Assert.True(store.IsInFailsafeMode);
    }

    [Fact]
    public async Task FailsafeRecovery_NewOperationCannotOverwriteCommittedClearOperation()
    {
        var storeDir = Path.Combine(_testDir, "store7");
        using var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions));
        store.EnterFailsafeMode("TEST_OVERWRITE");

        var clearOp1 = Guid.NewGuid();
        store.OnBeforeFileReplace = () => throw new InvalidOperationException("Crash after commit before replace");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ClearFailsafeAsync(new SecuritySettings(), Guid.NewGuid(), "owner", clearOp1));

        // Attempting another clear operation with a different operation ID must be rejected
        var clearOp2 = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ClearFailsafeAsync(new SecuritySettings(), Guid.NewGuid(), "owner", clearOp2));
    }

    [Fact]
    public async Task SecuritySettingsStore_CrashAfterAuditBeforePublication_RemainsFailsafe()
    {
        // State B: Crash immediately after audit commit
        var storeDir = Path.Combine(_testDir, "store8");
        using (var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions)))
        {
            store.EnterFailsafeMode("STATE_B_TEST");
            store.OnBeforeFileReplace = () => throw new InvalidOperationException("Crash after audit commit");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ClearFailsafeAsync(new SecuritySettings(IdleTimeoutMinutes: 45), Guid.NewGuid(), "owner"));
        }

        // Restart store: State B reconciliation discards staged, resets pending clear, remains in failsafe
        using (var restarted = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions)))
        {
            await restarted.InitializeAsync();
            Assert.True(restarted.IsInFailsafeMode);
            Assert.NotNull(restarted.CurrentFailsafeMarker);
            Assert.Null(restarted.CurrentFailsafeMarker.PendingClear);
        }
    }

    [Fact]
    public async Task SecuritySettingsStore_CrashAfterPublicationBeforeMarkerRemoval_ReconcilesSafely()
    {
        // State C: Crash after File.Replace, before marker delete
        var storeDir = Path.Combine(_testDir, "store9");
        var targetSettings = new SecuritySettings(IdleTimeoutMinutes: 50);

        using (var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions)))
        {
            store.EnterFailsafeMode("STATE_C_TEST");
            store.OnBeforeMarkerDelete = () => throw new InvalidOperationException("Crash before marker delete");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ClearFailsafeAsync(targetSettings, Guid.NewGuid(), "owner"));
        }

        // Restart store: State C reconciliation deletes marker, enters normal mode with NO new audit
        using (var restarted = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions)))
        {
            await restarted.InitializeAsync();
            Assert.False(restarted.IsInFailsafeMode);
            Assert.Null(restarted.CurrentFailsafeMarker);
            var settings = await restarted.GetSettingsAsync();
            Assert.Equal(50, settings.IdleTimeoutMinutes);
        }
    }

    [Fact]
    public async Task SecuritySettingsStore_FailsafeClear_IsIdempotentAfterCrash()
    {
        var storeDir = Path.Combine(_testDir, "store10");
        var clearOpId = Guid.NewGuid();
        var targetSettings = new SecuritySettings(IdleTimeoutMinutes: 25);

        using (var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions)))
        {
            store.EnterFailsafeMode("IDEMPOTENT_TEST");
            store.OnBeforeFileReplace = () => throw new InvalidOperationException("Crash");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ClearFailsafeAsync(targetSettings, Guid.NewGuid(), "owner", clearOpId));
        }

        // Second call with same clearOpId completes publication without duplicating database rows
        using (var store2 = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions)))
        {
            await store2.InitializeAsync();
            await store2.ClearFailsafeAsync(targetSettings, Guid.NewGuid(), "owner", clearOpId);
            Assert.False(store2.IsInFailsafeMode);
        }

        using var db = new RetailDbContext(_dbOptions);
        var auditRows = await db.AuditLogs
            .Where(a => a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED" && a.ClearOperationId == clearOpId)
            .ToListAsync();

        Assert.Single(auditRows);
    }

    [Fact]
    public async Task SecuritySettingsStore_RestartWithMarkerAndMismatchedClearAudit_RemainsFailsafe()
    {
        // State D: Matching FailsafeInstanceId and ClearOperationId committed in audit log,
        // BUT TargetSettingsSha256 does NOT match marker's target hash.
        var storeDir = Path.Combine(_testDir, "store_state_d");
        Directory.CreateDirectory(storeDir);

        var failsafeId = Guid.NewGuid();
        var clearOpId = Guid.NewGuid();
        var stagedFile = Path.Combine(storeDir, $"security_settings.json.staged.{clearOpId:N}");
        await File.WriteAllTextAsync(stagedFile, "{}");

        var expectedSha = "TARGET_SHA256_ON_MARKER_AAABBBCCC111222333";
        var mismatchedShaInDb = "MISMATCHED_SHA256_IN_AUDIT_XXX999ZZZ888777";

        var pendingOp = new PendingClearOperation(clearOpId, DateTime.UtcNow, Guid.NewGuid(), "owner", expectedSha, stagedFile);
        var marker = new FailsafeActiveMarker(failsafeId, DateTime.UtcNow, "STATE_D_EPISODE", pendingOp);

        var markerPath = Path.Combine(storeDir, "security_failsafe.active");
        var markerJson = JsonSerializer.Serialize(marker);
        await File.WriteAllTextAsync(markerPath, markerJson);

        // Commit audit log with matching FailsafeInstanceId & ClearOperationId, but mismatched SHA256
        await using (var db = new RetailDbContext(_dbOptions))
        {
            var audit = new AuditLog(
                0,
                "SECURITY_SETTINGS_FAILSAFE_CLEARED",
                "SecuritySettings",
                clearOpId.ToString(),
                Guid.NewGuid(),
                Guid.Empty,
                "Test audit with mismatched hash",
                success: true,
                userName: "owner",
                reason: "Mismatched hash test",
                failsafeInstanceId: failsafeId,
                clearOperationId: clearOpId,
                targetSettingsSha256: mismatchedShaInDb);

            db.AuditLogs.Add(audit);
            await db.SaveChangesAsync();
        }

        int initialClearAuditCount;
        await using (var db = new RetailDbContext(_dbOptions))
        {
            initialClearAuditCount = await db.AuditLogs.CountAsync(a => a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED");
        }

        // Initialize store: should detect State D (audit mismatch) and remain in Failsafe Mode
        using (var store = new SecuritySettingsStore(storeDir, dbContextFactory: () => new RetailDbContext(_dbOptions)))
        {
            await store.InitializeAsync();

            // Verification:
            // 1. Failsafe Mode remains active
            Assert.True(store.IsInFailsafeMode);

            // 2. Marker is NOT cleared / remains active on disk and in memory
            Assert.NotNull(store.CurrentFailsafeMarker);
            Assert.Equal(failsafeId, store.CurrentFailsafeMarker.FailsafeInstanceId);
            Assert.True(File.Exists(markerPath));

            // 3. Normal mode is NOT entered
            Assert.True(store.IsInFailsafeMode);
        }

        // 4. Verify no duplicate audit row was created
        // 5. Existing valid audit history remains unchanged
        await using (var db = new RetailDbContext(_dbOptions))
        {
            var finalClearAuditCount = await db.AuditLogs.CountAsync(a => a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED");
            Assert.Equal(initialClearAuditCount, finalClearAuditCount);

            var persisted = await db.AuditLogs.FirstOrDefaultAsync(a =>
                a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED" &&
                a.FailsafeInstanceId == failsafeId &&
                a.ClearOperationId == clearOpId);

            Assert.NotNull(persisted);
            Assert.Equal(mismatchedShaInDb, persisted.TargetSettingsSha256);
        }
    }

    [Fact]
    public void SecuritySettings_Validation_RejectsInvalidRangesAndNonFiniteValues()
    {
        Assert.Throws<DomainException>(() => new SecuritySettings(IdleTimeoutMinutes: 0).Validate());
        Assert.Throws<DomainException>(() => new SecuritySettings(IdleTimeoutMinutes: 2000).Validate());
        Assert.Throws<DomainException>(() => new SecuritySettings(MaxFailedPinAttempts: 2).Validate());
        Assert.Throws<DomainException>(() => new SecuritySettings(MaxFailedPinAttempts: 15).Validate());
        Assert.Throws<DomainException>(() => new SecuritySettings(PinLockoutMinutes: 0).Validate());
        Assert.Throws<DomainException>(() => new SecuritySettings(MaxCashierDiscountPercentage: -1).Validate());
        Assert.Throws<DomainException>(() => new SecuritySettings(MaxCashierDiscountPercentage: 101).Validate());

        // Valid settings do not throw
        var valid = new SecuritySettings(15, 5, 15, 10.00m);
        valid.Validate();
    }
}
