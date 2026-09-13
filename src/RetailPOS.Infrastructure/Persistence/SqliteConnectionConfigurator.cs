using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RetailPOS.Infrastructure.Persistence;

/// <summary>
/// Configures SQLite connections for maximum reliability, durability, and multi-counter concurrency.
/// Applies WAL mode, foreign key enforcement, 5000ms busy timeout, and synchronous=NORMAL.
/// </summary>
public static class SqliteConnectionConfigurator
{
    public static void ApplyPragmas(DbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var cmd = connection.CreateCommand();
        // 1. Write-Ahead Logging for non-blocking concurrent reads during active billing
        cmd.CommandText = "PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();

        // 2. Strict relational integrity across master, sales, and inventory tables
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();

        // 3. 5-second lock timeout prevents immediate 'database is locked' errors under multi-counter bursts
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();

        // 4. Synchronous NORMAL ensures full data durability in WAL mode without synchronous disk flush delays
        cmd.CommandText = "PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();
    }
}
