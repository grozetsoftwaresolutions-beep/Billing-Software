using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;

namespace RetailPOS.Application.Services;

public interface IInventoryService
{
    Task<ApiResponse<StockMovementDto>> AdjustStockAsync(
        StockAdjustmentRequest request,
        Guid? userId,
        Guid? terminalId,
        string executingUser,
        CancellationToken cancellationToken = default);

    Task<ApiResponse<StockBalanceDto>> GetStockBalanceAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<StockBalanceDto>>> GetLowStockProductsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<StockMovementDto>>> GetProductStockHistoryAsync(Guid? productId, int page, int pageSize, CancellationToken cancellationToken = default);
}
