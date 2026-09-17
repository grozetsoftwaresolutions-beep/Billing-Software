using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetailPOS.Domain.Common;

namespace RetailPOS.Application.Security;

public enum F2RecoveryDecision
{
    AlreadyFinalized,
    NormalIdle,
    StaleCleanup,
    PromotionPendingExecuted,
    PromotionCommittedCleaned,
    RestoredAndSwaps,
    TombstoneAppendedAndCleaned,
    FailedClosed
}

public sealed class F2RecoveryResult
{
    public bool IsSuccess { get; set; }
    public F2RecoveryDecision Decision { get; set; }
    public string ActiveFingerprint { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class F2JournalData
{
    public Guid RotationOperationId { get; set; }
    public string State { get; set; } = string.Empty; // ROTATION_VERIFIED, PROMOTION_PENDING, PROMOTION_COMMITTED
    public string F1Fingerprint { get; set; } = string.Empty;
    public string F2Fingerprint { get; set; } = string.Empty;
    public string TmpSha256 { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class F2PublicationManager
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly string _clientDirectory;
    private readonly string _targetPath;
    private readonly string _backupPath;
    private readonly string _tempPath;
    private readonly string _journalPath;
    private readonly string _tombstonePath;

    public F2PublicationManager(string clientDirectory)
    {
        _clientDirectory = clientDirectory;
        _targetPath = Path.Combine(clientDirectory, "server_identity.json");
        _backupPath = Path.Combine(clientDirectory, "server_identity.json.bak");
        _tempPath = Path.Combine(clientDirectory, "server_identity.json.tmp");
        _journalPath = Path.Combine(clientDirectory, "rotation_tx.json");
        _tombstonePath = Path.Combine(clientDirectory, "consumed_rotations.json");
    }

    public string TargetPath => _targetPath;
    public string BackupPath => _backupPath;
    public string TempPath => _tempPath;
    public string JournalPath => _journalPath;
    public string TombstonePath => _tombstonePath;

    public F2RecoveryResult RecoverAndReconcile(string expectedF1, string expectedF2, Guid rotationOpId)
    {
        bool tombstonePresent = DpapiStorageHelper.IsTombstoneRecorded(_tombstonePath, rotationOpId);

        // Classify Journal
        string? journalState = null;
        bool journalCorrupt = false;
        if (File.Exists(_journalPath))
        {
            try
            {
                string jText = File.ReadAllText(_journalPath);
                var jData = JsonSerializer.Deserialize<F2JournalData>(jText);
                if (jData == null || string.IsNullOrWhiteSpace(jData.State))
                    journalCorrupt = true;
                else
                    journalState = jData.State;
            }
            catch
            {
                journalCorrupt = true;
            }
        }

        // Classify Target
        string targetClass = ClassifyFile(_targetPath, expectedF1, expectedF2);
        string backupClass = ClassifyFile(_backupPath, expectedF1, expectedF2);
        string tempClass = ClassifyFile(_tempPath, expectedF1, expectedF2);

        // Evaluate Ordered Precedence Decision Tree (Priorities 1 to 21)

        // Priority 1: PRESENT, Any, F2, Any, Any
        if (tombstonePresent && targetClass == "F2")
        {
            CleanUpLeftovers();
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.AlreadyFinalized, ActiveFingerprint = expectedF2, ExitCode = 0 };
        }

        // Priority 2: PRESENT, Any, NOT F2
        if (tombstonePresent && targetClass != "F2")
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 117,
                ErrorMessage = "ERROR_TOMBSTONE_WITHOUT_PROMOTED_IDENTITY: Tombstone present but active certificate is not F2."
            };
        }

        // Priority 3: ABSENT, MISSING, F1, Missing, Missing
        if (!tombstonePresent && journalState == null && !journalCorrupt && targetClass == "F1" && backupClass == "Missing" && tempClass == "Missing")
        {
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.NormalIdle, ActiveFingerprint = expectedF1, ExitCode = 0 };
        }

        // Priority 4: ABSENT, MISSING, F1, Present, Present/Missing
        if (!tombstonePresent && journalState == null && !journalCorrupt && targetClass == "F1" && (backupClass != "Missing" || tempClass != "Missing"))
        {
            CleanUpLeftovers();
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.StaleCleanup, ActiveFingerprint = expectedF1, ExitCode = 0 };
        }

        // Priority 5: ABSENT, MISSING, F2
        if (!tombstonePresent && journalState == null && !journalCorrupt && targetClass == "F2")
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 119,
                ErrorMessage = "ERROR_IDENTITY_UNTRACKED_ROTATION: Untracked F2 without journal or tombstone."
            };
        }

        // Priority 6: ABSENT, MISSING, Corrupt/Missing/Ambiguous
        if (!tombstonePresent && journalState == null && !journalCorrupt && (targetClass == "Missing" || targetClass == "Corrupt" || targetClass == "Ambiguous"))
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 115,
                ErrorMessage = "ERROR_IDENTITY_STORE_CORRUPT: Active identity store missing or unreadable."
            };
        }

        // Priority 7: ABSENT, CORRUPT
        if (!tombstonePresent && journalCorrupt)
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 114,
                ErrorMessage = "ERROR_ROTATION_STATE_CORRUPT: Tampered or unreadable journal."
            };
        }

        // Priority 8: ABSENT, ROTATION_VERIFIED, F1, Missing, Valid F2
        if (!tombstonePresent && journalState == "ROTATION_VERIFIED" && targetClass == "F1" && backupClass == "Missing" && tempClass == "F2")
        {
            ExecuteAtomicReplace();
            AdvanceJournal(rotationOpId, "PROMOTION_COMMITTED", expectedF1, expectedF2);
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.PromotionPendingExecuted, ActiveFingerprint = expectedF2, ExitCode = 0 };
        }

        // Priority 10: ABSENT, ROTATION_VERIFIED, NOT F1
        if (!tombstonePresent && journalState == "ROTATION_VERIFIED" && targetClass != "F1")
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 116,
                ErrorMessage = "ERROR_IDENTITY_HASH_AMBIGUOUS: Target hash ambiguous during verified rotation."
            };
        }

        // Priority 11: ABSENT, PROMOTION_PENDING, F1, Missing, Valid F2
        if (!tombstonePresent && journalState == "PROMOTION_PENDING" && targetClass == "F1" && backupClass == "Missing" && tempClass == "F2")
        {
            ExecuteAtomicReplace();
            AdvanceJournal(rotationOpId, "PROMOTION_COMMITTED", expectedF1, expectedF2);
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.PromotionPendingExecuted, ActiveFingerprint = expectedF2, ExitCode = 0 };
        }

        // Priority 12: ABSENT, PROMOTION_PENDING, F2, F1, Any
        if (!tombstonePresent && journalState == "PROMOTION_PENDING" && targetClass == "F2" && backupClass == "F1")
        {
            DpapiStorageHelper.RecordTombstone(_tombstonePath, rotationOpId);
            CleanUpLeftovers();
            if (File.Exists(_journalPath)) File.Delete(_journalPath);
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.PromotionCommittedCleaned, ActiveFingerprint = expectedF2, ExitCode = 0 };
        }

        // Priority 13: ABSENT, PROMOTION_PENDING, Corrupt/Missing, F1, Valid F2
        if (!tombstonePresent && journalState == "PROMOTION_PENDING" && (targetClass == "Missing" || targetClass == "Corrupt") && backupClass == "F1" && tempClass == "F2")
        {
            File.Copy(_backupPath, _targetPath, overwrite: true);
            ExecuteAtomicReplace();
            AdvanceJournal(rotationOpId, "PROMOTION_COMMITTED", expectedF1, expectedF2);
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.RestoredAndSwaps, ActiveFingerprint = expectedF2, ExitCode = 0 };
        }

        // Priority 15: ABSENT, PROMOTION_PENDING, Corrupt/Missing, Corrupt/Missing
        if (!tombstonePresent && journalState == "PROMOTION_PENDING" && (targetClass == "Missing" || targetClass == "Corrupt") && (backupClass == "Missing" || backupClass == "Corrupt"))
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 115,
                ErrorMessage = "ERROR_IDENTITY_STORE_CORRUPT: Both target and backup missing or destroyed."
            };
        }

        // Priority 16: ABSENT, PROMOTION_PENDING, Ambiguous
        if (!tombstonePresent && journalState == "PROMOTION_PENDING" && targetClass == "Ambiguous")
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 116,
                ErrorMessage = "ERROR_IDENTITY_HASH_AMBIGUOUS: Unrecognized identity hash."
            };
        }

        // Priority 17: ABSENT, PROMOTION_PENDING, Any, F2
        if (!tombstonePresent && journalState == "PROMOTION_PENDING" && backupClass == "F2")
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 120,
                ErrorMessage = "ERROR_IDENTITY_BACKUP_AMBIGUOUS: Ambiguous backup containing new cert."
            };
        }

        // Priority 18: ABSENT, PROMOTION_COMMITTED, F2, F1/Missing, Any
        if (!tombstonePresent && journalState == "PROMOTION_COMMITTED" && targetClass == "F2")
        {
            DpapiStorageHelper.RecordTombstone(_tombstonePath, rotationOpId);
            CleanUpLeftovers();
            if (File.Exists(_journalPath)) File.Delete(_journalPath);
            return new F2RecoveryResult { IsSuccess = true, Decision = F2RecoveryDecision.TombstoneAppendedAndCleaned, ActiveFingerprint = expectedF2, ExitCode = 0 };
        }

        // Priority 20: ABSENT, PROMOTION_COMMITTED, NOT F2, NOT Valid F2
        if (!tombstonePresent && journalState == "PROMOTION_COMMITTED" && targetClass != "F2" && tempClass != "F2")
        {
            return new F2RecoveryResult
            {
                IsSuccess = false,
                Decision = F2RecoveryDecision.FailedClosed,
                ExitCode = 118,
                ErrorMessage = "ERROR_COMMITTED_PROMOTION_UNRECOVERABLE: Committed promotion unrecoverable."
            };
        }

        // Priority 21: Catch-all fail-closed
        return new F2RecoveryResult
        {
            IsSuccess = false,
            Decision = F2RecoveryDecision.FailedClosed,
            ExitCode = 121,
            ErrorMessage = "ERROR_IDENTITY_RECOVERY_UNMAPPED: Unmapped physical anomaly outside defined recovery rules."
        };
    }

    private void ExecuteAtomicReplace()
    {
        if (File.Exists(_targetPath))
        {
            File.Replace(_tempPath, _targetPath, _backupPath);
        }
        else
        {
            File.Move(_tempPath, _targetPath);
        }
    }

    private void AdvanceJournal(Guid rotationOpId, string newState, string f1, string f2)
    {
        var data = new F2JournalData
        {
            RotationOperationId = rotationOpId,
            State = newState,
            F1Fingerprint = f1,
            F2Fingerprint = f2,
            CreatedAtUtc = DateTime.UtcNow
        };
        string json = JsonSerializer.Serialize(data, IndentedOptions);
        File.WriteAllText(_journalPath, json);
    }

    private void CleanUpLeftovers()
    {
        if (File.Exists(_backupPath)) File.Delete(_backupPath);
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private static string ClassifyFile(string path, string f1, string f2)
    {
        if (!File.Exists(path)) return "Missing";
        try
        {
            string content = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("PinnedFingerprint", out var elem))
            {
                string? fp = elem.GetString();
                if (string.Equals(fp, f1, StringComparison.OrdinalIgnoreCase)) return "F1";
                if (string.Equals(fp, f2, StringComparison.OrdinalIgnoreCase)) return "F2";
                return "Ambiguous";
            }
            return "Ambiguous";
        }
        catch
        {
            return "Corrupt";
        }
    }
}
