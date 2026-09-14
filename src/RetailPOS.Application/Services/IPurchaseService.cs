using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Purchases;

namespace RetailPOS.Application.Services;

public interface IPurchaseService
{
    Task<ApiResponse<PurchaseInvoiceDto>> CreatePurchaseAsync(CreatePurchaseRequest request, Guid userId, string executingUser, Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<PurchaseInvoiceDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<PurchaseInvoiceDto>> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<PurchaseInvoiceDto>>> SearchAsync(
        Guid? supplierId,
        DateTime? fromDateUtc,
        DateTime? toDateUtc,
        int? paymentStatus,
        int? status,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<ApiResponse<PurchaseCancellationResultDto>> CancelPurchaseAsync(Guid id, CancelPurchaseRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
}
