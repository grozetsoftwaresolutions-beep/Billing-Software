using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class StockMovementViewModel : ObservableObject
{
    public StockMovementDto Dto { get; }

    public long Id => Dto.Id;
    public Guid ProductId => Dto.ProductId;
    public string ProductName => Dto.ProductName;
    public StockMovementType MovementType => Dto.MovementType;
    public decimal QuantityChanged => Dto.QuantityChanged;
    public decimal ResultingQuantity => Dto.ResultingQuantity;
    public string UnitCode => Dto.UnitCode;
    public string? ReferenceNumber => Dto.ReferenceNumber;
    public string? Reason => Dto.Reason;
    public DateTime CreatedAtUtc => Dto.CreatedAtUtc;
    public Guid? UserId => Dto.UserId;
    public Guid? TerminalId => Dto.TerminalId;

    public string FormattedTimestamp => CreatedAtUtc.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    public string FormattedQuantityChanged
    {
        get
        {
            var formatted = ProductItemViewModel.FormatQuantity(Math.Abs(QuantityChanged), UnitCode);
            return QuantityChanged >= 0 ? $"+{formatted}" : $"-{formatted}";
        }
    }

    public string FormattedResultingQuantity => ProductItemViewModel.FormatQuantity(ResultingQuantity, UnitCode);

    public string MovementTypeName => MovementType switch
    {
        StockMovementType.Sale => "Sale",
        StockMovementType.Purchase => "Purchase",
        StockMovementType.SaleReturn => "Sale Return",
        StockMovementType.PurchaseReturn => "Purchase Return",
        StockMovementType.AdjustmentIncrease => "Adj Increase",
        StockMovementType.AdjustmentDecrease => "Adj Decrease",
        StockMovementType.Wastage => "Wastage",
        _ => MovementType.ToString()
    };

    public string MovementTypeBadgeColor => MovementType switch
    {
        StockMovementType.Sale => "#2563EB",
        StockMovementType.Purchase or StockMovementType.AdjustmentIncrease => "#16A34A",
        StockMovementType.AdjustmentDecrease or StockMovementType.Wastage => "#DC2626",
        _ => "#64748B"
    };

    public StockMovementViewModel(StockMovementDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Dto = dto;
    }
}
