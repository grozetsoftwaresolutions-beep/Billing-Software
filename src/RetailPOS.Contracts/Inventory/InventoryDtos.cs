using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Inventory;

public enum ManualStockAdjustmentType
{
    Increase = 1,
    Decrease = 2
}

public sealed record StockAdjustmentRequest(
    Guid ProductId,
    ManualStockAdjustmentType AdjustmentType,
    decimal Quantity,
    string Reason);

public sealed record StockBalanceDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    decimal CurrentStock,
    decimal ReorderLevel,
    string UnitCode,
    bool IsLowStock);

public sealed record StockMovementDto(
    long Id,
    Guid ProductId,
    string ProductName,
    StockMovementType MovementType,
    decimal QuantityChanged,
    decimal ResultingQuantity,
    string UnitCode,
    string? ReferenceNumber,
    string? Reason,
    DateTime CreatedAtUtc,
    Guid? UserId,
    Guid? TerminalId);
