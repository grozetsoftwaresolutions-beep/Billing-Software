namespace RetailPOS.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Enums;

public sealed class EfBackupRepository : IBackupRepository
{
    private readonly RetailDbContext _context;

    public EfBackupRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task InsertAsync(BackupRecord record, CancellationToken ct = default)
    {
        await _context.Backups.AddAsync(record, ct).ConfigureAwait(false);
    }

    public Task UpdateAsync(BackupRecord record, CancellationToken ct = default)
    {
        _context.Backups.Update(record);
        return Task.CompletedTask;
    }

    public async Task<BackupRecord?> GetByIdAsync(Guid operationId, CancellationToken ct = default)
    {
        return await _context.Backups.FirstOrDefaultAsync(b => b.BackupOperationId == operationId, ct).ConfigureAwait(false);
    }

    public async Task<BackupRecord?> GetByFileNameAsync(string fileName, CancellationToken ct = default)
    {
        return await _context.Backups.FirstOrDefaultAsync(b => b.FileName == fileName, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupRecord>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.Backups.OrderByDescending(b => b.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupRecord>> GetByTypeAsync(BackupType type, CancellationToken ct = default)
    {
        return await _context.Backups.Where(b => b.Type == type).OrderByDescending(b => b.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupRecord>> GetByStatusAsync(BackupStatus status, CancellationToken ct = default)
    {
        return await _context.Backups.Where(b => b.Status == status).OrderByDescending(b => b.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid operationId, CancellationToken ct = default)
    {
        var record = await GetByIdAsync(operationId, ct).ConfigureAwait(false);
        if (record != null)
        {
            _context.Backups.Remove(record);
        }
    }
}
