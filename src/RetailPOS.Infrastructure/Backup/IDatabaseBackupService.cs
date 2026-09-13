namespace RetailPOS.Infrastructure.Backup;

public interface IDatabaseBackupService
{
    Task<string> CreateBackupAsync(string destinationDirectory, CancellationToken cancellationToken = default);
    Task<bool> VerifyBackupIntegrityAsync(string backupFilePath, CancellationToken cancellationToken = default);
}
