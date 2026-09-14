using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Entities.Sales;

namespace RetailPOS.Application.Persistence;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Customer?> GetByIdTrackedAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Customer?> GetByPhoneAsync(string canonicalPhone, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Customer> Items, int TotalCount)> SearchAsync(string? searchTerm, bool? isActive, int page, int pageSize, CancellationToken cancellationToken = default);
    Task AddAsync(Customer customer, CancellationToken cancellationToken = default);
    Task AddPaymentAsync(CustomerPayment payment, CancellationToken cancellationToken = default);
    Task<CustomerPayment?> GetPaymentByClientTxIdAsync(Guid clientTxId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerPayment>> GetPaymentsByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SaleInvoice>> GetSalesByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default);
}

public interface ICashSessionRepository
{
    Task<CashSession?> GetActiveSessionAsync(Guid terminalId, CancellationToken cancellationToken = default);
    Task<CashSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CashSession?> GetByIdTrackedAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddSessionAsync(CashSession session, CancellationToken cancellationToken = default);
    Task AddTransactionAsync(CashSessionTransaction tx, CancellationToken cancellationToken = default);
    Task<CashSessionTransaction?> GetTransactionByClientTxIdAsync(Guid clientTxId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CashSessionTransaction>> GetTransactionsBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
