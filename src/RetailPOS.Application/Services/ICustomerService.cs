using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Sales;

namespace RetailPOS.Application.Services;

public interface ICustomerService
{
    Task<ApiResponse<CustomerDto>> CreateCustomerAsync(CreateCustomerRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CustomerDto>> UpdateCustomerAsync(Guid id, UpdateCustomerRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CustomerDto>> UpdateCreditLimitAsync(Guid id, UpdateCreditLimitRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CustomerDto>> SetStatusAsync(Guid id, SetCustomerStatusRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CustomerDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<CustomerDto>> GetByPhoneAsync(string phone, CancellationToken cancellationToken = default);
    Task<ApiResponse<PagedResult<CustomerDto>>> SearchAsync(string? searchTerm, bool? isActive, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ApiResponse<CustomerPaymentDto>> RecordPaymentAsync(Guid customerId, RecordCustomerPaymentRequest request, Guid userId, string executingUser, Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<IReadOnlyList<CustomerPaymentDto>>> GetPaymentsAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task<ApiResponse<IReadOnlyList<CustomerLedgerEntryDto>>> GetLedgerAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task<ApiResponse<IReadOnlyList<SaleInvoiceDto>>> GetSalesAsync(Guid customerId, CancellationToken cancellationToken = default);
}
