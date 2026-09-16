using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Sales;

namespace RetailPOS.Application.Services;

public interface ISalesService
{
    Task<ApiResponse<CartCalculationDto>> CalculateCartAsync(CalculateSaleRequest request, CancellationToken cancellationToken = default);
    Task<ApiResponse<SaleInvoiceDto>> CreateSaleAsync(CreateSaleRequest request, Guid cashierId, string cashierName, Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<SaleRefundDto>> RefundSaleAsync(Guid saleId, RefundSaleRequest request, Guid authorizedByUserId, string authorizedByUserName, Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<VoidSaleResponse>> VoidSaleAsync(Guid saleId, VoidSaleRequest request, Guid cashierId, string cashierName, string sessionIdentityHash, Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<SaleInvoiceDto>> GetSaleByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<SaleInvoiceDto>> GetSaleByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<SaleSummaryDto>>> SearchSalesAsync(SaleSearchRequest request, CancellationToken cancellationToken = default);
    Task<ApiResponse<IReadOnlyList<SaleRefundDto>>> GetRefundsForSaleAsync(Guid saleId, CancellationToken cancellationToken = default);
}
