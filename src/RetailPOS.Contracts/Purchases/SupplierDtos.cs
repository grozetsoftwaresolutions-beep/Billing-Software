using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Purchases;

public sealed record CreateSupplierRequest(
    string Name,
    string? ContactPerson = null,
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? TaxNumber = null,
    int PaymentTermsDays = 0,
    string? Notes = null);

public sealed record UpdateSupplierRequest(
    string Name,
    string? ContactPerson = null,
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? TaxNumber = null,
    int PaymentTermsDays = 0,
    string? Notes = null);

public sealed record SupplierDto(
    Guid Id,
    string Name,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Address,
    string? TaxNumber,
    int PaymentTermsDays,
    long BalanceDuePaise,
    long CreditBalancePaise,
    bool IsActive,
    string? Notes,
    DateTime CreatedAtUtc);

public sealed record RecordSupplierPaymentRequest(
    Guid? PurchaseInvoiceId,
    long AmountPaise,
    PaymentMethod Method,
    string? ReferenceNumber,
    DateTime? PaymentDateUtc,
    bool AllowAdvancePayment,
    string? Notes,
    Guid ClientTransactionId);

public sealed record SupplierPaymentAllocationDto(
    long Id,
    Guid PurchaseInvoiceId,
    long AmountAllocatedPaise,
    DateTime CreatedAtUtc);

public sealed record SupplierPaymentDto(
    Guid Id,
    int StoreId,
    Guid SupplierId,
    SupplierPaymentType Type,
    long AmountPaise,
    PaymentMethod Method,
    string? ReferenceNumber,
    DateTime PaymentDateUtc,
    string? Notes,
    Guid ClientTransactionId,
    Guid CreatedByUserId,
    DateTime CreatedAtUtc,
    IReadOnlyList<SupplierPaymentAllocationDto> Allocations);
