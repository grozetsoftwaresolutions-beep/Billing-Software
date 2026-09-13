namespace RetailPOS.Domain.Enums;

/// <summary>
/// Authoritative classifications of stock changes.
/// </summary>
public enum StockMovementType
{
    Sale = 1,               // -Qty
    Purchase = 2,           // +Qty
    SaleReturn = 3,         // +Qty
    PurchaseReturn = 4,     // -Qty
    AdjustmentIncrease = 5, // +Qty
    AdjustmentDecrease = 6, // -Qty
    Wastage = 7             // -Qty
}
