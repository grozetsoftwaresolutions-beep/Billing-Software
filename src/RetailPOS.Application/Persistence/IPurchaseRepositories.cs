using RetailPOS.Domain.Entities.Purchases;

namespace RetailPOS.Application.Persistence;

public interface ISupplierRepository
{
    Task<Supplier?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Supplier?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> ExistsByNameAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Supplier> Items, int TotalCount)> SearchAsync(string? searchTerm, bool? isActive, int page, int pageSize, CancellationToken cancellationToken = default);
    Task AddAsync(Supplier supplier, CancellationToken cancellationToken = default);
    Task<SupplierPayment?> GetPaymentByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default);
    Task AddPaymentAsync(SupplierPayment payment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupplierPayment>> GetPaymentsBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default);
    Task<SupplierPayment?> GetPaymentByIdAsync(Guid paymentId, CancellationToken cancellationToken = default);
}

public interface IPurchaseRepository
{
    Task<PurchaseInvoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PurchaseInvoice?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default);
    Task<PurchaseInvoice?> GetBySupplierInvoiceNumberAsync(Guid supplierId, string supplierInvoiceNumber, CancellationToken cancellationToken = default);
    Task<PurchaseInvoice?> GetByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default);
    Task<PurchaseInvoice?> GetByCancellationClientTransactionIdAsync(Guid cancellationClientTransactionId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<PurchaseInvoice> Items, int TotalCount)> SearchAsync(
        Guid? supplierId,
        DateTime? fromDateUtc,
        DateTime? toDateUtc,
        int? paymentStatus,
        int? status,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PurchaseInvoice>> GetOpenInvoicesBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default);
    Task AddAsync(PurchaseInvoice purchase, CancellationToken cancellationToken = default);
}
