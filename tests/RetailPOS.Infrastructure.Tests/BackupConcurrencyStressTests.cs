namespace RetailPOS.Infrastructure.Tests;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetailPOS.Domain.Common;
using Xunit;

public sealed class BackupConcurrencyStressTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _connectionString;

    public BackupConcurrencyStressTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "RetailPOS_ConcurrencyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "live_store.db");
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;";

        // Initialize SQLite in WAL mode
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE sales (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    invoice_no TEXT NOT NULL,
                    amount DECIMAL NOT NULL,
                    created_at TEXT NOT NULL
                );
                INSERT INTO sales (invoice_no, amount, created_at) VALUES ('INV-0001', 100.0, datetime('now'));
            ";
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task ConcurrentBillingWrites_DuringVacuumIntoSnapshot_SucceedsWithoutDeadlock()
    {
        var backupSnapshotPath = Path.Combine(_testDir, "concurrent_backup.db");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writtenInvoices = new ConcurrentBag<string>();
        var writeErrors = new ConcurrentBag<Exception>();

        // Start background writer simulating live POS checkouts
        var writerTask = Task.Run(async () =>
        {
            int counter = 0;
            while (!cts.IsCancellationRequested && counter < 200)
            {
                counter++;
                var inv = $"INV-{counter:D5}";
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync(cts.Token);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT INTO sales (invoice_no, amount, created_at) VALUES ($inv, $amt, datetime('now'));";
                    cmd.Parameters.AddWithValue("$inv", inv);
                    cmd.Parameters.AddWithValue("$amt", 50.0 + counter);
                    await cmd.ExecuteNonQueryAsync(cts.Token);
                    writtenInvoices.Add(inv);

                    await Task.Delay(5, cts.Token); // rapid transactions
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    writeErrors.Add(ex);
                }
            }
        }, cts.Token);

        // Allow some writes to happen before snapshot starts
        await Task.Delay(50);

        // Perform VACUUM INTO snapshot on a separate connection
        using (var backupConn = new SqliteConnection(_connectionString))
        {
            await backupConn.OpenAsync(CancellationToken.None);
            using var vacuumCmd = backupConn.CreateCommand();
            vacuumCmd.CommandText = $"VACUUM INTO '{backupSnapshotPath.Replace("'", "''")}';";
            await vacuumCmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // Signal writer to stop and await
        cts.Cancel();
        await writerTask;

        // Assertions
        Assert.Empty(writeErrors);
        Assert.NotEmpty(writtenInvoices);
        Assert.True(File.Exists(backupSnapshotPath));

        // Verify SQLite integrity of the snapshot
        var snapshotConnString = $"Data Source={backupSnapshotPath};Mode=ReadOnly;";
        using (var snapConn = new SqliteConnection(snapshotConnString))
        {
            await snapConn.OpenAsync();
            using var chkCmd = snapConn.CreateCommand();
            chkCmd.CommandText = "PRAGMA integrity_check;";
            var checkResult = (string?)await chkCmd.ExecuteScalarAsync();
            Assert.Equal("ok", checkResult);

            // Verify sales table is intact and has at least initial record
            using var countCmd = snapConn.CreateCommand();
            countCmd.CommandText = "SELECT count(*) FROM sales;";
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(count >= 1, "Snapshot must contain at least 1 record.");
        }
    }
}
