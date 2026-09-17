using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using Xunit;

namespace RetailPOS.Application.Tests;

[SupportedOSPlatform("windows")]
public sealed class Phase14F2RecoveryTests
{
    private readonly F2RecoveryService _service = new();

    [Fact]
    public void TEST_41_F2Recovery_ValidSignedPayload_Accepted()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pinnedSpki = rootKey.ExportSubjectPublicKeyInfo();

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        string candidateF2 = "BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF001122334455";
        string nonce = Guid.NewGuid().ToString("N");
        DateTime validUntil = DateTime.UtcNow.AddMinutes(30);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var (artifact, confirmationCode) = _service.GenerateRecoveryArtifact(
            storeId, terminalId, rotationId, candidateF2, nonce, validUntil, tmk, rootKey);

        Assert.NotNull(artifact);
        Assert.NotNull(confirmationCode);
        Assert.Equal(8 + 1, confirmationCode.Length); // 8 chars + 1 hyphen: XXXX-XXXX

        string promotedF2 = _service.VerifyAndApplyRecoveryArtifact(
            artifact, confirmationCode, storeId, terminalId, tmk, pinnedSpki);

        Assert.Equal(candidateF2, promotedF2);
    }

    [Fact]
    public void TEST_42_F2Recovery_ConfirmationCode_DerivedFromTMK_Matches()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pinnedSpki = rootKey.ExportSubjectPublicKeyInfo();

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        string candidateF2 = "CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF0011223344556677";
        string nonce = "sample-nonce";
        DateTime validUntil = DateTime.UtcNow.AddHours(1);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var (artifact, confirmationCode) = _service.GenerateRecoveryArtifact(
            storeId, terminalId, rotationId, candidateF2, nonce, validUntil, tmk, rootKey);

        // Wrong TMK used on counter must fail confirmation code validation
        byte[] wrongTmk = RandomNumberGenerator.GetBytes(32);
        var ex = Assert.Throws<DomainException>(() => _service.VerifyAndApplyRecoveryArtifact(
            artifact, confirmationCode, storeId, terminalId, wrongTmk, pinnedSpki));

        Assert.Equal("ERROR_CONFIRMATION_CODE_MISMATCH", ex.ErrorCode);
    }

    [Fact]
    public void TEST_43_F2Recovery_OwnerPin_NeverExposedToCounter()
    {
        // Assert IF2RecoveryService interface does NOT accept Owner PIN on Counter verification method
        var verifyMethod = typeof(IF2RecoveryService).GetMethod(nameof(IF2RecoveryService.VerifyAndApplyRecoveryArtifact));
        Assert.NotNull(verifyMethod);

        var paramNames = verifyMethod.GetParameters().Select(p => p.Name).ToList();
        Assert.DoesNotContain("ownerPin", paramNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("pin", paramNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", paramNames, StringComparer.OrdinalIgnoreCase);

        // F2RecoveryArtifactDto also contains no PIN field
        var dtoProps = typeof(F2RecoveryArtifactDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("OwnerPin", dtoProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pin", dtoProps, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TEST_44_F2Recovery_MismatchedStoreOrTerminal_Rejected()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pinnedSpki = rootKey.ExportSubjectPublicKeyInfo();

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var otherStoreId = Guid.NewGuid();
        var otherTerminalId = Guid.NewGuid();
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var (artifact, confirmationCode) = _service.GenerateRecoveryArtifact(
            storeId, terminalId, Guid.NewGuid(), "F2FINGERPRINT", "nonce", DateTime.UtcNow.AddHours(1), tmk, rootKey);

        // Mismatched Store
        var exStore = Assert.Throws<DomainException>(() => _service.VerifyAndApplyRecoveryArtifact(
            artifact, confirmationCode, otherStoreId, terminalId, tmk, pinnedSpki));
        Assert.Equal("ERROR_STORE_MISMATCH", exStore.ErrorCode);

        // Mismatched Terminal
        var exTerm = Assert.Throws<DomainException>(() => _service.VerifyAndApplyRecoveryArtifact(
            artifact, confirmationCode, storeId, otherTerminalId, tmk, pinnedSpki));
        Assert.Equal("ERROR_TERMINAL_MISMATCH", exTerm.ErrorCode);
    }

    [Fact]
    public void TEST_45_F2Recovery_TamperedPayloadOrSignature_Rejected()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pinnedSpki = rootKey.ExportSubjectPublicKeyInfo();

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        string candidateF2 = "BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF001122334455";
        string nonce = "fixed-nonce";
        DateTime validUntil = DateTime.UtcNow.AddMinutes(30);
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        var (artifact, confirmationCode) = _service.GenerateRecoveryArtifact(
            storeId, terminalId, rotationId, candidateF2, nonce, validUntil, tmk, rootKey);

        // 1. DER signature instead of P1363: DER signatures are typically 70-72 bytes with 0x30 prefix
        string canonicalPayload = $"RETAILPOS-ROTATION-v5.2.9.6\n{storeId}\n{terminalId}\n{rotationId}\n{candidateF2}\n{nonce}\n{validUntil.Ticks}";
        byte[] canonicalBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        byte[] derSig = rootKey.SignData(canonicalBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var derArtifact = new F2RecoveryArtifactDto
        {
            StoreId = storeId,
            TerminalId = terminalId,
            RotationOperationId = rotationId,
            CandidateF2Fingerprint = candidateF2,
            Nonce = nonce,
            ValidUntilUtc = validUntil,
            SignatureBase64 = Convert.ToBase64String(derSig)
        };

        var exDer = Assert.Throws<DomainException>(() => _service.VerifyAndApplyRecoveryArtifact(
            derArtifact, confirmationCode, storeId, terminalId, tmk, pinnedSpki));
        Assert.Equal("ERROR_INVALID_SIGNATURE_FORMAT", exDer.ErrorCode);

        // 2. Truncated signature (< 64 bytes)
        byte[] truncatedSig = new byte[32];
        var truncArtifact = new F2RecoveryArtifactDto
        {
            StoreId = storeId,
            TerminalId = terminalId,
            RotationOperationId = rotationId,
            CandidateF2Fingerprint = candidateF2,
            Nonce = nonce,
            ValidUntilUtc = validUntil,
            SignatureBase64 = Convert.ToBase64String(truncatedSig)
        };
        var exTrunc = Assert.Throws<DomainException>(() => _service.VerifyAndApplyRecoveryArtifact(
            truncArtifact, confirmationCode, storeId, terminalId, tmk, pinnedSpki));
        Assert.Equal("ERROR_INVALID_SIGNATURE_FORMAT", exTrunc.ErrorCode);

        // 3. Tampered payload: modify fingerprint
        var tamperedArtifact = new F2RecoveryArtifactDto
        {
            StoreId = storeId,
            TerminalId = terminalId,
            RotationOperationId = rotationId,
            CandidateF2Fingerprint = "TAMPERED_FINGERPRINT",
            Nonce = nonce,
            ValidUntilUtc = validUntil,
            SignatureBase64 = artifact.SignatureBase64
        };
        var exTamper = Assert.Throws<DomainException>(() => _service.VerifyAndApplyRecoveryArtifact(
            tamperedArtifact, confirmationCode, storeId, terminalId, tmk, pinnedSpki));
        Assert.Equal("ERROR_INVALID_SIGNATURE", exTamper.ErrorCode);
    }

    [Fact]
    public void TEST_46_F2_Recovery_TargetF1_BackupMissing_TempValid_PromotionPending_Recovers()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "f2_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manager = new F2PublicationManager(tempDir);
            string f1 = "1111111111111111111111111111111111111111111111111111111111111111";
            string f2 = "2222222222222222222222222222222222222222222222222222222222222222";
            Guid opId = Guid.NewGuid();

            File.WriteAllText(manager.TargetPath, $"{{\"PinnedFingerprint\": \"{f1}\"}}");
            File.WriteAllText(manager.TempPath, $"{{\"PinnedFingerprint\": \"{f2}\"}}");
            File.WriteAllText(manager.JournalPath, JsonSerializer.Serialize(new F2JournalData
            {
                RotationOperationId = opId,
                State = "PROMOTION_PENDING",
                F1Fingerprint = f1,
                F2Fingerprint = f2
            }));

            var result = manager.RecoverAndReconcile(f1, f2, opId);
            Assert.True(result.IsSuccess);
            Assert.Equal(F2RecoveryDecision.PromotionPendingExecuted, result.Decision);
            Assert.Equal(f2, result.ActiveFingerprint);
            Assert.Contains(f2, File.ReadAllText(manager.TargetPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TEST_47_F2_Recovery_TargetF2_BackupF1_PromotionPending_Finalizes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "f2_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manager = new F2PublicationManager(tempDir);
            string f1 = "1111111111111111111111111111111111111111111111111111111111111111";
            string f2 = "2222222222222222222222222222222222222222222222222222222222222222";
            Guid opId = Guid.NewGuid();

            File.WriteAllText(manager.TargetPath, $"{{\"PinnedFingerprint\": \"{f2}\"}}");
            File.WriteAllText(manager.BackupPath, $"{{\"PinnedFingerprint\": \"{f1}\"}}");
            File.WriteAllText(manager.JournalPath, JsonSerializer.Serialize(new F2JournalData
            {
                RotationOperationId = opId,
                State = "PROMOTION_PENDING",
                F1Fingerprint = f1,
                F2Fingerprint = f2
            }));

            var result = manager.RecoverAndReconcile(f1, f2, opId);
            Assert.True(result.IsSuccess);
            Assert.Equal(F2RecoveryDecision.PromotionCommittedCleaned, result.Decision);
            Assert.False(File.Exists(manager.JournalPath));
            Assert.False(File.Exists(manager.BackupPath));
            Assert.True(DpapiStorageHelper.IsTombstoneRecorded(manager.TombstonePath, opId));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TEST_48_F2_Recovery_TargetMissing_BackupF1_PromotionPending_RestoresAndSwaps()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "f2_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manager = new F2PublicationManager(tempDir);
            string f1 = "1111111111111111111111111111111111111111111111111111111111111111";
            string f2 = "2222222222222222222222222222222222222222222222222222222222222222";
            Guid opId = Guid.NewGuid();

            // Target missing, backup F1, temp F2, journal PROMOTION_PENDING
            File.WriteAllText(manager.BackupPath, $"{{\"PinnedFingerprint\": \"{f1}\"}}");
            File.WriteAllText(manager.TempPath, $"{{\"PinnedFingerprint\": \"{f2}\"}}");
            File.WriteAllText(manager.JournalPath, JsonSerializer.Serialize(new F2JournalData
            {
                RotationOperationId = opId,
                State = "PROMOTION_PENDING",
                F1Fingerprint = f1,
                F2Fingerprint = f2
            }));

            var result = manager.RecoverAndReconcile(f1, f2, opId);
            Assert.True(result.IsSuccess);
            Assert.Equal(F2RecoveryDecision.RestoredAndSwaps, result.Decision);
            Assert.Contains(f2, File.ReadAllText(manager.TargetPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TEST_49_F2_Recovery_TargetF2_BackupF1_PromotionCommitted_AppendsTombstoneAndCleans()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "f2_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manager = new F2PublicationManager(tempDir);
            string f1 = "1111111111111111111111111111111111111111111111111111111111111111";
            string f2 = "2222222222222222222222222222222222222222222222222222222222222222";
            Guid opId = Guid.NewGuid();

            File.WriteAllText(manager.TargetPath, $"{{\"PinnedFingerprint\": \"{f2}\"}}");
            File.WriteAllText(manager.BackupPath, $"{{\"PinnedFingerprint\": \"{f1}\"}}");
            File.WriteAllText(manager.JournalPath, JsonSerializer.Serialize(new F2JournalData
            {
                RotationOperationId = opId,
                State = "PROMOTION_COMMITTED",
                F1Fingerprint = f1,
                F2Fingerprint = f2
            }));

            var result = manager.RecoverAndReconcile(f1, f2, opId);
            Assert.True(result.IsSuccess);
            Assert.Equal(F2RecoveryDecision.TombstoneAppendedAndCleaned, result.Decision);
            Assert.False(File.Exists(manager.JournalPath));
            Assert.False(File.Exists(manager.BackupPath));
            Assert.True(DpapiStorageHelper.IsTombstoneRecorded(manager.TombstonePath, opId));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TEST_50_F2_Recovery_TombstonePresent_TargetNotF2_FailsClosed()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "f2_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manager = new F2PublicationManager(tempDir);
            string f1 = "1111111111111111111111111111111111111111111111111111111111111111";
            string f2 = "2222222222222222222222222222222222222222222222222222222222222222";
            Guid opId = Guid.NewGuid();

            // Record tombstone, but target is F1 (NOT F2)
            DpapiStorageHelper.RecordTombstone(manager.TombstonePath, opId);
            File.WriteAllText(manager.TargetPath, $"{{\"PinnedFingerprint\": \"{f1}\"}}");

            var result = manager.RecoverAndReconcile(f1, f2, opId);
            Assert.False(result.IsSuccess);
            Assert.Equal(F2RecoveryDecision.FailedClosed, result.Decision);
            Assert.Equal(117, result.ExitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TEST_51_F2_JournalState_Consistency_NoOrphanedJournalOrBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "f2_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manager = new F2PublicationManager(tempDir);
            string f1 = "1111111111111111111111111111111111111111111111111111111111111111";
            string f2 = "2222222222222222222222222222222222222222222222222222222222222222";
            Guid opId = Guid.NewGuid();

            // Already finalized state (tombstone present, target F2, leftover backup and temp)
            DpapiStorageHelper.RecordTombstone(manager.TombstonePath, opId);
            File.WriteAllText(manager.TargetPath, $"{{\"PinnedFingerprint\": \"{f2}\"}}");
            File.WriteAllText(manager.BackupPath, $"{{\"PinnedFingerprint\": \"{f1}\"}}");
            File.WriteAllText(manager.TempPath, $"{{\"PinnedFingerprint\": \"{f2}\"}}");

            var result = manager.RecoverAndReconcile(f1, f2, opId);
            Assert.True(result.IsSuccess);
            Assert.Equal(F2RecoveryDecision.AlreadyFinalized, result.Decision);

            // Invariant: zero orphaned .bak, .tmp, or journal files on disk
            Assert.False(File.Exists(manager.BackupPath));
            Assert.False(File.Exists(manager.TempPath));
            Assert.False(File.Exists(manager.JournalPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TEST_52_OfflineRotation_ExpiredOrReplayedArtifact_Fails()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pinnedSpki = rootKey.ExportSubjectPublicKeyInfo();

        var storeId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        string candidateF2 = "CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF0011223344556677";
        string nonce = "replayed-nonce";
        byte[] tmk = RandomNumberGenerator.GetBytes(32);

        // 1. Expired artifact (validUntil in the past)
        DateTime expiredTime = DateTime.UtcNow.AddMinutes(-10);
        var (expiredArtifact, expiredCode) = _service.GenerateRecoveryArtifact(
            storeId, terminalId, rotationId, candidateF2, nonce, expiredTime, tmk, rootKey);

        var exExpired = Assert.Throws<DomainException>(() => _service.VerifyAndApplyRecoveryArtifact(
            expiredArtifact, expiredCode, storeId, terminalId, tmk, pinnedSpki));
        Assert.Equal("ERROR_RECOVERY_EXPIRED", exExpired.ErrorCode);

        // 2. Replay of already-consumed RotationOpId
        string tempDir = Path.Combine(Path.GetTempPath(), "f2_replay_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            string tombstoneFile = Path.Combine(tempDir, "consumed_rotations.json");
            DpapiStorageHelper.RecordTombstone(tombstoneFile, rotationId);

            // Valid active artifact
            var (validArtifact, validCode) = _service.GenerateRecoveryArtifact(
                storeId, terminalId, rotationId, candidateF2, nonce, DateTime.UtcNow.AddHours(1), tmk, rootKey);

            // Verification of signature passes, but tombstone check rejects reuse
            bool isReplayed = DpapiStorageHelper.IsTombstoneRecorded(tombstoneFile, validArtifact.RotationOperationId);
            Assert.True(isReplayed, "Already-consumed RotationOpId must be detected in tombstone store.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
