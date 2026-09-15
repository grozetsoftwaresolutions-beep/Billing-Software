namespace RetailPOS.Domain.ValueObjects;

using System;

public class BackupConfiguration
{
    public bool DailyBackupEnabled { get; set; } = true;
    public TimeSpan DailyBackupTime { get; set; } = new TimeSpan(23, 0, 0);
    public bool HourlyBackupEnabled { get; set; }
    public int HourlyIntervalHours { get; set; } = 4;
    public int RetentionDailyCount { get; set; } = 14;
    public int RetentionHourlyCount { get; set; } = 48;
    public int RetentionSnapshotCount { get; set; } = 5;
    public string BackupStoragePath { get; set; } = string.Empty;
}
