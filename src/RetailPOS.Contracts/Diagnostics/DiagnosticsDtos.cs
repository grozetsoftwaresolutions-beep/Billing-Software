namespace RetailPOS.Contracts.Diagnostics;

using System;
using System.Collections.Generic;
using RetailPOS.Contracts.Backup;

public class GenerateDiagnosticsRequestDto
{
    public string? Reason { get; set; }
}

public class DiagnosticBundleMetadataDto
{
    public Guid OperationId { get; set; }
    public string BundleFileName { get; set; } = string.Empty;
    public string BundleFilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int LogFilesIncluded { get; set; }
    public List<string> IncludedArtifacts { get; set; } = new();
}

public class DiagnosticSystemEnvironmentDto
{
    public string OsVersion { get; set; } = string.Empty;
    public string CpuArchitecture { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long AvailableMemoryBytes { get; set; }
    public long DataDriveFreeSpaceBytes { get; set; }
    public string DotNetRuntimeVersion { get; set; } = string.Empty;
    public TimeSpan SystemUptime { get; set; }
}

public class DiagnosticServerHealthDto
{
    public string Status { get; set; } = string.Empty;
    public bool IsDatabaseHealthy { get; set; }
    public List<string> ActivePortBindings { get; set; } = new();
    public int ProtocolVersion { get; set; }
    public double UptimeSeconds { get; set; }
}

public class DiagnosticDatabaseSummaryDto
{
    public long PageSize { get; set; }
    public long PageCount { get; set; }
    public string JournalMode { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string QuickCheckResult { get; set; } = string.Empty;
}

public class DiagnosticBackupSummaryDto
{
    public int TotalBackupsCount { get; set; }
    public long BackupsDirectorySizeBytes { get; set; }
    public bool DailyBackupEnabled { get; set; }
    public bool HourlyBackupEnabled { get; set; }
    public string? SecondaryStoragePath { get; set; }
    public string? RemovableUsbBackupPath { get; set; }
    public List<BackupRecordDto> RecentBackups { get; set; } = new();
}

public class DiagnosticServiceStatusDto
{
    public string ServiceName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StartupType { get; set; } = string.Empty;
}

public class DiagnosticNetworkInterfaceDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> IpAddresses { get; set; } = new();
    public string? Gateway { get; set; }
    public bool IsDhcpEnabled { get; set; }
}

public class DiagnosticNetworkSummaryDto
{
    public List<DiagnosticNetworkInterfaceDto> Interfaces { get; set; } = new();
    public bool DiscoveryPortOpen { get; set; }
}
