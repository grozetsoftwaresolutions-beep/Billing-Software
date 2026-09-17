using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Application.Deployment;
using RetailPOS.Contracts.Deployment;

namespace RetailPOS.Infrastructure.Deployment;

public sealed class FileMigrationAuditJournal : IMigrationAuditJournal, IDisposable
{
    public void Dispose()
    {
        _lock.Dispose();
    }

    private readonly string _journalFilePath;
    private readonly byte[] _hmacKey;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileMigrationAuditJournal(string journalFilePath, byte[] hmacKey)
    {
        _journalFilePath = journalFilePath;
        _hmacKey = hmacKey ?? throw new ArgumentNullException(nameof(hmacKey));
    }

    public async Task AppendEntryAsync(string eventType, string operationId, string details)
    {
        await _lock.WaitAsync();
        try
        {
            var entries = await ReadEntriesInternalAsync();
            int nextSequence = entries.Count + 1;
            string previousHmac = entries.Count > 0 ? entries[^1].EntryHmac : "GENESIS";
            var timestamp = DateTime.UtcNow;

            string canonicalData = $"{nextSequence}|{timestamp:O}|{eventType}|{operationId}|{details}|{previousHmac}";
            byte[] hmacBytes = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(canonicalData));
            string entryHmac = Convert.ToHexString(hmacBytes);

            var newEntry = new MigrationJournalEntry
            {
                Sequence = nextSequence,
                TimestampUtc = timestamp,
                EventType = eventType,
                OperationId = operationId,
                Details = details,
                PreviousHmac = previousHmac,
                EntryHmac = entryHmac
            };

            string line = JsonSerializer.Serialize(newEntry) + Environment.NewLine;
            string? dir = Path.GetDirectoryName(_journalFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.AppendAllTextAsync(_journalFilePath, line, Encoding.UTF8);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> VerifyChainIntegrityAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var entries = await ReadEntriesInternalAsync();
            if (entries.Count == 0) return true;

            string expectedPreviousHmac = "GENESIS";
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Sequence != i + 1) return false;
                if (!string.Equals(entry.PreviousHmac, expectedPreviousHmac, StringComparison.OrdinalIgnoreCase)) return false;

                string canonicalData = $"{entry.Sequence}|{entry.TimestampUtc:O}|{entry.EventType}|{entry.OperationId}|{entry.Details}|{entry.PreviousHmac}";
                byte[] expectedHmacBytes = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(canonicalData));
                string expectedHmac = Convert.ToHexString(expectedHmacBytes);

                if (!string.Equals(entry.EntryHmac, expectedHmac, StringComparison.OrdinalIgnoreCase)) return false;

                expectedPreviousHmac = entry.EntryHmac;
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<MigrationJournalEntry>> ReadEntriesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadEntriesInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<MigrationJournalEntry>> ReadEntriesInternalAsync()
    {
        var list = new List<MigrationJournalEntry>();
        if (!File.Exists(_journalFilePath)) return list;

        string[] lines = await File.ReadAllLinesAsync(_journalFilePath, Encoding.UTF8);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = JsonSerializer.Deserialize<MigrationJournalEntry>(line);
            if (entry != null) list.Add(entry);
        }

        return list;
    }
}
