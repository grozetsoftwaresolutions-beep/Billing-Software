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
    public bool IsLowStock { get; private set; }

    private StockBalance() { }

    public StockBalance(Guid productId, UnitOfMeasure unit, decimal initialQuantity = 0m, decimal reorderLevel = 5m)
    {
        Id = Guid.NewGuid();
        ProductId = productId;
        CurrentQuantity = Quantity.From(initialQuantity, unit);
        ReorderLevel = Quantity.From(reorderLevel, unit);
        IsLowStock = CurrentQuantity.Value <= ReorderLevel.Value;
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

        IsLowStock = CurrentQuantity.Value <= ReorderLevel.Value;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateReorderLevel(Quantity newLevel)
    {
        if (newLevel.Value < 0m)
        {
            throw new DomainException("Reorder level cannot be negative.", "INVALID_REORDER_LEVEL");
        }
        if (newLevel.Unit != null && CurrentQuantity.Unit != null && newLevel.Unit.Code != CurrentQuantity.Unit.Code)
        {
            throw new DomainException($"Unit mismatch for reorder level: {newLevel.Unit.Code} vs {CurrentQuantity.Unit.Code}.", "UNIT_MISMATCH");
        }

        ReorderLevel = newLevel;
        IsLowStock = CurrentQuantity.Value <= ReorderLevel.Value;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateUnitOfMeasure(UnitOfMeasure newUnit, Quantity newReorderLevel)
    {
        ArgumentNullException.ThrowIfNull(newUnit);
        if (CurrentQuantity.Value != 0m)
        {
            throw new DomainException("Unit of measure cannot be changed when stock is non-zero.", "UNIT_IMMUTABLE");
        }
        if (newReorderLevel.Unit != null && newReorderLevel.Unit.Code != newUnit.Code)
        {
            throw new DomainException($"Unit mismatch for reorder level: {newReorderLevel.Unit.Code} vs {newUnit.Code}.", "UNIT_MISMATCH");
        }
        if (newReorderLevel.Value < 0m)
        {
            throw new DomainException("Reorder level cannot be negative.", "INVALID_REORDER_LEVEL");
        }

        CurrentQuantity = Quantity.Zero(newUnit);
        ReorderLevel = newReorderLevel;
        IsLowStock = CurrentQuantity.Value <= ReorderLevel.Value;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}