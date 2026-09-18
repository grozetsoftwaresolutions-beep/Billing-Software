using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Security;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Infrastructure.Security;

public sealed class SecuritySettingsStore : ISecuritySettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _configDir;
    private readonly string _settingsFilePath;
    private readonly string _markerFilePath;
    private readonly IAuditLogRepository? _auditRepo;
    private readonly Func<RetailDbContext>? _dbContextFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private volatile bool _isInFailsafeMode;
    private volatile bool _isDatabaseOffline;
    private volatile bool _failsafeAudited;
    private FailsafeActiveMarker? _currentMarker;
    private SecuritySettings _currentSettings = new();
    private bool _disposed;

    // Test fault-injection hooks
    public Action? OnBeforeAuditCommit { get; set; }
    public Action? OnBeforeFileReplace { get; set; }
    public Action? OnBeforeMarkerDelete { get; set; }
    public Action? OnBeforeMemoryUpdate { get; set; }
    public bool SimulateFileReplaceFailure { get; set; }
    public bool SimulateMarkerWriteFailure { get; set; }

    public bool IsInFailsafeMode => _isInFailsafeMode;
    public FailsafeActiveMarker? CurrentFailsafeMarker => _currentMarker;
    public string ConfigDirectory => _configDir;
    public string SettingsFilePath => _settingsFilePath;
    public string MarkerFilePath => _markerFilePath;

    public SecuritySettingsStore(
        string? customConfigDir = null,
        IAuditLogRepository? auditRepo = null,
        Func<RetailDbContext>? dbContextFactory = null)
    {
        _auditRepo = auditRepo;
        _dbContextFactory = dbContextFactory;

        if (!string.IsNullOrWhiteSpace(customConfigDir))
        {
            _configDir = customConfigDir;
        }
        else
        {
            _configDir = RetailPOSPaths.GetDefaultSecurityDirectory();
        }

        Directory.CreateDirectory(_configDir);
        _settingsFilePath = Path.Combine(_configDir, "security_settings.json");
        _markerFilePath = Path.Combine(_configDir, "security_failsafe.active");
    }

    public void SetDatabaseOffline() => _isDatabaseOffline = true;
    public void SetDatabaseOnline() => _isDatabaseOffline = false;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_markerFilePath))
            {
                // Marker exists: evaluate crash reconciliation state machine
                FailsafeActiveMarker? marker = null;
                try
                {
                    var markerJson = await File.ReadAllTextAsync(_markerFilePath, ct).ConfigureAwait(false);
                    marker = JsonSerializer.Deserialize<FailsafeActiveMarker>(markerJson);
                }
                catch
                {
                    // Corrupt marker -> fail closed in failsafe
                    _isInFailsafeMode = true;
                    return;
                }

                if (marker == null)
                {
                    _isInFailsafeMode = true;
                    return;
                }

                _currentMarker = marker;
                _isInFailsafeMode = true;

                if (marker.PendingClear == null)
                {
                    // State A: Active failsafe episode with no pending clear operation
                    CleanStagedFiles();
                    return;
                }

                // Check DB for committed clear audit
                var clearOpId = marker.PendingClear.ClearOperationId;
                var failsafeId = marker.FailsafeInstanceId;

                AuditLog? auditLog = null;
                var isAuditCommitted = false;
                if (!_isDatabaseOffline && (_auditRepo != null || _dbContextFactory != null))
                {
                    try
                    {
                        if (_auditRepo != null)
                        {
                            auditLog = await _auditRepo.GetFailsafeClearAuditAsync(failsafeId, clearOpId, ct).ConfigureAwait(false);
                            isAuditCommitted = auditLog != null;
                        }
                        else if (_dbContextFactory != null)
                        {
                            await using var db = _dbContextFactory();
                            auditLog = await db.AuditLogs.AsNoTracking()
                                .FirstOrDefaultAsync(a => a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED" &&
                                                          a.FailsafeInstanceId == failsafeId &&
                                                          a.ClearOperationId == clearOpId, ct).ConfigureAwait(false);
                            isAuditCommitted = auditLog != null;
                        }
                    }
                    catch
                    {
                        // DB offline or error -> fail closed
                        return;
                    }
                }

                if (!isAuditCommitted)
                {
                    // State A: Clear audit was NOT committed (transaction rolled back)
                    DiscardPendingClear(marker.PendingClear.StagedFilePath);
                    var updated = marker with { PendingClear = null };
                    WriteMarkerAtomic(updated);
                    _currentMarker = updated;
                    _isInFailsafeMode = true;
                    return;
                }

                // State D: Audit mismatch check
                // Verify FailsafeInstanceId, ClearOperationId, and TargetSettingsSha256
                if (auditLog != null)
                {
                    var isFailsafeMatch = auditLog.FailsafeInstanceId == failsafeId;
                    var isClearOpMatch = auditLog.ClearOperationId == clearOpId;
                    var isShaMatch = string.Equals(auditLog.TargetSettingsSha256, marker.PendingClear.TargetSettingsSha256, StringComparison.OrdinalIgnoreCase);

                    if (!isFailsafeMatch || !isClearOpMatch || !isShaMatch)
                    {
                        // State D: Audit mismatch -> remain in failsafe mode, marker retained
                        _isInFailsafeMode = true;
                        return;
                    }
                }

                // Audit WAS committed: check live settings file
                if (File.Exists(_settingsFilePath))
                {
                    var liveBytes = await File.ReadAllBytesAsync(_settingsFilePath, ct).ConfigureAwait(false);
                    var liveSha256 = ComputeSha256(liveBytes);

                    if (string.Equals(liveSha256, marker.PendingClear.TargetSettingsSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        // State C: Publication completed, marker removal interrupted -> reconcile marker, enter normal mode
                        TryDeleteFile(_markerFilePath);
                        _currentMarker = null;
                        _isInFailsafeMode = false;

                        try
                        {
                            var settingsJson = Encoding.UTF8.GetString(liveBytes);
                            _currentSettings = JsonSerializer.Deserialize<SecuritySettings>(settingsJson) ?? new SecuritySettings();
                        }
                        catch
                        {
                            _currentSettings = new SecuritySettings();
                        }
                        return;
                    }
                }

                // State B: Audit committed, but settings file missing or hash mismatch -> fail closed, discard staged
                DiscardPendingClear(marker.PendingClear.StagedFilePath);
                var resetMarker = marker with { PendingClear = null };
                WriteMarkerAtomic(resetMarker);
                _currentMarker = resetMarker;
                _isInFailsafeMode = true;
                return;
            }

            // No marker file: check settings file
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_settingsFilePath, ct).ConfigureAwait(false);
                    var settings = JsonSerializer.Deserialize<SecuritySettings>(json);
                    if (settings == null)
                    {
                        EnterFailsafeInternal("CORRUPTED_SETTINGS_FILE");
                        return;
                    }

                    settings.Validate();
                    _currentSettings = settings;
                    _isInFailsafeMode = false;
                }
                catch
                {
                    EnterFailsafeInternal("CORRUPTED_SETTINGS_FILE");
                }
            }
            else
            {
                // Clean initial installation: write default settings
                var defaults = new SecuritySettings();
                WriteSettingsAtomic(defaults);
                _currentSettings = defaults;
                _isInFailsafeMode = false;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void EnterFailsafeMode(string reason)
    {
        _lock.Wait();
        try
        {
            EnterFailsafeInternal(reason);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnterFailsafeInternal(string reason)
    {
        _isInFailsafeMode = true;
        if (_currentMarker == null)
        {
            var marker = new FailsafeActiveMarker(Guid.NewGuid(), DateTime.UtcNow, reason, null);
            WriteMarkerAtomic(marker);
            _currentMarker = marker;
        }

        if (!_failsafeAudited && !_isDatabaseOffline)
        {
            _failsafeAudited = true;
            try
            {
                if (_dbContextFactory != null)
                {
                    using var db = _dbContextFactory();
                    var audit = new AuditLog(
                        0,
                        "SECURITY_SETTINGS_FAILSAFE_ENTERED",
                        "SecuritySettings",
                        _currentMarker.FailsafeInstanceId.ToString(),
                        Guid.Empty,
                        Guid.Empty,
                        $"Entered security failsafe mode. Reason: {reason}",
                        success: true,
                        reason: reason,
                        failsafeInstanceId: _currentMarker.FailsafeInstanceId);
                    db.AuditLogs.Add(audit);
                    db.SaveChanges();
                }
            }
            catch
            {
                // Ignore audit failure when entering failsafe to prevent cascading fault
            }
        }
    }

    public async Task<SecuritySettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _currentSettings;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateSettingsAsync(SecuritySettings newSettings, Guid ownerUserId, string ownerUsername, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        newSettings.Validate();

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isInFailsafeMode)
            {
                throw new DomainException("Cannot update settings while in failsafe mode. Clear failsafe first.", "FAILSAFE_MODE_ACTIVE");
            }

            WriteSettingsAtomic(newSettings);
            _currentSettings = newSettings;

            if (!_isDatabaseOffline && _dbContextFactory != null)
            {
                try
                {
                    await using var db = _dbContextFactory();
                    var audit = new AuditLog(
                        0,
                        "SECURITY_SETTINGS_UPDATED",
                        "SecuritySettings",
                        ownerUserId.ToString(),
                        ownerUserId,
                        Guid.Empty,
                        "Security settings updated by owner.",
                        success: true,
                        userName: ownerUsername,
                        afterJson: JsonSerializer.Serialize(newSettings));
                    db.AuditLogs.Add(audit);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Audit failure does not revert valid settings write
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearFailsafeAsync(
        SecuritySettings newSettings,
        Guid ownerUserId,
        string ownerUsername,
        Guid? clearOperationId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        newSettings.Validate();

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isInFailsafeMode)
            {
                throw new DomainException("Store is not in failsafe mode.", "FAILSAFE_NOT_ACTIVE");
            }

            _currentMarker ??= new FailsafeActiveMarker(Guid.NewGuid(), DateTime.UtcNow, "UNKNOWN_EPISODE", null);
            var clearOpId = clearOperationId ?? Guid.NewGuid();

            // Strict Clear Operation Overwrite Rule:
            // Check if existing pending operation already has committed audit in SQLite
            if (_currentMarker.PendingClear != null)
            {
                var existingCommitted = false;
                if (!_isDatabaseOffline && (_auditRepo != null || _dbContextFactory != null))
                {
                    try
                    {
                        if (_auditRepo != null)
                        {
                            var existingAudit = await _auditRepo.GetFailsafeClearAuditAsync(_currentMarker.FailsafeInstanceId, _currentMarker.PendingClear.ClearOperationId, ct).ConfigureAwait(false);
                            existingCommitted = existingAudit != null;
                        }
                        else if (_dbContextFactory != null)
                        {
                            await using var db = _dbContextFactory();
                            var existingAudit = await db.AuditLogs.AsNoTracking()
                                .FirstOrDefaultAsync(a => a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED" &&
                                                          a.FailsafeInstanceId == _currentMarker.FailsafeInstanceId &&
                                                          a.ClearOperationId == _currentMarker.PendingClear.ClearOperationId, ct).ConfigureAwait(false);
                            existingCommitted = existingAudit != null;
                        }
                    }
                    catch
                    {
                        // Assume safe if cannot verify
                    }
                }

                if (existingCommitted && _currentMarker.PendingClear.ClearOperationId != clearOpId)
                {
                    throw new InvalidOperationException("Cannot overwrite a committed clear operation that has not been reconciled.");
                }

                // If uncommitted, discard previous staged file
                if (!existingCommitted)
                {
                    DiscardPendingClear(_currentMarker.PendingClear.StagedFilePath);
                }
            }

            // Step 1: Write staged settings file to disk
            var stagedPath = Path.Combine(_configDir, $"security_settings.json.staged.{clearOpId:N}");
            var settingsJson = JsonSerializer.Serialize(newSettings, JsonOptions);
            var settingsBytes = Encoding.UTF8.GetBytes(settingsJson);
            var targetSha256 = ComputeSha256(settingsBytes);

            await using (var fs = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await fs.WriteAsync(settingsBytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            // Step 2: Update marker via atomic publication: set PendingClear = operation
            var pendingOp = new PendingClearOperation(clearOpId, DateTime.UtcNow, ownerUserId, ownerUsername, targetSha256, stagedPath);
            var updatedMarker = _currentMarker with { PendingClear = pendingOp };
            WriteMarkerAtomic(updatedMarker);
            _currentMarker = updatedMarker;

            OnBeforeAuditCommit?.Invoke();

            // Step 3: Commit audit row to SQLite in transaction
            if (!_isDatabaseOffline && _dbContextFactory != null)
            {
                await using var db = _dbContextFactory();
                var exists = await db.AuditLogs.AnyAsync(a => a.Action == "SECURITY_SETTINGS_FAILSAFE_CLEARED" &&
                                                              a.FailsafeInstanceId == _currentMarker.FailsafeInstanceId &&
                                                              a.ClearOperationId == clearOpId, ct).ConfigureAwait(false);
                if (!exists)
                {
                    var audit = new AuditLog(
                        0,
                        "SECURITY_SETTINGS_FAILSAFE_CLEARED",
                        "SecuritySettings",
                        clearOpId.ToString(),
                        ownerUserId,
                        Guid.Empty,
                        $"Security settings failsafe cleared. Episode: {_currentMarker.FailsafeInstanceId}, Operation: {clearOpId}",
                        success: true,
                        userName: ownerUsername,
                        reason: "Failsafe cleared by store owner",
                        beforeJson: "{\"failsafeActive\": true}",
                        afterJson: settingsJson,
                        failsafeInstanceId: _currentMarker.FailsafeInstanceId,
                        clearOperationId: clearOpId,
                        targetSettingsSha256: targetSha256);

                    db.AuditLogs.Add(audit);
                    try
                    {
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UQ_audit_logs_failsafe_clear", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Database-enforced idempotency: constraint violation means row exists
                    }
                }
            }

            OnBeforeFileReplace?.Invoke();

            if (SimulateFileReplaceFailure)
            {
                throw new IOException("Simulated file replacement failure.");
            }

            // Step 4: Publish settings via File.Replace: security_settings.json.staged -> security_settings.json
            AtomicReplaceFile(stagedPath, _settingsFilePath);

            // Step 5: Verify published file SHA-256
            var liveBytes = await File.ReadAllBytesAsync(_settingsFilePath, ct).ConfigureAwait(false);
            var liveSha256 = ComputeSha256(liveBytes);
            if (!string.Equals(liveSha256, targetSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Published security settings file SHA-256 verification failed.");
            }

            OnBeforeMarkerDelete?.Invoke();

            // Step 6: Delete security_failsafe.active
            TryDeleteFile(_markerFilePath);

            OnBeforeMemoryUpdate?.Invoke();

            // Step 7: Update in-memory state and enter Normal Mode
            _currentMarker = null;
            _isInFailsafeMode = false;
            _currentSettings = newSettings;
            _failsafeAudited = false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void WriteMarkerAtomic(FailsafeActiveMarker marker)
    {
        if (SimulateMarkerWriteFailure)
        {
            throw new IOException("Simulated marker write failure.");
        }

        var json = JsonSerializer.Serialize(marker, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var tmpMarker = $"{_markerFilePath}.tmp_{Guid.NewGuid():N}";

        using (var fs = new FileStream(tmpMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, false))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        AtomicReplaceFile(tmpMarker, _markerFilePath);

        // Verify target marker integrity
        var verifyJson = File.ReadAllText(_markerFilePath);
        var readBack = JsonSerializer.Deserialize<FailsafeActiveMarker>(verifyJson);
        if (readBack == null || readBack.FailsafeInstanceId != marker.FailsafeInstanceId)
        {
            throw new InvalidOperationException("Marker verification failed after publication.");
        }
    }

    private void WriteSettingsAtomic(SecuritySettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var tmpPath = $"{_settingsFilePath}.tmp_{Guid.NewGuid():N}";

        using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, false))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        AtomicReplaceFile(tmpPath, _settingsFilePath);
    }

    private static void AtomicReplaceFile(string sourceTmpPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            var backupPath = $"{destinationPath}.bak_{Guid.NewGuid():N}";
            try
            {
                File.Replace(sourceTmpPath, destinationPath, backupPath, ignoreMetadataErrors: true);
            }
            finally
            {
                TryDeleteFile(backupPath);
            }
        }
        else
        {
            try
            {
                File.Move(sourceTmpPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                var backupPath = $"{destinationPath}.bak_{Guid.NewGuid():N}";
                try
                {
                    File.Replace(sourceTmpPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                }
                finally
                {
                    TryDeleteFile(backupPath);
                }
            }
        }
    }

    private static void DiscardPendingClear(string? stagedFilePath)
    {
        if (!string.IsNullOrWhiteSpace(stagedFilePath))
        {
            TryDeleteFile(stagedFilePath);
        }
    }

    private void CleanStagedFiles()
    {
        try
        {
            var stagedFiles = Directory.GetFiles(_configDir, "security_settings.json.staged.*");
            foreach (var f in stagedFiles)
            {
                TryDeleteFile(f);
            }
        }
        catch
        {
            // Ignore directory search exceptions
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lock.Dispose();
        }
    }
}
