using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Common;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Infrastructure.Backup;

/// <summary>
/// Authoritative backup service using SQLite online consistent backup command (VACUUM INTO).
/// Safe to execute while concurrent counter billing transactions are active in WAL mode.
/// </summary>
public sealed class SqliteDatabaseBackupService : IDatabaseBackupService
{
    private readonly RetailDbContext _context;

    public SqliteDatabaseBackupService(RetailDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<string> CreateBackupAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination directory cannot be empty.", nameof(destinationDirectory));

        Directory.CreateDirectory(destinationDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var backupFileName = $"grozet_pos_backup_{timestamp}.db";
        var fullBackupPath = Path.Combine(destinationDirectory, backupFileName);

        var connection = _context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{fullBackupPath.Replace("'", "''")}';";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        var isValid = await VerifyBackupIntegrityAsync(fullBackupPath, cancellationToken);
        if (!isValid)
        {
            throw new DomainException($"Created backup failed integrity verification: {fullBackupPath}", "BACKUP_INTEGRITY_FAILED");
        }

        return fullBackupPath;
    }

    public async Task<bool> VerifyBackupIntegrityAsync(string backupFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupFilePath)) return false;

        try
        {
            var options = new DbContextOptionsBuilder<RetailDbContext>()
                .UseSqlite($"Data Source={backupFilePath}")
                .Options;

            using var verifyContext = new RetailDbContext(options);
            var result = await DatabaseIntegrityChecker.CheckIntegrityAsync(verifyContext, cancellationToken);
            return result.QuickCheckValid;
        }
        catch
        {
            return false;
        }
    }
}
