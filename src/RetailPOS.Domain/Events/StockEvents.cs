using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Events;

public sealed record StockDeductedEvent(
    Guid ProductId,
    Quantity DeductedQuantity,
    Quantity RemainingQuantity,
    DateTime OccurredOnUtc) : IDomainEvent;

public sealed record StockDepletedEvent(
    Guid ProductId,
    DateTime OccurredOnUtc) : IDomainEvent;
