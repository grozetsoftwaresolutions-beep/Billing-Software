using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Entities.Sales;

namespace RetailPOS.Application.Persistence;

public interface ISaleRepository
{
    Task<SaleInvoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SaleInvoice?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default);
    Task<SaleInvoice?> GetByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<SaleInvoice> Items, int TotalCount)> SearchAsync(SaleSearchRequest request, CancellationToken cancellationToken = default);
    Task AddAsync(SaleInvoice sale, CancellationToken cancellationToken = default);
    Task UpdateAsync(SaleInvoice sale, CancellationToken cancellationToken = default);
    Task<int> IncrementReprintCountAsync(Guid saleInvoiceId, DateTime reprintedAtUtc, CancellationToken cancellationToken = default);
}

public interface ISaleRefundRepository
{
    Task AddRefundAsync(SaleRefund refund, CancellationToken cancellationToken = default);
    Task<SaleRefund?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SaleRefund?> GetByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SaleRefund>> GetRefundsBySaleIdAsync(Guid saleId, CancellationToken cancellationToken = default);
    Task<decimal> GetRefundedQuantityForSaleItemAsync(long saleItemId, CancellationToken cancellationToken = default);
    Task<long> GetTotalRefundedAmountForSaleAsync(Guid saleId, CancellationToken cancellationToken = default);
}

public interface IInvoiceSequenceRepository
{
    Task<string> GetNextSequenceNumberAsync(string sequenceName, string prefix, string periodDate, CancellationToken cancellationToken = default);
}
