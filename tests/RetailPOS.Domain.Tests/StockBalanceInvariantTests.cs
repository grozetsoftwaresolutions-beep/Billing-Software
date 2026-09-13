using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public class StockBalanceInvariantTests
{
    private readonly Guid _productId = Guid.NewGuid();

    [Fact]
    public void StockBalance_InitialState_CorrectlyCalculatesIsLowStock()
    {
        // Initial stock 10 > reorder 5 -> Not low stock
        var stockAbove = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 10m, reorderLevel: 5m);
        Assert.False(stockAbove.IsLowStock);

        // Initial stock 5 == reorder 5 -> Low stock (boundary condition <=)
        var stockEqual = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 5m, reorderLevel: 5m);
        Assert.True(stockEqual.IsLowStock);

        // Initial stock 3 < reorder 5 -> Low stock
        var stockBelow = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 3m, reorderLevel: 5m);
        Assert.True(stockBelow.IsLowStock);
    }

    [Fact]
    public void StockBalance_ApplyMovement_DeductionAcrossThreshold_UpdatesIsLowStock()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 10m, reorderLevel: 5m);
        Assert.False(stock.IsLowStock);

        // Deduct 4 -> Remaining 6 > 5 -> IsLowStock == false
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(4m, UnitOfMeasure.Piece));
        Assert.Equal(6m, stock.CurrentQuantity.Value);
        Assert.False(stock.IsLowStock);

        // Deduct 1 -> Remaining 5 == 5 -> IsLowStock == true
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(1m, UnitOfMeasure.Piece));
        Assert.Equal(5m, stock.CurrentQuantity.Value);
        Assert.True(stock.IsLowStock);

        // Deduct 2 -> Remaining 3 < 5 -> IsLowStock == true
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(2m, UnitOfMeasure.Piece));
        Assert.Equal(3m, stock.CurrentQuantity.Value);
        Assert.True(stock.IsLowStock);

        // Increase 5 -> Remaining 8 > 5 -> IsLowStock == false
        stock.ApplyMovement(StockMovementType.AdjustmentIncrease, Quantity.From(5m, UnitOfMeasure.Piece));
        Assert.Equal(8m, stock.CurrentQuantity.Value);
        Assert.False(stock.IsLowStock);
    }

    [Fact]
    public void StockBalance_WeightedItem_PrecisionAndThresholdTransitions()
    {
        // 1.250 KG initial, 1.000 KG reorder level
        var stock = new StockBalance(_productId, UnitOfMeasure.Kilogram, initialQuantity: 1.250m, reorderLevel: 1.000m);
        Assert.False(stock.IsLowStock);

        // Deduct 0.250 KG -> 1.000 KG remaining -> Low stock
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(0.250m, UnitOfMeasure.Kilogram));
        Assert.Equal(1.000m, stock.CurrentQuantity.Value);
        Assert.True(stock.IsLowStock);

        // Deduct 0.001 KG -> 0.999 KG remaining -> Low stock
        stock.ApplyMovement(StockMovementType.Sale, Quantity.From(0.001m, UnitOfMeasure.Kilogram));
        Assert.Equal(0.999m, stock.CurrentQuantity.Value);
        Assert.True(stock.IsLowStock);

        // Restock 1.000 KG -> 1.999 KG -> Not low stock
        stock.ApplyMovement(StockMovementType.Purchase, Quantity.From(1.000m, UnitOfMeasure.Kilogram));
        Assert.Equal(1.999m, stock.CurrentQuantity.Value);
        Assert.False(stock.IsLowStock);
    }

    [Fact]
    public void StockBalance_UpdateReorderLevel_DynamicallyUpdatesIsLowStock()
    {
        var stock = new StockBalance(_productId, UnitOfMeasure.Piece, initialQuantity: 10m, reorderLevel: 5m);
        Assert.False(stock.IsLowStock);

        // Raise reorder level to 12 -> 10 <= 12 -> IsLowStock == true
        stock.UpdateReorderLevel(Quantity.From(12m, UnitOfMeasure.Piece));
        Assert.True(stock.IsLowStock);

        // Lower reorder level to 8 -> 10 > 8 -> IsLowStock == false
        stock.UpdateReorderLevel(Quantity.From(8m, UnitOfMeasure.Piece));
        Assert.False(stock.IsLowStock);

        // Negative reorder level rejected
        Assert.Throws<DomainException>(() =>
            stock.UpdateReorderLevel(new Quantity(-1m, UnitOfMeasure.Piece)));

        // Mismatched unit rejected
        Assert.Throws<DomainException>(() =>
            stock.UpdateReorderLevel(Quantity.From(10m, UnitOfMeasure.Kilogram)));
    }
}