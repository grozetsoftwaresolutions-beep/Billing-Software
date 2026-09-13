using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Infrastructure.Persistence.Repositories;

/// <summary>
/// Enforces database-level atomic stock updates to eliminate race conditions across multiple counters.
/// Does NOT rely solely on in-memory locks.
/// </summary>
public sealed class AtomicStockManager
{
    private readonly RetailDbContext _context;

    public AtomicStockManager(RetailDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task DeductStockAtomicallyAsync(
        Guid productId,
        Quantity quantityToDeduct,
        NegativeStockPolicy policy = NegativeStockPolicy.Disallowed,
        CancellationToken cancellationToken = default)
    {
        var stock = await _context.StockBalances
            .FirstOrDefaultAsync(s => s.ProductId == productId, cancellationToken);

        if (stock == null)
        {
            throw new InsufficientStockException(productId, 0m, quantityToDeduct.Value);
        }

        // Apply domain stock movement rule
        stock.ApplyMovement(StockMovementType.Sale, quantityToDeduct, policy);

        // Commit change with concurrency checking
        await _context.SaveChangesAsync(cancellationToken);
    }
}
