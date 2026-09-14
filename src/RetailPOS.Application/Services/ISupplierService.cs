using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Purchases;

namespace RetailPOS.Application.Services;

public interface ISupplierService
{
    Task<ApiResponse<SupplierDto>> CreateSupplierAsync(CreateSupplierRequest request, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<SupplierDto>> UpdateSupplierAsync(Guid id, UpdateSupplierRequest request, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<SupplierDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<SupplierDto>>> SearchAsync(string? searchTerm, bool? isActive, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ApiResponse<bool>> DeactivateAsync(Guid id, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<bool>> ActivateAsync(Guid id, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<SupplierPaymentDto>> RecordPaymentAsync(Guid supplierId, RecordSupplierPaymentRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<IReadOnlyList<SupplierPaymentDto>>> GetPaymentsAsync(Guid supplierId, CancellationToken cancellationToken = default);
}
