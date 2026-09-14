using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Configuration;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

/// <summary>
/// Database-backed centralized sequence generator ensuring unique, collision-free invoice numbering.
/// </summary>
public sealed class InvoiceSequenceGenerator
{
    private readonly RetailDbContext _context;

    public InvoiceSequenceGenerator(RetailDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<string> GenerateNextInvoiceNumberAsync(string sequenceName = "DEFAULT_SALE", CancellationToken cancellationToken = default)
    {
        var periodDate = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var id = $"{sequenceName.Trim().ToUpperInvariant()}-{periodDate}";

        var sequence = await _context.InvoiceSequences
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (sequence == null)
        {
            sequence = new InvoiceSequence(sequenceName, "INV", periodDate, 0);
            _context.InvoiceSequences.Add(sequence);
        }

        var nextInvoice = sequence.NextInvoiceNumber();
        await _context.SaveChangesAsync(cancellationToken);

        return nextInvoice;
    }
}
