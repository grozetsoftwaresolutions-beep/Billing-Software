using Microsoft.EntityFrameworkCore;

namespace RetailPOS.Infrastructure.Persistence;

public enum DatabaseHealthStatus
{
    Healthy = 1,
    Degraded = 2,
    Unavailable = 3,
    Corrupt = 4
}

public sealed record IntegrityCheckResult(
    DatabaseHealthStatus Status,
    string Message,
    bool ForeignKeysValid,
    bool QuickCheckValid);

/// <summary>
/// Verifies SQLite database integrity and foreign key consistency.
/// </summary>
public static class DatabaseIntegrityChecker
{
    public static async Task<IntegrityCheckResult> CheckIntegrityAsync(RetailDbContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await context.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
            {
                return new IntegrityCheckResult(DatabaseHealthStatus.Unavailable, "Cannot connect to SQLite database.", false, false);
            }

            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            using var cmd = connection.CreateCommand();

            // Run PRAGMA quick_check
            cmd.CommandText = "PRAGMA quick_check;";
            var checkResult = (string?)(await cmd.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
            var isIntegrityValid = string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase);

            if (!isIntegrityValid)
            {
                return new IntegrityCheckResult(DatabaseHealthStatus.Corrupt, $"SQLite integrity check failed: {checkResult}", false, false);
            }

            // Run PRAGMA foreign_key_check
            cmd.CommandText = "PRAGMA foreign_key_check;";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var fkValid = !reader.HasRows;

            var status = fkValid ? DatabaseHealthStatus.Healthy : DatabaseHealthStatus.Degraded;
            var msg = fkValid ? "Database is healthy and integrity verified." : "Foreign key violations detected.";

            return new IntegrityCheckResult(status, msg, fkValid, isIntegrityValid);
        }
        catch (Exception ex)
        {
            return new IntegrityCheckResult(DatabaseHealthStatus.Unavailable, $"Database check exception: {ex.Message}", false, false);
        }
    }
}
