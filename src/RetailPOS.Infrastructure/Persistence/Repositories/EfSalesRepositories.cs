using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Sales;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfSaleRepository : ISaleRepository
{
    private readonly RetailDbContext _context;

    public EfSaleRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<SaleInvoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.SalesInvoices
            .Include(s => s.Items)
                .ThenInclude(i => i.TaxComponents)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<SaleInvoice?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default)
    {
        return await _context.SalesInvoices
            .Include(s => s.Items)
                .ThenInclude(i => i.TaxComponents)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.InvoiceNumber == invoiceNumber, cancellationToken);
    }

    public async Task<SaleInvoice?> GetByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default)
    {
        return await _context.SalesInvoices
            .Include(s => s.Items)
                .ThenInclude(i => i.TaxComponents)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.ClientTransactionId == clientTransactionId, cancellationToken);
    }

    public async Task<(IReadOnlyList<SaleInvoice> Items, int TotalCount)> SearchAsync(SaleSearchRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.SalesInvoices
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.InvoiceNumber))
        {
            query = query.Where(s => s.InvoiceNumber.Contains(request.InvoiceNumber));
        }

        if (request.CashierId.HasValue)
        {
            query = query.Where(s => s.CashierId == request.CashierId.Value);
        }

        if (request.TerminalId.HasValue)
        {
            query = query.Where(s => s.TerminalId == request.TerminalId.Value);
        }

        if (request.FromUtc.HasValue)
        {
            query = query.Where(s => s.CreatedAtUtc >= request.FromUtc.Value);
        }

        if (request.ToUtc.HasValue)
        {
            query = query.Where(s => s.CreatedAtUtc <= request.ToUtc.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var page = request.Page <= 0 ? 1 : request.Page;
        var pageSize = request.PageSize <= 0 ? 20 : request.PageSize;

        var items = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(SaleInvoice sale, CancellationToken cancellationToken = default)
    {
        await _context.SalesInvoices.AddAsync(sale, cancellationToken);
    }

    public async Task<int> IncrementReprintCountAsync(Guid saleInvoiceId, DateTime reprintedAtUtc, CancellationToken cancellationToken = default)
    {
        var isoTime = reprintedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var rows = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sales_invoices SET ReprintCount = ReprintCount + 1, LastReprintedAtUtc = {isoTime} WHERE Id = {saleInvoiceId};",
            cancellationToken);

        var local = _context.SalesInvoices.Local.FirstOrDefault(s => s.Id == saleInvoiceId);
        if (local != null)
        {
            await _context.Entry(local).ReloadAsync(cancellationToken);
        }

        return rows;
    }

    public Task UpdateAsync(SaleInvoice sale, CancellationToken cancellationToken = default)
    {
        _context.SalesInvoices.Update(sale);
        return Task.CompletedTask;
    }
}

public sealed class EfSaleRefundRepository : ISaleRefundRepository
{
    private readonly RetailDbContext _context;

    public EfSaleRefundRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task AddRefundAsync(SaleRefund refund, CancellationToken cancellationToken = default)
    {
        await _context.SaleRefunds.AddAsync(refund, cancellationToken);
    }

    public async Task<SaleRefund?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.SaleRefunds
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<SaleRefund?> GetByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default)
    {
        return await _context.SaleRefunds
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.ClientTransactionId == clientTransactionId, cancellationToken);
    }

    public async Task<IReadOnlyList<SaleRefund>> GetRefundsBySaleIdAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        return await _context.SaleRefunds
            .Include(r => r.Items)
            .Where(r => r.SaleId == saleId)
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<decimal> GetRefundedQuantityForSaleItemAsync(long saleItemId, CancellationToken cancellationToken = default)
    {
        var items = await _context.SaleRefundItems
            .Where(i => i.SaleItemId == saleItemId)
            .ToListAsync(cancellationToken);

        return items.Sum(i => i.QuantityRefunded.Value);
    }

    public async Task<long> GetTotalRefundedAmountForSaleAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var refunds = await _context.SaleRefunds
            .Where(r => r.SaleId == saleId)
            .ToListAsync(cancellationToken);

        return refunds.Sum(r => r.TotalRefundAmount.MinorUnits);
    }
}

public sealed class EfInvoiceSequenceRepository : IInvoiceSequenceRepository
{
    private readonly RetailDbContext _context;

    public EfInvoiceSequenceRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<string> GetNextSequenceNumberAsync(
        string sequenceName,
        string prefix,
        string periodDate,
        CancellationToken cancellationToken = default)
    {
        var seqId = $"{sequenceName.Trim().ToUpperInvariant()}-{periodDate.Trim()}";
        var sequence = await _context.InvoiceSequences
            .FirstOrDefaultAsync(s => s.Id == seqId, cancellationToken);

        if (sequence == null)
        {
            sequence = new InvoiceSequence(sequenceName, prefix, periodDate, 0);
            _context.InvoiceSequences.Add(sequence);
        }

        return sequence.NextNumber();
    }
}
