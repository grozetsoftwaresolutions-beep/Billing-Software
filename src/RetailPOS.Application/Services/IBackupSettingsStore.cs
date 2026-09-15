namespace RetailPOS.Application.Services;

using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.ValueObjects;

public interface IBackupSettingsStore
{
    Task<BackupConfiguration> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(BackupConfiguration configuration, CancellationToken ct = default);
}
