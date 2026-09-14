using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfSupplierRepository : ISupplierRepository
{
    private readonly RetailDbContext _context;

    public EfSupplierRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<Supplier?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<Supplier?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        return await _context.Suppliers
            .FirstOrDefaultAsync(s => EF.Functions.Like(s.Name, trimmed), cancellationToken);
    }

    public async Task<bool> ExistsByNameAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        var query = _context.Suppliers.Where(s => EF.Functions.Like(s.Name, trimmed));
        if (excludeId.HasValue)
        {
            query = query.Where(s => s.Id != excludeId.Value);
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Supplier> Items, int TotalCount)> SearchAsync(
        string? searchTerm,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Suppliers.AsNoTracking();

        if (isActive.HasValue)
        {
            query = query.Where(s => s.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var pattern = $"%{searchTerm.Trim()}%";
            query = query.Where(s =>
                EF.Functions.Like(s.Name, pattern) ||
                (s.ContactPerson != null && EF.Functions.Like(s.ContactPerson, pattern)) ||
                (s.Phone != null && EF.Functions.Like(s.Phone, pattern)) ||
                (s.Email != null && EF.Functions.Like(s.Email, pattern)) ||
                (s.TaxNumber != null && EF.Functions.Like(s.TaxNumber, pattern)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(s => s.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Supplier supplier, CancellationToken cancellationToken = default)
    {
        await _context.Suppliers.AddAsync(supplier, cancellationToken);
    }

    public async Task<SupplierPayment?> GetPaymentByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default)
    {
        return await _context.SupplierPayments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.ClientTransactionId == clientTransactionId, cancellationToken);
    }

    public async Task AddPaymentAsync(SupplierPayment payment, CancellationToken cancellationToken = default)
    {
        await _context.SupplierPayments.AddAsync(payment, cancellationToken);
    }

    public async Task<IReadOnlyList<SupplierPayment>> GetPaymentsBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default)
    {
        return await _context.SupplierPayments
            .Include(p => p.Allocations)
            .Where(p => p.SupplierId == supplierId)
            .OrderByDescending(p => p.PaymentDateUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<SupplierPayment?> GetPaymentByIdAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        return await _context.SupplierPayments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
    }
}

public sealed class EfPurchaseRepository : IPurchaseRepository
{
    private readonly RetailDbContext _context;

    public EfPurchaseRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<PurchaseInvoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.PurchaseInvoices
            .Include(p => p.Items)
                .ThenInclude(i => i.TaxComponents)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<PurchaseInvoice?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default)
    {
        var trimmed = invoiceNumber.Trim();
        return await _context.PurchaseInvoices
            .Include(p => p.Items)
                .ThenInclude(i => i.TaxComponents)
            .FirstOrDefaultAsync(p => p.InvoiceNumber == trimmed, cancellationToken);
    }

    public async Task<PurchaseInvoice?> GetBySupplierInvoiceNumberAsync(Guid supplierId, string supplierInvoiceNumber, CancellationToken cancellationToken = default)
    {
        var trimmed = supplierInvoiceNumber.Trim();
        return await _context.PurchaseInvoices
            .Include(p => p.Items)
                .ThenInclude(i => i.TaxComponents)
            .FirstOrDefaultAsync(p => p.SupplierId == supplierId && EF.Functions.Like(p.SupplierInvoiceNumber, trimmed), cancellationToken);
    }

    public async Task<PurchaseInvoice?> GetByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default)
    {
        return await _context.PurchaseInvoices
            .Include(p => p.Items)
                .ThenInclude(i => i.TaxComponents)
            .FirstOrDefaultAsync(p => p.ClientTransactionId == clientTransactionId, cancellationToken);
    }

    public async Task<PurchaseInvoice?> GetByCancellationClientTransactionIdAsync(Guid cancellationClientTransactionId, CancellationToken cancellationToken = default)
    {
        return await _context.PurchaseInvoices
            .Include(p => p.Items)
                .ThenInclude(i => i.TaxComponents)
            .FirstOrDefaultAsync(p => p.CancellationClientTransactionId == cancellationClientTransactionId, cancellationToken);
    }

    public async Task<(IReadOnlyList<PurchaseInvoice> Items, int TotalCount)> SearchAsync(
        Guid? supplierId,
        DateTime? fromDateUtc,
        DateTime? toDateUtc,
        int? paymentStatus,
        int? status,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PurchaseInvoices
            .Include(p => p.Items)
            .AsNoTracking();

        if (supplierId.HasValue)
        {
            query = query.Where(p => p.SupplierId == supplierId.Value);
        }

        if (fromDateUtc.HasValue)
        {
            query = query.Where(p => p.ReceivedDateUtc >= fromDateUtc.Value);
        }

        if (toDateUtc.HasValue)
        {
            query = query.Where(p => p.ReceivedDateUtc <= toDateUtc.Value);
        }

        if (paymentStatus.HasValue)
        {
            var ps = (PurchasePaymentStatus)paymentStatus.Value;
            query = query.Where(p => p.PaymentStatus == ps);
        }

        if (status.HasValue)
        {
            var s = (PurchaseStatus)status.Value;
            query = query.Where(p => p.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var pattern = $"%{searchTerm.Trim()}%";
            query = query.Where(p =>
                EF.Functions.Like(p.InvoiceNumber, pattern) ||
                EF.Functions.Like(p.SupplierInvoiceNumber, pattern));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.ReceivedDateUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<PurchaseInvoice>> GetOpenInvoicesBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default)
    {
        return await _context.PurchaseInvoices
            .Where(p => p.SupplierId == supplierId && p.Status == PurchaseStatus.Received && p.BalanceDue > Money.Zero())
            .OrderBy(p => p.InvoiceDate)
            .ThenBy(p => p.ReceivedDateUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(PurchaseInvoice purchase, CancellationToken cancellationToken = default)
    {
        await _context.PurchaseInvoices.AddAsync(purchase, cancellationToken);
    }
}
