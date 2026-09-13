using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Inventory;

public sealed class StockMovement : BaseEntity<long>
{
    public Guid ProductId { get; private set; }
    public StockMovementType MovementType { get; private set; }
    public Quantity QuantityChanged { get; private set; }
    public Quantity ResultingQuantity { get; private set; }
    public string? ReferenceNumber { get; private set; }
    public string? Reason { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? TerminalId { get; private set; }

    private StockMovement() { }

    public StockMovement(
        long id,
        Guid productId,
        StockMovementType movementType,
        Quantity quantityChanged,
        Quantity resultingQuantity,
        string? referenceNumber = null,
        string? reason = null,
        Guid? userId = null,
        Guid? terminalId = null)
    {
        Id = id;
        ProductId = productId;
        MovementType = movementType;
        QuantityChanged = quantityChanged;
        ResultingQuantity = resultingQuantity;
        ReferenceNumber = referenceNumber;
        Reason = reason;
        UserId = userId;
        TerminalId = terminalId;
    }
}
