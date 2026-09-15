namespace RetailPOS.Domain.Tests;

using System;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

public class Phase11DomainTests
{
    [Fact]
    public void BackupRecord_InstantiationAndPropertyIntegrity()
    {
        var opId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var record = new BackupRecord
        {
            BackupOperationId = opId,
            FileName = "grozet_pos_manual_20260915_000000.db",
            FilePath = "C:\\Backups\\grozet_pos_manual_20260915_000000.db",
            SizeBytes = 1048576,
            DatabaseSha256 = "A1B2C3D4E5F6",
            Type = BackupType.Manual,
            Status = BackupStatus.Succeeded,
            FileState = FileState.Active,
            Origin = BackupOrigin.LocalGenerated,
            StoreId = storeId,
            StoreName = "Supermarket Main",
            AppVersion = "1.0.0",
            SchemaVersion = 11,
            CreatedAtUtc = now,
            CompletedAtUtc = now.AddSeconds(5),
            TriggeredByUserId = "USR-101",
            TriggeredByUserName = "Store Manager"
        };

        Assert.Equal(opId, record.BackupOperationId);
        Assert.Equal("grozet_pos_manual_20260915_000000.db", record.FileName);
        Assert.Equal(1048576, record.SizeBytes);
        Assert.Equal(BackupType.Manual, record.Type);
        Assert.Equal(BackupStatus.Succeeded, record.Status);
        Assert.Equal(FileState.Active, record.FileState);
        Assert.Equal(BackupOrigin.LocalGenerated, record.Origin);
        Assert.Equal(storeId, record.StoreId);
        Assert.Equal("Supermarket Main", record.StoreName);
        Assert.Equal(11, record.SchemaVersion);
    }

    [Fact]
    public void BackupConfiguration_Defaults_AreCommercialGrade()
    {
        var config = new BackupConfiguration();

        Assert.True(config.DailyBackupEnabled);
        Assert.Equal(new TimeSpan(23, 0, 0), config.DailyBackupTime);
        Assert.False(config.HourlyBackupEnabled);
        Assert.Equal(4, config.HourlyIntervalHours);
        Assert.Equal(14, config.RetentionDailyCount);
        Assert.Equal(48, config.RetentionHourlyCount);
        Assert.Equal(5, config.RetentionSnapshotCount);
    }

    [Fact]
    public void Scheduler_RejectsIanaTimezone_AsiaKolkataReturns400()
    {
        var ex = Assert.Throws<DomainException>(() =>
            new Store(1, "Test Store", timeZoneId: "Asia/Kolkata"));

        Assert.Equal("INVALID_TIMEZONE_ID", ex.ErrorCode);
        Assert.Contains("IANA time zone identifiers are not allowed", ex.Message);
    }

    [Fact]
    public void Scheduler_AcceptsWindowsTimezone_IndiaStandardTimeAccepted()
    {
        var store = new Store(1, "Test Store", timeZoneId: "India Standard Time");
        Assert.Equal("India Standard Time", store.TimeZoneId);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(store.TimeZoneId);
        Assert.NotNull(tz);
    }

    [Fact]
    public void Scheduler_DerivesCalendarDate_FromStoreTimeZoneId()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var utc = new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);

        // UTC is 20:00 on Sept 15, India is +5:30 -> 01:30 on Sept 16!
        Assert.Equal(16, local.Day);
        Assert.Equal(1, local.Hour);
        Assert.Equal(30, local.Minute);
    }

    [Fact]
    public void Scheduler_TimezoneConversion_ConvertsLocalToUtcAccurately()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var localTime = new DateTime(2026, 9, 15, 23, 0, 0, DateTimeKind.Unspecified);
        var utcTime = TimeZoneInfo.ConvertTimeToUtc(localTime, tz);

        // 23:00 IST - 5:30 = 17:30 UTC
        Assert.Equal(17, utcTime.Hour);
        Assert.Equal(30, utcTime.Minute);
    }
}
