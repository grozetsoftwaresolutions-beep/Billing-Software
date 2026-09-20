using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Infrastructure.Deployment.Steps;

/// <summary>
/// Migration Step 15: Creates the authoritative schema_migrations tracking table.
/// </summary>
public sealed class MigrationStep15_SchemaMigrationsTable : IMigrationStep
{
    public int Version => 15;
    public string Name => "Step15_SchemaMigrationsTable";

    public async Task ApplyAsync(RetailDbContext context, CancellationToken ct = default)
    {
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL,
    execution_time_ms INTEGER NOT NULL,
    checksum TEXT NOT NULL
);
";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
