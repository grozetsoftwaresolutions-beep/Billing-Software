using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public class StockRulesTests
{
    private readonly Guid _productId = Guid.NewGuid();

    [Fact]
    public void PurchaseAndAdjustmentIncrease_ShouldIncreaseStock()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 10m);

        // Purchase +15 units
        stock.ApplyMovement(StockMovementType.Purchase, Quantity.From(15m, UnitOfMeasure.Piece));
        Assert.Equal(25m, stock.CurrentQuantity.Value);

        // Adjustment +5 units
        stock.ApplyMovement(StockMovementType.AdjustmentIncrease, Quantity.From(5m, UnitOfMeasure.Piece));
        Assert.Equal(30m, stock.CurrentQuantity.Value);
    }

    [Fact]
    public void SaleReturn_ShouldIncreaseStock()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 5m);
        stock.ApplyMovement(StockMovementType.SaleReturn, Quantity.From(2m, UnitOfMeasure.Piece));
        Assert.Equal(7m, stock.CurrentQuantity.Value);
    }

    [Fact]
    public void SaleAndWastage_ShouldDeductStock()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 20m);

        // Sale -5 units
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(5m, UnitOfMeasure.Piece));
        Assert.Equal(15m, stock.CurrentQuantity.Value);

        // Wastage -2 units
        stock.ApplyMovement(StockMovementType.Wastage, Quantity.From(2m, UnitOfMeasure.Piece));
        Assert.Equal(13m, stock.CurrentQuantity.Value);

        // Purchase return -3 units
        stock.ApplyMovement(StockMovementType.PurchaseReturn, Quantity.From(3m, UnitOfMeasure.Piece));
        Assert.Equal(10m, stock.CurrentQuantity.Value);
    }

    [Fact]
    public void SaleExceedingStock_WhenNegativeStockDisallowed_ShouldThrowInsufficientStockException()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 3m);

        // Attempting to sell 4 when only 3 are available
        var ex = Assert.Throws<InsufficientStockException>(() =>
            stock.ApplyMovement(StockMovementType.Sale, Quantity.From(4m, UnitOfMeasure.Piece), NegativeStockPolicy.Disallowed));

        Assert.Equal(3m, ex.AvailableQuantity);
        Assert.Equal(4m, ex.RequestedQuantity);
        Assert.Equal(3m, stock.CurrentQuantity.Value); // Stock remains unchanged
    }

    [Fact]
    public void SaleExceedingStock_WhenNegativeStockAllowed_ShouldPermitNegativeBalance()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 2m);

        // Selling 5 when 2 available, but store policy explicitly allows negative stock
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(5m, UnitOfMeasure.Piece), NegativeStockPolicy.Allowed);

        Assert.Equal(-3m, stock.CurrentQuantity.Value);
        Assert.True(stock.CurrentQuantity.IsNegative);
    }

    [Fact]
    public void LowStockAlert_ShouldTriggerWhenAtOrBelowReorderLevel()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 10m, reorderLevel: 5m);
        Assert.False(stock.IsLowStock);

        // Reduce to exactly 5 units (at reorder level)
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(5m, UnitOfMeasure.Piece));
        Assert.True(stock.IsLowStock);

        // Reduce to 2 units (below reorder level)
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(3m, UnitOfMeasure.Piece));
        Assert.True(stock.IsLowStock);
    }
}
