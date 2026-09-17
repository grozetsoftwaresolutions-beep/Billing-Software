using System.Collections.Generic;
using System.Threading.Tasks;
using RetailPOS.Contracts.Deployment;

namespace RetailPOS.Application.Deployment;

public interface IMigrationAuditJournal
{
    Task AppendEntryAsync(string eventType, string operationId, string details);
    Task<bool> VerifyChainIntegrityAsync();
    Task<IReadOnlyList<MigrationJournalEntry>> ReadEntriesAsync();
}
