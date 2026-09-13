using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Inventory;

public class InsufficientStockException : DomainException
{
    public Guid ProductId { get; }
    public decimal AvailableQuantity { get; }
    public decimal RequestedQuantity { get; }

    public InsufficientStockException(Guid productId, decimal available, decimal requested)
        : base($"Insufficient stock for product {productId}. Available: {available}, Requested: {requested}.", "INSUFFICIENT_STOCK")
    {
        ProductId = productId;
        AvailableQuantity = available;
        RequestedQuantity = requested;
    }
}

public sealed class StockBalance : BaseEntity<Guid>
{
    public Guid ProductId { get; private set; }
    public Quantity CurrentQuantity { get; private set; }
    public Quantity ReorderLevel { get; private set; }
    public bool IsLowStock => CurrentQuantity.Value <= ReorderLevel.Value;

    private StockBalance() { }

    public StockBalance(Guid productId, UnitOfMeasure unit, decimal initialQuantity = 0m, decimal reorderLevel = 5m)
    {
        Id = Guid.NewGuid();
        ProductId = productId;
        CurrentQuantity = Quantity.From(initialQuantity, unit);
        ReorderLevel = Quantity.From(reorderLevel, unit);
    }

    public void ApplyMovement(
        StockMovementType movementType,
        Quantity quantity,
        NegativeStockPolicy policy = NegativeStockPolicy.Disallowed)
    {
        switch (movementType)
        {
            case StockMovementType.Purchase:
            case StockMovementType.SaleReturn:
            case StockMovementType.AdjustmentIncrease:
                CurrentQuantity += quantity;
                break;

            case StockMovementType.Sale:
            case StockMovementType.PurchaseReturn:
            case StockMovementType.AdjustmentDecrease:
            case StockMovementType.Wastage:
                if (policy == NegativeStockPolicy.Disallowed && CurrentQuantity.Value < quantity.Value)
                {
                    throw new InsufficientStockException(ProductId, CurrentQuantity.Value, quantity.Value);
                }
                CurrentQuantity -= quantity;
                break;

            default:
                throw new DomainException($"Unsupported stock movement type {movementType}.", "INVALID_MOVEMENT_TYPE");
        }

        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateReorderLevel(Quantity newLevel)
    {
        ReorderLevel = newLevel;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
