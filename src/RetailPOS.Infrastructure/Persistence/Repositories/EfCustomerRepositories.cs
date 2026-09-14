using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

public sealed class EfCustomerRepository : ICustomerRepository
{
    private readonly RetailDbContext _context;

    public EfCustomerRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Customer?> GetByIdTrackedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Customers
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Customer?> GetByPhoneAsync(string canonicalPhone, CancellationToken cancellationToken = default)
    {
        return await _context.Customers
            .FirstOrDefaultAsync(c => c.Phone == canonicalPhone, cancellationToken);
    }

    public async Task<(IReadOnlyList<Customer> Items, int TotalCount)> SearchAsync(
        string? searchTerm,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var pattern = $"%{searchTerm.Trim()}%";
            query = query.Where(c =>
                EF.Functions.Like(c.Name, pattern) ||
                (c.Phone != null && EF.Functions.Like(c.Phone, pattern)) ||
                (c.Email != null && EF.Functions.Like(c.Email, pattern)) ||
                (c.Gstin != null && EF.Functions.Like(c.Gstin, pattern)));
        }

        if (isActive.HasValue)
        {
            query = query.Where(c => c.IsActive == isActive.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await _context.Customers.AddAsync(customer, cancellationToken);
    }

    public async Task AddPaymentAsync(CustomerPayment payment, CancellationToken cancellationToken = default)
    {
        await _context.CustomerPayments.AddAsync(payment, cancellationToken);
    }

    public async Task<CustomerPayment?> GetPaymentByClientTxIdAsync(Guid clientTxId, CancellationToken cancellationToken = default)
    {
        return await _context.CustomerPayments
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ClientTransactionId == clientTxId, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerPayment>> GetPaymentsByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        return await _context.CustomerPayments
            .AsNoTracking()
            .Where(p => p.CustomerId == customerId)
            .OrderByDescending(p => p.PaymentDateUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SaleInvoice>> GetSalesByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        return await _context.SalesInvoices
            .Include(s => s.Payments)
            .Include(s => s.Items)
                .ThenInclude(i => i.TaxComponents)
            .AsNoTracking()
            .Where(s => s.CustomerId == customerId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}

public sealed class EfCashSessionRepository : ICashSessionRepository
{
    private readonly RetailDbContext _context;

    public EfCashSessionRepository(RetailDbContext context)
    {
        _context = context;
    }

    public async Task<CashSession?> GetActiveSessionAsync(Guid terminalId, CancellationToken cancellationToken = default)
    {
        return await _context.CashSessions
            .FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == CashSessionStatus.Open, cancellationToken);
    }

    public async Task<CashSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CashSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<CashSession?> GetByIdTrackedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CashSessions
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task AddSessionAsync(CashSession session, CancellationToken cancellationToken = default)
    {
        await _context.CashSessions.AddAsync(session, cancellationToken);
    }

    public async Task AddTransactionAsync(CashSessionTransaction tx, CancellationToken cancellationToken = default)
    {
        await _context.CashSessionTransactions.AddAsync(tx, cancellationToken);
    }

    public async Task<CashSessionTransaction?> GetTransactionByClientTxIdAsync(Guid clientTxId, CancellationToken cancellationToken = default)
    {
        return await _context.CashSessionTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ClientTransactionId == clientTxId, cancellationToken);
    }

    public async Task<IReadOnlyList<CashSessionTransaction>> GetTransactionsBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await _context.CashSessionTransactions
            .AsNoTracking()
            .Where(t => t.CashSessionId == sessionId)
            .OrderBy(t => t.TimestampUtc)
            .ToListAsync(cancellationToken);
    }
}
