namespace RetailPOS.Infrastructure.Persistence;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.ValueObjects;

public sealed class JsonBackupSettingsStore : IBackupSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public JsonBackupSettingsStore(string? customSettingsPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customSettingsPath))
        {
            _settingsFilePath = customSettingsPath;
        }
        else
        {
            var baseDir = RetailPOSPaths.GetDefaultSecurityDirectory();
            _settingsFilePath = Path.Combine(baseDir, "backup_settings.json");
        }
    }

    public async Task<BackupConfiguration> GetSettingsAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                var defaultConfig = CreateDefault();
                await SaveInternalAsync(defaultConfig, ct).ConfigureAwait(false);
                return defaultConfig;
            }

            var json = await File.ReadAllTextAsync(_settingsFilePath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<BackupConfiguration>(json);
            return parsed ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveSettingsAsync(BackupConfiguration configuration, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(configuration, ct).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task SaveInternalAsync(BackupConfiguration configuration, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(configuration, IndentedJsonOptions);
        var tmpPath = $"{_settingsFilePath}.tmp_{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);

        if (File.Exists(_settingsFilePath))
        {
            File.Replace(tmpPath, _settingsFilePath, null);
        }
        else
        {
            File.Move(tmpPath, _settingsFilePath);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _semaphore.Dispose();
        }
    }

    private static BackupConfiguration CreateDefault()
    {
        var defaultBackupDir = RetailPOSPaths.GetDefaultBackupsDirectory();
        return new BackupConfiguration
        {
            DailyBackupEnabled = true,
            DailyBackupTime = new TimeSpan(23, 0, 0),
            HourlyBackupEnabled = false,
            HourlyIntervalHours = 4,
            RetentionDailyCount = 14,
            RetentionHourlyCount = 48,
            RetentionSnapshotCount = 5,
            BackupStoragePath = defaultBackupDir
        };
    }
}
