using System;

namespace RetailPOS.Contracts.Deployment;

public sealed class MigrationJournalEntry
{
    public int Sequence { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string PreviousHmac { get; set; } = string.Empty;
    public string EntryHmac { get; set; } = string.Empty;
}

public sealed class MigrationMarker
{
    public string State { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string PreMigrationBackupPath { get; set; } = string.Empty;
    public string PreMigrationBackupSha256 { get; set; } = string.Empty;
    public string PreMigrationIdentityBackupPath { get; set; } = string.Empty;
    public string PreMigrationIdentityBackupSha256 { get; set; } = string.Empty;
    public string PreUpgradeVerifierHash { get; set; } = string.Empty;
    public int TargetSchemaVersion { get; set; }
    public int CurrentStep { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MigrationResult
{
    public bool IsSuccess { get; set; }
    public string CurrentState { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int ExitCode { get; set; }
}

public sealed class RollbackResult
{
    public bool RollbackPerformed { get; set; }
    public bool IsSuccess { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int ExitCode { get; set; }
}
