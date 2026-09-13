using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class InventoryService : IInventoryService
{
    private readonly IProductRepository _productRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public InventoryService(
        IProductRepository productRepo,
        IInventoryRepository inventoryRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _productRepo = productRepo;
        _inventoryRepo = inventoryRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<StockMovementDto>> AdjustStockAsync(
        StockAdjustmentRequest request,
        Guid? userId,
        Guid? terminalId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.Quantity <= 0m)
            throw new DomainException("Adjustment quantity must be greater than zero.", "INVALID_ADJUSTMENT_QUANTITY");

        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new DomainException("Stock adjustment reason is mandatory.", "REASON_REQUIRED");

        var trimmedReason = request.Reason.Trim();
        if (trimmedReason.Length > 250)
            throw new DomainException("Stock adjustment reason cannot exceed 250 characters.", "REASON_TOO_LONG");

        if (request.AdjustmentType != ManualStockAdjustmentType.Increase &&
            request.AdjustmentType != ManualStockAdjustmentType.Decrease)
        {
            throw new DomainException("Manual adjustments only support AdjustmentIncrease or AdjustmentDecrease.", "INVALID_ADJUSTMENT_TYPE");
        }

        var product = await _productRepo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product == null)
            throw new DomainException("Product not found.", "PRODUCT_NOT_FOUND");

        var movementQty = Quantity.From(request.Quantity, product.Unit);
        var movementType = request.AdjustmentType == ManualStockAdjustmentType.Increase
            ? StockMovementType.AdjustmentIncrease
            : StockMovementType.AdjustmentDecrease;

        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var stock = await _inventoryRepo.GetStockBalanceTrackedAsync(request.ProductId, cancellationToken);
                if (stock == null)
                    throw new DomainException("Stock balance record not found for product.", "STOCK_BALANCE_NOT_FOUND");

                // Enforces domain stock deduction rules and throws InsufficientStockException if insufficient
                stock.ApplyMovement(movementType, movementQty, NegativeStockPolicy.Disallowed);

                var movement = new StockMovement(
                    id: 0,
                    productId: product.Id,
                    movementType: movementType,
                    quantityChanged: movementQty,
                    resultingQuantity: stock.CurrentQuantity,
                    referenceNumber: $"ADJ-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    reason: trimmedReason,
                    userId: userId,
                    terminalId: terminalId);

                await _inventoryRepo.AddStockMovementAsync(movement, cancellationToken);

                var details = $"Manual adjustment: {movementType} {movementQty.Value} {product.Unit.Code}. Reason: {trimmedReason}. Resulting: {stock.CurrentQuantity.Value}.";
                await _auditRepo.RecordAsync("Inventory", "STOCK_ADJUSTED", executingUser, details, cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                var dto = new StockMovementDto(
                    movement.Id,
                    product.Id,
                    product.Name,
                    movement.MovementType,
                    movement.QuantityChanged.Value,
                    movement.ResultingQuantity.Value,
                    product.Unit.Code,
                    movement.ReferenceNumber,
                    movement.Reason,
                    movement.CreatedAtUtc,
                    movement.UserId,
                    movement.TerminalId);

                return ApiResponse.Ok(dto);
            }
            catch (DomainException)
            {
                throw;
            }
            catch (Exception ex) when (_unitOfWork.IsTransient(ex) && attempt < maxRetries)
            {
                await Task.Delay(50 * attempt, cancellationToken);
            }
        }

        throw new DomainException("Database is busy. Please retry the adjustment.", "DATABASE_BUSY");
    }

    public async Task<ApiResponse<StockBalanceDto>> GetStockBalanceAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var product = await _productRepo.GetByIdAsync(productId, cancellationToken);
        if (product == null)
            return ApiResponse.Fail<StockBalanceDto>("Product not found.", "PRODUCT_NOT_FOUND");

        var stock = await _inventoryRepo.GetStockBalanceAsync(productId, cancellationToken);
        if (stock == null)
            return ApiResponse.Fail<StockBalanceDto>("Stock balance not found.", "STOCK_BALANCE_NOT_FOUND");

        var dto = new StockBalanceDto(
            product.Id,
            product.SKU,
            product.Name,
            stock.CurrentQuantity.Value,
            stock.ReorderLevel.Value,
            product.Unit.Code,
            stock.IsLowStock);

        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<PagedResult<StockBalanceDto>>> GetLowStockProductsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 50);

        var (lowStockList, totalCount) = await _inventoryRepo.GetLowStockAsync(clampedPage, clampedSize, cancellationToken);

        var items = new List<StockBalanceDto>(lowStockList.Count);
        foreach (var s in lowStockList)
        {
            var p = await _productRepo.GetByIdAsync(s.ProductId, cancellationToken);
            items.Add(new StockBalanceDto(
                s.ProductId,
                p?.SKU ?? "UNKNOWN",
                p?.Name ?? "Unknown Product",
                s.CurrentQuantity.Value,
                s.ReorderLevel.Value,
                s.CurrentQuantity.Unit.Code,
                s.IsLowStock));
        }

        var result = new PagedResult<StockBalanceDto>(items, totalCount, clampedPage, clampedSize);
        return ApiResponse.Ok(result);
    }

    public async Task<ApiResponse<PagedResult<StockMovementDto>>> GetProductStockHistoryAsync(
        Guid? productId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 50);

        var (movements, totalCount) = await _inventoryRepo.GetStockMovementsAsync(productId, clampedPage, clampedSize, cancellationToken);

        var items = new List<StockMovementDto>(movements.Count);
        foreach (var m in movements)
        {
            var p = await _productRepo.GetByIdAsync(m.ProductId, cancellationToken);
            items.Add(new StockMovementDto(
                m.Id,
                m.ProductId,
                p?.Name ?? "Unknown",
                m.MovementType,
                m.QuantityChanged.Value,
                m.ResultingQuantity.Value,
                m.QuantityChanged.Unit.Code,
                m.ReferenceNumber,
                m.Reason,
                m.CreatedAtUtc,
                m.UserId,
                m.TerminalId));
        }

        var result = new PagedResult<StockMovementDto>(items, totalCount, clampedPage, clampedSize);
        return ApiResponse.Ok(result);
    }
}