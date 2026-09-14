using System.Globalization;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class SupplierService : ISupplierService
{
    private readonly ISupplierRepository _supplierRepo;
    private readonly IPurchaseRepository _purchaseRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public SupplierService(
        ISupplierRepository supplierRepo,
        IPurchaseRepository purchaseRepo,
        IStoreRepository storeRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _supplierRepo = supplierRepo;
        _purchaseRepo = purchaseRepo;
        _storeRepo = storeRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<SupplierDto>> CreateSupplierAsync(
        CreateSupplierRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return ApiResponse.Fail<SupplierDto>("Supplier name cannot be empty.", "INVALID_SUPPLIER_NAME");

        var trimmedName = request.Name.Trim();
        if (await _supplierRepo.ExistsByNameAsync(trimmedName, null, cancellationToken))
            return ApiResponse.Fail<SupplierDto>($"Supplier with name '{trimmedName}' already exists.", "DUPLICATE_SUPPLIER_NAME");

        var supplier = new Supplier(
            Guid.NewGuid(),
            trimmedName,
            request.ContactPerson,
            request.Phone,
            request.Email,
            request.Address,
            request.TaxNumber,
            request.PaymentTermsDays,
            request.Notes);

        await _supplierRepo.AddAsync(supplier, cancellationToken);
        await _auditRepo.RecordAsync("Purchases", "SUPPLIER_CREATED", executingUser, $"Created supplier '{supplier.Name}'.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(supplier));
    }

    public async Task<ApiResponse<SupplierDto>> UpdateSupplierAsync(
        Guid id,
        UpdateSupplierRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        var supplier = await _supplierRepo.GetByIdAsync(id, cancellationToken);
        if (supplier == null)
            return ApiResponse.Fail<SupplierDto>("Supplier not found.", "SUPPLIER_NOT_FOUND");

        if (string.IsNullOrWhiteSpace(request.Name))
            return ApiResponse.Fail<SupplierDto>("Supplier name cannot be empty.", "INVALID_SUPPLIER_NAME");

        var trimmedName = request.Name.Trim();
        if (await _supplierRepo.ExistsByNameAsync(trimmedName, id, cancellationToken))
            return ApiResponse.Fail<SupplierDto>($"Supplier with name '{trimmedName}' already exists.", "DUPLICATE_SUPPLIER_NAME");

        supplier.UpdateProfile(
            trimmedName,
            request.ContactPerson,
            request.Phone,
            request.Email,
            request.Address,
            request.TaxNumber,
            request.PaymentTermsDays,
            request.Notes);

        await _auditRepo.RecordAsync("Purchases", "SUPPLIER_UPDATED", executingUser, $"Updated supplier '{supplier.Name}'.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(supplier));
    }

    public async Task<ApiResponse<SupplierDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var supplier = await _supplierRepo.GetByIdAsync(id, cancellationToken);
        if (supplier == null)
            return ApiResponse.Fail<SupplierDto>("Supplier not found.", "SUPPLIER_NOT_FOUND");

        return ApiResponse.Ok(MapToDto(supplier));
    }

    public async Task<ApiResponse<PagedResult<SupplierDto>>> SearchAsync(
        string? searchTerm,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var (items, totalCount) = await _supplierRepo.SearchAsync(searchTerm, isActive, clampedPage, clampedSize, cancellationToken);
        var dtos = items.Select(MapToDto).ToList();
        return ApiResponse.Ok(new PagedResult<SupplierDto>(dtos, totalCount, clampedPage, clampedSize));
    }

    public async Task<ApiResponse<bool>> DeactivateAsync(Guid id, string executingUser, CancellationToken cancellationToken = default)
    {
        var supplier = await _supplierRepo.GetByIdAsync(id, cancellationToken);
        if (supplier == null)
            return ApiResponse.Fail<bool>("Supplier not found.", "SUPPLIER_NOT_FOUND");

        supplier.Deactivate();
        await _auditRepo.RecordAsync("Purchases", "SUPPLIER_DEACTIVATED", executingUser, $"Deactivated supplier '{supplier.Name}'.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(true);
    }

    public async Task<ApiResponse<bool>> ActivateAsync(Guid id, string executingUser, CancellationToken cancellationToken = default)
    {
        var supplier = await _supplierRepo.GetByIdAsync(id, cancellationToken);
        if (supplier == null)
            return ApiResponse.Fail<bool>("Supplier not found.", "SUPPLIER_NOT_FOUND");

        supplier.Activate();
        await _auditRepo.RecordAsync("Purchases", "SUPPLIER_ACTIVATED", executingUser, $"Activated supplier '{supplier.Name}'.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(true);
    }

    public async Task<ApiResponse<SupplierPaymentDto>> RecordPaymentAsync(
        Guid supplierId,
        RecordSupplierPaymentRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientTransactionId == Guid.Empty)
            return ApiResponse.Fail<SupplierPaymentDto>("ClientTransactionId is mandatory.", "CLIENT_TX_REQUIRED");

        if (request.AmountPaise <= 0)
            return ApiResponse.Fail<SupplierPaymentDto>("Payment amount must be greater than zero.", "INVALID_PAYMENT_AMOUNT");

        if (request.Method == PaymentMethod.Card || request.Method == PaymentMethod.Mixed || request.Method == PaymentMethod.Credit)
            return ApiResponse.Fail<SupplierPaymentDto>($"Payment method '{request.Method}' is not permitted for supplier transactions.", "INVALID_SUPPLIER_PAYMENT_METHOD");

        if (request.Method != PaymentMethod.Cash && string.IsNullOrWhiteSpace(request.ReferenceNumber))
            return ApiResponse.Fail<SupplierPaymentDto>($"Reference number is mandatory for {request.Method} supplier payments.", "PAYMENT_REFERENCE_REQUIRED");

        // Idempotency check before transaction
        var existingPayment = await _supplierRepo.GetPaymentByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
        if (existingPayment != null)
            return ApiResponse.Ok(MapPaymentToDto(existingPayment));

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _unitOfWork.ResetChangeTracker();

            existingPayment = await _supplierRepo.GetPaymentByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
            if (existingPayment != null)
                return ApiResponse.Ok(MapPaymentToDto(existingPayment));

            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var store = await _storeRepo.GetStoreAsync(cancellationToken);
                if (store == null)
                    throw new DomainException("Store configuration not found.", "STORE_CONFIG_NOT_FOUND");

                var supplier = await _supplierRepo.GetByIdAsync(supplierId, cancellationToken);
                if (supplier == null)
                    throw new DomainException("Supplier not found.", "SUPPLIER_NOT_FOUND");

                if (!supplier.IsActive)
                    throw new DomainException("Cannot record payment for an inactive supplier.", "SUPPLIER_INACTIVE");

                var paymentAmount = Money.FromMinorUnits(request.AmountPaise, Currency.INR);
                var paymentDate = request.PaymentDateUtc ?? DateTime.UtcNow;

                var payment = new SupplierPayment(
                    Guid.NewGuid(),
                    store.Id,
                    supplier.Id,
                    SupplierPaymentType.Payment,
                    paymentAmount,
                    request.Method,
                    request.ReferenceNumber,
                    paymentDate,
                    request.ClientTransactionId,
                    userId,
                    request.Notes);

                if (request.PurchaseInvoiceId.HasValue)
                {
                    // Invoice-specific payment
                    var invoice = await _purchaseRepo.GetByIdAsync(request.PurchaseInvoiceId.Value, cancellationToken);
                    if (invoice == null || invoice.SupplierId != supplier.Id)
                        throw new DomainException("Purchase invoice not found for this supplier.", "PURCHASE_INVOICE_NOT_FOUND");

                    if (invoice.Status == PurchaseStatus.Cancelled)
                        throw new DomainException("Cannot apply payment to a cancelled purchase invoice.", "INVOICE_CANCELLED");

                    if (paymentAmount > invoice.BalanceDue)
                        throw new DomainException(
                            string.Format(CultureInfo.InvariantCulture, "Payment amount of {0} paise exceeds invoice balance due of {1} paise.", paymentAmount.MinorUnits, invoice.BalanceDue.MinorUnits),
                            "PAYMENT_EXCEEDS_INVOICE_BALANCE");

                    invoice.RecordPayment(paymentAmount);
                    supplier.DecreaseBalanceDue(paymentAmount);

                    var alloc = new SupplierPaymentAllocation(0, payment.Id, invoice.Id, paymentAmount);
                    payment.AddAllocation(alloc);
                }
                else
                {
                    // FIFO Auto-allocation
                    var openInvoices = await _purchaseRepo.GetOpenInvoicesBySupplierIdAsync(supplier.Id, cancellationToken);
                    var totalOutstanding = Money.FromMinorUnits(Math.Max(supplier.BalanceDue.MinorUnits, openInvoices.Sum(i => i.BalanceDue.MinorUnits)), Currency.INR);

                    if (paymentAmount > totalOutstanding && !request.AllowAdvancePayment)
                    {
                        throw new DomainException(
                            string.Format(CultureInfo.InvariantCulture, "Payment amount {0} paise exceeds total outstanding balance {1} paise without advance authorization.", paymentAmount.MinorUnits, totalOutstanding.MinorUnits),
                            "PAYMENT_EXCEEDS_TOTAL_OUTSTANDING");
                    }

                    var remainingToAllocate = paymentAmount;
                    foreach (var invoice in openInvoices)
                    {
                        if (remainingToAllocate.IsZero)
                            break;

                        var allocAmount = Money.FromMinorUnits(Math.Min(remainingToAllocate.MinorUnits, invoice.BalanceDue.MinorUnits), Currency.INR);
                        invoice.RecordPayment(allocAmount);
                        supplier.DecreaseBalanceDue(allocAmount);

                        var alloc = new SupplierPaymentAllocation(0, payment.Id, invoice.Id, allocAmount);
                        payment.AddAllocation(alloc);

                        remainingToAllocate -= allocAmount;
                    }

                    // If supplier has remaining BalanceDue not tied to open invoices, apply to supplier BalanceDue
                    if (!remainingToAllocate.IsZero && supplier.BalanceDue > Money.Zero())
                    {
                        var directReduction = Money.FromMinorUnits(Math.Min(remainingToAllocate.MinorUnits, supplier.BalanceDue.MinorUnits), Currency.INR);
                        supplier.DecreaseBalanceDue(directReduction);
                        remainingToAllocate -= directReduction;
                    }

                    // Any excess amount becomes unallocated credit balance
                    if (!remainingToAllocate.IsZero)
                    {
                        supplier.IncreaseCreditBalance(remainingToAllocate);
                    }
                }

                await _supplierRepo.AddPaymentAsync(payment, cancellationToken);
                await _auditRepo.RecordAsync(
                    "Purchases",
                    "SUPPLIER_PAYMENT_RECORDED",
                    executingUser,
                    string.Format(CultureInfo.InvariantCulture, "Recorded {0} payment of {1} paise to supplier '{2}'.", request.Method, request.AmountPaise, supplier.Name),
                    cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return ApiResponse.Ok(MapPaymentToDto(payment));
            }
            catch (DomainException)
            {
                throw;
            }
            catch (Exception ex) when (_unitOfWork.IsTransient(ex))
            {
                if (attempt == maxRetries)
                    throw;

                await Task.Delay(Random.Shared.Next(50, 150) * attempt, cancellationToken);
            }
        }

        return ApiResponse.Fail<SupplierPaymentDto>("Failed to record supplier payment after retries.", "TRANSIENT_FAILURE");
    }

    public async Task<ApiResponse<IReadOnlyList<SupplierPaymentDto>>> GetPaymentsAsync(
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var payments = await _supplierRepo.GetPaymentsBySupplierIdAsync(supplierId, cancellationToken);
        var dtos = payments.Select(MapPaymentToDto).ToList();
        return ApiResponse.Ok<IReadOnlyList<SupplierPaymentDto>>(dtos);
    }

    private static SupplierDto MapToDto(Supplier s) =>
        new(
            s.Id,
            s.Name,
            s.ContactPerson,
            s.Phone,
            s.Email,
            s.Address,
            s.TaxNumber,
            s.PaymentTermsDays,
            s.BalanceDue.MinorUnits,
            s.CreditBalance.MinorUnits,
            s.IsActive,
            s.Notes,
            s.CreatedAtUtc);

    private static SupplierPaymentDto MapPaymentToDto(SupplierPayment p) =>
        new(
            p.Id,
            p.StoreId,
            p.SupplierId,
            p.Type,
            p.Amount.MinorUnits,
            p.Method,
            p.ReferenceNumber,
            p.PaymentDateUtc,
            p.Notes,
            p.ClientTransactionId,
            p.CreatedByUserId,
            p.CreatedAtUtc,
            p.Allocations.Select(a => new SupplierPaymentAllocationDto(a.Id, a.PurchaseInvoiceId, a.AmountAllocated.MinorUnits, a.CreatedAtUtc)).ToList());
}
