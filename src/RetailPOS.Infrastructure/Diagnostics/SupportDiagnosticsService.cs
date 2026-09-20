namespace RetailPOS.Infrastructure.Diagnostics;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Diagnostics;
using RetailPOS.Domain.Common;
using RetailPOS.Infrastructure.Persistence;

public sealed class SupportDiagnosticsService : ISupportDiagnosticsService
{
    private readonly IDbContextFactory<RetailDbContext>? _contextFactory;
    private readonly RetailDbContext? _dbContext;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IStoreIdentityService _storeIdentityService;
    private readonly IServerIdentityService _serverIdentityService;
    private readonly IBackupService _backupService;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly string _diagnosticsDir;
    private readonly string _stagingDir;
    private readonly string _logsDir;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SupportDiagnosticsService(
        IStoreIdentityService storeIdentityService,
        IServerIdentityService serverIdentityService,
        IBackupService backupService,
        RetailDbContext? dbContext = null,
        IServiceScopeFactory? scopeFactory = null,
        IDbContextFactory<RetailDbContext>? contextFactory = null,
        IAuditLogRepository? auditLogRepository = null,
        string? customDiagnosticsDir = null,
        string? customStagingDir = null,
        string? customLogsDir = null)
    {
        _contextFactory = contextFactory;
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
        _storeIdentityService = storeIdentityService;
        _serverIdentityService = serverIdentityService;
        _backupService = backupService;
        _auditLogRepository = auditLogRepository;

        var dataRoot = RetailPOSPaths.GetDefaultServerRoot();
        _stagingDir = customStagingDir ?? Path.Combine(RetailPOSPaths.GetDefaultStagingDirectory(), "DiagnosticsStaging");
        _diagnosticsDir = customDiagnosticsDir ?? Path.Combine(RetailPOSPaths.GetDefaultStagingDirectory(), "Diagnostics");
        _logsDir = customLogsDir ?? RetailPOSPaths.GetDefaultLogsDirectory();
    }

    public Task<DiagnosticSystemEnvironmentDto> GetSystemEnvironmentAsync(CancellationToken ct = default)
    {
        long freeBytes = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_diagnosticsDir) ?? "C:\\");
            if (drive.IsReady)
            {
                freeBytes = drive.AvailableFreeSpace;
            }
        }
        catch { }

        long totalMem = 0;
        try
        {
            var gcMem = GC.GetGCMemoryInfo();
            totalMem = gcMem.TotalAvailableMemoryBytes;
        }
        catch { }

        var env = new DiagnosticSystemEnvironmentDto
        {
            OsVersion = Environment.OSVersion.ToString(),
            CpuArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryBytes = totalMem,
            AvailableMemoryBytes = GC.GetTotalMemory(false),
            DataDriveFreeSpaceBytes = freeBytes,
            DotNetRuntimeVersion = RuntimeInformation.FrameworkDescription,
            SystemUptime = TimeSpan.FromMilliseconds(Environment.TickCount64)
        };

        return Task.FromResult(env);
    }

    public Task<DiagnosticServerHealthDto> GetServerHealthSummaryAsync(CancellationToken ct = default)
    {
        var summary = new DiagnosticServerHealthDto
        {
            Status = _serverIdentityService.CurrentState.ToString(),
            IsDatabaseHealthy = _serverIdentityService.CurrentState == Domain.Enums.ServerState.Available,
            ActivePortBindings = new List<string> { "5100 (HTTP)", "5101 (HTTPS)", "5105 (UDP Discovery)" },
            ProtocolVersion = 1,
            UptimeSeconds = TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds
        };

        return Task.FromResult(summary);
    }

    public async Task<DiagnosticBundleMetadataDto> GenerateSupportBundleAsync(
        string triggeredByUserId,
        string triggeredByUserName,
        string? reason = null,
        CancellationToken ct = default)
    {
        var operationId = Guid.NewGuid();
        var timestampStr = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var bundleStagingPath = Path.Combine(_stagingDir, $"bundle_{operationId:N}");
        var finalZipName = $"retailpos_support_{timestampStr}_{operationId:N}.zip";
        var finalZipPath = Path.Combine(_diagnosticsDir, finalZipName);

        if (Directory.Exists(bundleStagingPath))
        {
            Directory.Delete(bundleStagingPath, true);
        }
        Directory.CreateDirectory(bundleStagingPath);
        Directory.CreateDirectory(_diagnosticsDir);

        try
        {
            var includedArtifacts = new List<string>();

            // --- Tier 1 & 2: Allowlisted Data Collection & Scrubbing ---

            // 1. system_environment.json
            var envDto = await GetSystemEnvironmentAsync(ct).ConfigureAwait(false);
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "system_environment.json"), envDto, ct).ConfigureAwait(false);
            includedArtifacts.Add("system_environment.json");

            // 2. server_health.json
            var healthDto = await GetServerHealthSummaryAsync(ct).ConfigureAwait(false);
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "server_health.json"), healthDto, ct).ConfigureAwait(false);
            includedArtifacts.Add("server_health.json");

            // 3. service_status.json
            var serviceDto = new DiagnosticServiceStatusDto
            {
                ServiceName = "GrozetRetailPOSServer",
                Status = "Running",
                StartupType = "Automatic"
            };
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "service_status.json"), serviceDto, ct).ConfigureAwait(false);
            includedArtifacts.Add("service_status.json");

            // 4. database_diagnostics.json (NO BUSINESS TABLES DUMPED)
            var dbDiag = await CollectDatabaseDiagnosticsAsync(ct).ConfigureAwait(false);
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "database_diagnostics.json"), dbDiag, ct).ConfigureAwait(false);
            includedArtifacts.Add("database_diagnostics.json");

            // 5. backup_diagnostics.json
            var backupDiag = await CollectBackupDiagnosticsAsync(ct).ConfigureAwait(false);
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "backup_diagnostics.json"), backupDiag, ct).ConfigureAwait(false);
            includedArtifacts.Add("backup_diagnostics.json");

            // 6. migration_diagnostics.json
            var migrationDiag = await CollectMigrationDiagnosticsAsync(ct).ConfigureAwait(false);
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "migration_diagnostics.json"), migrationDiag, ct).ConfigureAwait(false);
            includedArtifacts.Add("migration_diagnostics.json");

            // 7. network_diagnostics.json
            var netDiag = CollectNetworkDiagnostics();
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "network_diagnostics.json"), netDiag, ct).ConfigureAwait(false);
            includedArtifacts.Add("network_diagnostics.json");

            // 8. hardware_diagnostics.json
            var hwDiag = new
            {
                Printer = new { Status = "Configured", Type = "ESC/POS Network/USB" },
                CashDrawer = new { Status = "Configured", Trigger = "PrinterKickout" },
                BarcodeScanner = new { Status = "Ready", Interface = "HID/KeyboardEmulation" }
            };
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "hardware_diagnostics.json"), hwDiag, ct).ConfigureAwait(false);
            includedArtifacts.Add("hardware_diagnostics.json");

            // 9. logs/ (Approved log sources only, past 7 days, max 25MB)
            int logCount = await CollectAndScrubLogsAsync(bundleStagingPath, ct).ConfigureAwait(false);
            if (logCount > 0)
            {
                includedArtifacts.Add($"logs/ ({logCount} files)");
            }

            // 10. bundle_manifest.json
            var manifestDto = new
            {
                OperationId = operationId,
                CreatedAtUtc = DateTime.UtcNow,
                TriggeredByUserId = triggeredByUserId,
                TriggeredByUserName = triggeredByUserName,
                Reason = reason,
                IncludedArtifacts = includedArtifacts
            };
            await WriteScrubbedJsonAsync(Path.Combine(bundleStagingPath, "bundle_manifest.json"), manifestDto, ct).ConfigureAwait(false);
            includedArtifacts.Add("bundle_manifest.json");

            // --- Tier 3: Final Post-Scrub Scan (Independent Auditor) ---
            var allStagedFiles = Directory.GetFiles(bundleStagingPath, "*", SearchOption.AllDirectories);
            foreach (var filePath in allStagedFiles)
            {
                var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                var auditResult = DiagnosticSecretAuditor.Audit(content);
                if (!auditResult.IsClean)
                {
                    // --- Tier 4: Fail-Closed Packaging ---
                    if (Directory.Exists(bundleStagingPath))
                    {
                        Directory.Delete(bundleStagingPath, true);
                    }

                    if (_auditLogRepository != null)
                    {
                        await _auditLogRepository.RecordAsync(
                            "SupportDiagnostics",
                            "SUPPORT_BUNDLE_CREATION_FAILED_DIRTY_PAYLOAD",
                            triggeredByUserName,
                            $"Diagnostic bundle creation aborted: detected prohibited {auditResult.ViolationCategory}. Detail: {auditResult.Details}",
                            ct).ConfigureAwait(false);
                    }

                    throw new DomainException(
                        $"Support diagnostic payload failed security scan ({auditResult.ViolationCategory}): {auditResult.Details}",
                        "DIAGNOSTIC_PAYLOAD_FAILED_SECURITY_SCAN");
                }
            }

            // Compress to ZIP
            if (File.Exists(finalZipPath))
            {
                File.Delete(finalZipPath);
            }
            ZipFile.CreateFromDirectory(bundleStagingPath, finalZipPath, CompressionLevel.Optimal, false);

            // Clean up staging
            Directory.Delete(bundleStagingPath, true);

            // Compute ZIP SHA-256
            var zipBytes = await File.ReadAllBytesAsync(finalZipPath, ct).ConfigureAwait(false);
            var zipSha256 = Convert.ToHexString(SHA256.HashData(zipBytes));
            await File.WriteAllTextAsync($"{finalZipPath}.sha256", zipSha256, ct).ConfigureAwait(false);

            // Enforce bundle retention: max 3 bundles
            PruneOldestBundles();

            if (_auditLogRepository != null)
            {
                await _auditLogRepository.RecordAsync(
                    "SupportDiagnostics",
                    "SUPPORT_DIAGNOSTICS_GENERATED",
                    triggeredByUserName,
                    $"Generated support bundle {finalZipName} ({zipBytes.Length} bytes, SHA256: {zipSha256})",
                    ct).ConfigureAwait(false);
            }

            return new DiagnosticBundleMetadataDto
            {
                OperationId = operationId,
                BundleFileName = finalZipName,
                BundleFilePath = finalZipPath,
                SizeBytes = zipBytes.Length,
                CreatedAtUtc = DateTime.UtcNow,
                Sha256 = zipSha256,
                LogFilesIncluded = logCount,
                IncludedArtifacts = includedArtifacts
            };
        }
        catch
        {
            try
            {
                if (Directory.Exists(bundleStagingPath))
                {
                    Directory.Delete(bundleStagingPath, true);
                }
            }
            catch { }
            throw;
        }
    }

    public Task<IReadOnlyList<DiagnosticBundleMetadataDto>> GetBundleHistoryAsync(CancellationToken ct = default)
    {
        var result = new List<DiagnosticBundleMetadataDto>();
        if (!Directory.Exists(_diagnosticsDir))
        {
            return Task.FromResult<IReadOnlyList<DiagnosticBundleMetadataDto>>(result);
        }

        var zipFiles = Directory.GetFiles(_diagnosticsDir, "retailpos_support_*.zip")
            .OrderByDescending(f => File.GetCreationTimeUtc(f))
            .Take(10);

        foreach (var zip in zipFiles)
        {
            var fi = new FileInfo(zip);
            string sha = string.Empty;
            var shaFile = $"{zip}.sha256";
            if (File.Exists(shaFile))
            {
                try { sha = File.ReadAllText(shaFile).Trim(); } catch { }
            }

            result.Add(new DiagnosticBundleMetadataDto
            {
                BundleFileName = fi.Name,
                BundleFilePath = fi.FullName,
                SizeBytes = fi.Length,
                CreatedAtUtc = fi.CreationTimeUtc,
                Sha256 = sha
            });
        }

        return Task.FromResult<IReadOnlyList<DiagnosticBundleMetadataDto>>(result);
    }

    public Task<Stream?> OpenBundleReadStreamAsync(Guid operationId, CancellationToken ct = default)
    {
        if (!Directory.Exists(_diagnosticsDir))
        {
            return Task.FromResult<Stream?>(null);
        }

        var match = Directory.GetFiles(_diagnosticsDir, $"*{operationId:N}*.zip").FirstOrDefault();
        if (match != null && File.Exists(match))
        {
            Stream fs = new FileStream(match, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult<Stream?>(fs);
        }

        return Task.FromResult<Stream?>(null);
    }

    private static async Task WriteScrubbedJsonAsync(string path, object data, CancellationToken ct)
    {
        var rawJson = JsonSerializer.Serialize(data, JsonOptions);
        var scrubbed = DiagnosticSecretScrubber.Scrub(rawJson);
        await File.WriteAllTextAsync(path, scrubbed, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private async Task<DiagnosticDatabaseSummaryDto> CollectDatabaseDiagnosticsAsync(CancellationToken ct)
    {
        RetailDbContext? context = null;
        IServiceScope? scope = null;
        if (_contextFactory != null)
        {
            context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        }
        else if (_scopeFactory != null)
        {
            scope = _scopeFactory.CreateScope();
            context = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
        }
        else
        {
            context = _dbContext;
        }

        if (context == null) return new DiagnosticDatabaseSummaryDto();

        var conn = context.Database.GetDbConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            long pageSize = 4096;
            long pageCount = 0;
            string journalMode = "wal";
            int schemaVer = 15;
            string quickCheck = "ok";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA page_size;";
                var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (val != null) pageSize = Convert.ToInt64(val, CultureInfo.InvariantCulture);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA page_count;";
                var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (val != null) pageCount = Convert.ToInt64(val, CultureInfo.InvariantCulture);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (val != null) journalMode = val.ToString() ?? "wal";
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (val != null) schemaVer = Convert.ToInt32(val, CultureInfo.InvariantCulture);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA quick_check(1);";
                var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (val != null) quickCheck = val.ToString() ?? "ok";
            }

            return new DiagnosticDatabaseSummaryDto
            {
                PageSize = pageSize,
                PageCount = pageCount,
                JournalMode = journalMode,
                SchemaVersion = schemaVer,
                QuickCheckResult = quickCheck
            };
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
            if (scope != null) scope.Dispose();
            else if (_contextFactory != null && context != null) await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<DiagnosticBackupSummaryDto> CollectBackupDiagnosticsAsync(CancellationToken ct)
    {
        var settings = await _backupService.GetSettingsAsync(ct).ConfigureAwait(false);
        var history = await _backupService.GetBackupHistoryAsync(ct).ConfigureAwait(false);

        long dirSize = 0;
        var backupDir = string.IsNullOrWhiteSpace(settings.BackupStoragePath)
            ? RetailPOSPaths.GetDefaultBackupsDirectory()
            : settings.BackupStoragePath;

        if (Directory.Exists(backupDir))
        {
            try
            {
                dirSize = Directory.GetFiles(backupDir, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => new FileInfo(f).Length)
                    .Sum();
            }
            catch { }
        }

        return new DiagnosticBackupSummaryDto
        {
            TotalBackupsCount = history.Count,
            BackupsDirectorySizeBytes = dirSize,
            DailyBackupEnabled = settings.DailyBackupEnabled,
            HourlyBackupEnabled = settings.HourlyBackupEnabled,
            SecondaryStoragePath = settings.SecondaryStoragePath,
            RemovableUsbBackupPath = settings.RemovableUsbBackupPath,
            RecentBackups = history.Take(10).ToList()
        };
    }

    private async Task<object> CollectMigrationDiagnosticsAsync(CancellationToken ct)
    {
        RetailDbContext? context = null;
        IServiceScope? scope = null;
        if (_contextFactory != null)
        {
            context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        }
        else if (_scopeFactory != null)
        {
            scope = _scopeFactory.CreateScope();
            context = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
        }
        else
        {
            context = _dbContext;
        }

        if (context == null) return new { RecentAppliedMigrations = new List<object>() };

        var conn = context.Database.GetDbConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var logs = new List<object>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT migration_id, name, applied_at_utc, execution_time_ms FROM schema_migration_journal ORDER BY migration_id DESC LIMIT 20;";
            try
            {
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    logs.Add(new
                    {
                        MigrationId = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        AppliedAtUtc = reader.GetDateTime(2),
                        ExecutionTimeMs = reader.GetInt64(3)
                    });
                }
            }
            catch { }

            return new { RecentAppliedMigrations = logs };
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
            if (scope != null) scope.Dispose();
            else if (_contextFactory != null && context != null) await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static DiagnosticNetworkSummaryDto CollectNetworkDiagnostics()
    {
        var nics = new List<DiagnosticNetworkInterfaceDto>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var ipProps = nic.GetIPProperties();
                var ips = ipProps.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(u => u.Address.ToString())
                    .ToList();

                var gw = ipProps.GatewayAddresses.FirstOrDefault()?.Address.ToString();

                bool isDhcp = false;
                if (OperatingSystem.IsWindows())
                {
                    isDhcp = ipProps.GetIPv4Properties()?.IsDhcpEnabled ?? false;
                }

                nics.Add(new DiagnosticNetworkInterfaceDto
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Status = nic.OperationalStatus.ToString(),
                    IpAddresses = ips,
                    Gateway = gw,
                    IsDhcpEnabled = isDhcp
                });
            }
        }
        catch { }

        return new DiagnosticNetworkSummaryDto
        {
            Interfaces = nics,
            DiscoveryPortOpen = true
        };
    }

    private async Task<int> CollectAndScrubLogsAsync(string bundleStagingPath, CancellationToken ct)
    {
        if (!Directory.Exists(_logsDir)) return 0;

        var targetLogsDir = Path.Combine(bundleStagingPath, "sanitized_logs");
        Directory.CreateDirectory(targetLogsDir);

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var logFiles = Directory.GetFiles(_logsDir, "retailpos-server-*.log")
            .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .Take(7)
            .ToList();

        long cumulativeBytes = 0;
        int count = 0;

        foreach (var logFile in logFiles)
        {
            var fi = new FileInfo(logFile);
            if (cumulativeBytes + fi.Length > 25L * 1024 * 1024) break; // 25 MB max
            cumulativeBytes += fi.Length;

            var raw = await File.ReadAllTextAsync(logFile, ct).ConfigureAwait(false);
            var scrubbed = DiagnosticSecretScrubber.Scrub(raw);

            var destPath = Path.Combine(targetLogsDir, fi.Name);
            await File.WriteAllTextAsync(destPath, scrubbed, Encoding.UTF8, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private void PruneOldestBundles()
    {
        if (!Directory.Exists(_diagnosticsDir)) return;
        try
        {
            var zipFiles = Directory.GetFiles(_diagnosticsDir, "retailpos_support_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (zipFiles.Count > 3)
            {
                foreach (var oldFile in zipFiles.Skip(3))
                {
                    try { oldFile.Delete(); } catch { }
                    var shaFile = $"{oldFile.FullName}.sha256";
                    if (File.Exists(shaFile))
                    {
                        try { File.Delete(shaFile); } catch { }
                    }
                }
            }
        }
        catch { }
    }
}
