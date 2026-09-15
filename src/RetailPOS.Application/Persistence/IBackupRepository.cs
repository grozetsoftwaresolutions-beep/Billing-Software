namespace RetailPOS.Application.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Enums;

public interface IBackupRepository
{
    Task InsertAsync(BackupRecord record, CancellationToken ct = default);
    Task UpdateAsync(BackupRecord record, CancellationToken ct = default);
    Task<BackupRecord?> GetByIdAsync(Guid operationId, CancellationToken ct = default);
    Task<BackupRecord?> GetByFileNameAsync(string fileName, CancellationToken ct = default);
    Task<IReadOnlyList<BackupRecord>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BackupRecord>> GetByTypeAsync(BackupType type, CancellationToken ct = default);
    Task<IReadOnlyList<BackupRecord>> GetByStatusAsync(BackupStatus status, CancellationToken ct = default);
    Task DeleteAsync(Guid operationId, CancellationToken ct = default);
}
