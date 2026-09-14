using System.Globalization;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class PurchaseService : IPurchaseService
{
    private readonly IPurchaseRepository _purchaseRepo;
    private readonly ISupplierRepository _supplierRepo;
    private readonly IProductRepository _productRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly IInvoiceSequenceRepository _sequenceRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public PurchaseService(
        IPurchaseRepository purchaseRepo,
        ISupplierRepository supplierRepo,
        IProductRepository productRepo,
        IInventoryRepository inventoryRepo,
        IStoreRepository storeRepo,
        IInvoiceSequenceRepository sequenceRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _purchaseRepo = purchaseRepo;
        _supplierRepo = supplierRepo;
        _productRepo = productRepo;
        _inventoryRepo = inventoryRepo;
        _storeRepo = storeRepo;
        _sequenceRepo = sequenceRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<PurchaseInvoiceDto>> CreatePurchaseAsync(
        CreatePurchaseRequest request,
        Guid userId,
        string executingUser,
        Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientTransactionId == Guid.Empty)
            return ApiResponse.Fail<PurchaseInvoiceDto>("ClientTransactionId is mandatory.", "CLIENT_TX_REQUIRED");

        if (string.IsNullOrWhiteSpace(request.SupplierInvoiceNumber))
            return ApiResponse.Fail<PurchaseInvoiceDto>("Supplier invoice number cannot be empty.", "INVALID_SUPPLIER_INVOICE");

        if (request.Items == null || request.Items.Count == 0)
            return ApiResponse.Fail<PurchaseInvoiceDto>("Purchase must contain at least one item.", "EMPTY_PURCHASE_ITEMS");

        // Pre-check idempotency
        var existing = await _purchaseRepo.GetByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
        if (existing != null)
        {
            var sup = await _supplierRepo.GetByIdAsync(existing.SupplierId, cancellationToken);
            return ApiResponse.Ok(MapToDto(existing, sup?.Name ?? "Unknown"));
        }

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _unitOfWork.ResetChangeTracker();

            existing = await _purchaseRepo.GetByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
            if (existing != null)
            {
                var sup = await _supplierRepo.GetByIdAsync(existing.SupplierId, cancellationToken);
                return ApiResponse.Ok(MapToDto(existing, sup?.Name ?? "Unknown"));
            }

            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var store = await _storeRepo.GetStoreAsync(cancellationToken);
                if (store == null)
                    throw new DomainException("Store configuration not found.", "STORE_CONFIG_NOT_FOUND");

                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(store.TimeZoneId);
                var storeNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
                var storeLocalDate = DateOnly.FromDateTime(storeNow);
                var periodDate = storeNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                if (request.InvoiceDate > storeLocalDate)
                    throw new DomainException("Invoice date cannot be in the future.", "INVALID_INVOICE_DATE");

                if (request.InvoiceDate < storeLocalDate.AddDays(-90))
                    throw new DomainException("Invoice date cannot be older than 90 days.", "INVOICE_DATE_TOO_OLD");

                var trimmedSupplierInvoice = request.SupplierInvoiceNumber.Trim();
                var duplicateBill = await _purchaseRepo.GetBySupplierInvoiceNumberAsync(request.SupplierId, trimmedSupplierInvoice, cancellationToken);
                if (duplicateBill != null)
                    throw new DomainException($"Supplier invoice '{trimmedSupplierInvoice}' has already been inwarded.", "DUPLICATE_SUPPLIER_INVOICE");

                var supplier = await _supplierRepo.GetByIdAsync(request.SupplierId, cancellationToken);
                if (supplier == null)
                    throw new DomainException("Supplier not found.", "SUPPLIER_NOT_FOUND");

                if (!supplier.IsActive)
                    throw new DomainException("Cannot inward purchases for an inactive supplier.", "SUPPLIER_INACTIVE");

                // Allocate invoice number inside transaction
                var invoiceNumber = await _sequenceRepo.GetNextSequenceNumberAsync("PURCHASE_INVOICE", "PUR", periodDate, cancellationToken);
                var invoiceId = Guid.NewGuid();

                long subtotalPaise = 0;
                long totalTaxPaise = 0;
                var domainItems = new List<PurchaseItem>(request.Items.Count);

                foreach (var itemReq in request.Items)
                {
                    if (itemReq.Quantity <= 0m)
                        throw new DomainException("Purchase item quantity must be positive.", "INVALID_QUANTITY");

                    if (itemReq.UnitCostPaise < 0)
                        throw new DomainException("Purchase item unit cost cannot be negative.", "INVALID_UNIT_COST");

                    if (itemReq.ExpiryDate.HasValue && itemReq.ExpiryDate.Value <= storeLocalDate)
                        throw new DomainException("Expired products cannot be inwarded.", "EXPIRED_PRODUCT_CANNOT_BE_INWARDED");

                    var product = await _productRepo.GetByIdAsync(itemReq.ProductId, cancellationToken);
                    if (product == null)
                        throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Product with ID '{0}' was not found.", itemReq.ProductId), "PRODUCT_NOT_FOUND");

                    if (!product.IsActive)
                        throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Product '{0}' is inactive.", product.Name), "PRODUCT_INACTIVE");

                    var unitCostMoney = Money.FromMinorUnits(itemReq.UnitCostPaise, Currency.INR);
                    var qtyVo = Quantity.From(itemReq.Quantity, product.Unit);

                    // Server-authoritative tax calculation
                    long lineBaseCostPaise = (long)Math.Round(itemReq.Quantity * itemReq.UnitCostPaise, MidpointRounding.AwayFromZero);
                    long lineTaxPaise = 0;
                    decimal effectiveTaxRate = 0m;
                    var componentEntities = new List<PurchaseItemTaxComponent>();

                    if (itemReq.TaxComponents != null && itemReq.TaxComponents.Count > 0)
                    {
                        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var tc in itemReq.TaxComponents)
                        {
                            if (string.IsNullOrWhiteSpace(tc.ComponentName))
                                throw new DomainException("Tax component name cannot be empty.", "INVALID_TAX_COMPONENT_NAME");

                            var compName = tc.ComponentName.Trim().ToUpperInvariant();
                            if (!seenNames.Add(compName))
                                throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Duplicate tax component '{0}' on item.", compName), "DUPLICATE_TAX_COMPONENT");

                            if (tc.RatePercentage < 0m || tc.RatePercentage > 100m)
                                throw new DomainException("Tax rate percentage must be between 0 and 100.", "INVALID_TAX_RATE");

                            long compTaxPaise = (long)Math.Round((decimal)lineBaseCostPaise * (tc.RatePercentage / 100m), MidpointRounding.AwayFromZero);
                            lineTaxPaise += compTaxPaise;
                            effectiveTaxRate += tc.RatePercentage;

                            var compEntity = new PurchaseItemTaxComponent(0, 0, compName, tc.RatePercentage, Money.FromMinorUnits(compTaxPaise, Currency.INR));
                            componentEntities.Add(compEntity);
                        }
                    }

                    long lineTotalPaise = lineBaseCostPaise + lineTaxPaise;
                    subtotalPaise += lineBaseCostPaise;
                    totalTaxPaise += lineTaxPaise;

                    var purchaseItem = new PurchaseItem(
                        id: 0,
                        purchaseId: invoiceId,
                        productId: product.Id,
                        productSku: product.SKU,
                        productName: product.Name,
                        unitOfMeasure: product.Unit.Code,
                        quantity: qtyVo,
                        unitCost: unitCostMoney,
                        taxRatePercentage: effectiveTaxRate,
                        taxAmount: Money.FromMinorUnits(lineTaxPaise, Currency.INR),
                        lineTotal: Money.FromMinorUnits(lineTotalPaise, Currency.INR),
                        batchNumber: itemReq.BatchNumber,
                        expiryDate: itemReq.ExpiryDate);

                    foreach (var tc in componentEntities)
                    {
                        purchaseItem.AddTaxComponent(tc);
                    }

                    domainItems.Add(purchaseItem);

                    // Atomic stock increase
                    var stock = await _inventoryRepo.GetStockBalanceTrackedAsync(product.Id, cancellationToken);
                    if (stock == null)
                        throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Stock balance not found for product '{0}'.", product.Name), "STOCK_BALANCE_NOT_FOUND");

                    stock.ApplyMovement(StockMovementType.Purchase, qtyVo, store.NegativeStockPolicy);

                    var movement = new StockMovement(
                        id: 0,
                        productId: product.Id,
                        movementType: StockMovementType.Purchase,
                        quantityChanged: qtyVo,
                        resultingQuantity: stock.CurrentQuantity,
                        referenceNumber: invoiceNumber,
                        reason: string.Format(CultureInfo.InvariantCulture, "Purchase from {0} (Bill #{1})", supplier.Name, trimmedSupplierInvoice),
                        userId: userId,
                        terminalId: terminalId);

                    await _inventoryRepo.AddStockMovementAsync(movement, cancellationToken);

                    // Master Cost Price update
                    if (itemReq.UpdateMasterCostPrice && unitCostMoney != product.CostPrice)
                    {
                        var oldCost = product.CostPrice;
                        product.UpdatePrices(newCostPrice: unitCostMoney, newSellingPrice: product.SellingPrice);
                        await _auditRepo.RecordAsync(
                            "Catalog",
                            "PRODUCT_COST_UPDATED",
                            executingUser,
                            string.Format(CultureInfo.InvariantCulture, "Product {0} cost price updated from {1} to {2} paise via purchase receipt {3}.", product.SKU, oldCost.MinorUnits, unitCostMoney.MinorUnits, invoiceNumber),
                            cancellationToken);
                    }
                }

                long grandTotalPaise = subtotalPaise + totalTaxPaise;
                var grandTotalMoney = Money.FromMinorUnits(grandTotalPaise, Currency.INR);

                // Automatic CreditBalance offset
                long creditOffsetPaise = 0;
                if (supplier.CreditBalance.IsPositive)
                {
                    creditOffsetPaise = Math.Min(supplier.CreditBalance.MinorUnits, grandTotalPaise);
                    supplier.DecreaseCreditBalance(Money.FromMinorUnits(creditOffsetPaise, Currency.INR));
                }

                long remainingAfterCredit = grandTotalPaise - creditOffsetPaise;
                long initialPaidPaise = request.InitialPaymentAmountPaise;

                if (initialPaidPaise < 0)
                    throw new DomainException("Initial payment amount cannot be negative.", "INVALID_PAYMENT_AMOUNT");

                if (initialPaidPaise > remainingAfterCredit)
                    throw new DomainException(
                        string.Format(CultureInfo.InvariantCulture, "Initial payment {0} paise exceeds remaining invoice balance {1} paise.", initialPaidPaise, remainingAfterCredit),
                        "INITIAL_PAYMENT_EXCEEDS_BALANCE");

                if (initialPaidPaise > 0)
                {
                    if (!request.InitialPaymentMethod.HasValue)
                        throw new DomainException("Payment method is required when initial payment is greater than zero.", "PAYMENT_METHOD_REQUIRED");

                    var pMethod = request.InitialPaymentMethod.Value;
                    if (pMethod == PaymentMethod.Card || pMethod == PaymentMethod.Mixed || pMethod == PaymentMethod.Credit)
                        throw new DomainException($"Payment method '{pMethod}' is not permitted for supplier transactions.", "INVALID_SUPPLIER_PAYMENT_METHOD");

                    if (pMethod != PaymentMethod.Cash && string.IsNullOrWhiteSpace(request.InitialPaymentReference))
                        throw new DomainException($"Reference number is mandatory for {pMethod} supplier payments.", "PAYMENT_REFERENCE_REQUIRED");

                    var initialPayment = new SupplierPayment(
                        Guid.NewGuid(),
                        store.Id,
                        supplier.Id,
                        SupplierPaymentType.Payment,
                        Money.FromMinorUnits(initialPaidPaise, Currency.INR),
                        pMethod,
                        request.InitialPaymentReference,
                        DateTime.UtcNow,
                        Guid.NewGuid(),
                        userId,
                        $"Initial payment for bill #{trimmedSupplierInvoice}");

                    var alloc = new SupplierPaymentAllocation(0, initialPayment.Id, invoiceId, Money.FromMinorUnits(initialPaidPaise, Currency.INR));
                    initialPayment.AddAllocation(alloc);

                    await _supplierRepo.AddPaymentAsync(initialPayment, cancellationToken);
                }

                long totalAmountPaidPaise = creditOffsetPaise + initialPaidPaise;
                long balanceDuePaise = grandTotalPaise - totalAmountPaidPaise;

                if (balanceDuePaise > 0)
                {
                    supplier.IncreaseBalanceDue(Money.FromMinorUnits(balanceDuePaise, Currency.INR));
                }

                var paymentStatus = balanceDuePaise == 0
                    ? PurchasePaymentStatus.Paid
                    : (totalAmountPaidPaise > 0 ? PurchasePaymentStatus.PartiallyPaid : PurchasePaymentStatus.Unpaid);

                var invoice = new PurchaseInvoice(
                    id: invoiceId,
                    storeId: store.Id,
                    invoiceNumber: invoiceNumber,
                    supplierInvoiceNumber: trimmedSupplierInvoice,
                    supplierId: supplier.Id,
                    invoiceDate: request.InvoiceDate,
                    receivedDateUtc: DateTime.UtcNow,
                    subtotal: Money.FromMinorUnits(subtotalPaise, Currency.INR),
                    totalTax: Money.FromMinorUnits(totalTaxPaise, Currency.INR),
                    grandTotal: grandTotalMoney,
                    creditOffsetAmount: Money.FromMinorUnits(creditOffsetPaise, Currency.INR),
                    amountPaid: Money.FromMinorUnits(totalAmountPaidPaise, Currency.INR),
                    balanceDue: Money.FromMinorUnits(balanceDuePaise, Currency.INR),
                    paymentStatus: paymentStatus,
                    createdByUserId: userId,
                    clientTransactionId: request.ClientTransactionId,
                    notes: request.Notes);

                foreach (var item in domainItems)
                {
                    invoice.AddItem(item);
                }

                await _purchaseRepo.AddAsync(invoice, cancellationToken);

                await _auditRepo.RecordAsync(
                    "Purchases",
                    "PURCHASE_RECEIVED",
                    executingUser,
                    string.Format(CultureInfo.InvariantCulture, "Inwarded purchase {0} (Bill #{1}) from supplier '{2}' total {3} paise.", invoiceNumber, trimmedSupplierInvoice, supplier.Name, grandTotalPaise),
                    cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return ApiResponse.Ok(MapToDto(invoice, supplier.Name));
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

        return ApiResponse.Fail<PurchaseInvoiceDto>("Failed to inward purchase after retries.", "TRANSIENT_FAILURE");
    }

    public async Task<ApiResponse<PurchaseInvoiceDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var invoice = await _purchaseRepo.GetByIdAsync(id, cancellationToken);
        if (invoice == null)
            return ApiResponse.Fail<PurchaseInvoiceDto>("Purchase invoice not found.", "PURCHASE_NOT_FOUND");

        var supplier = await _supplierRepo.GetByIdAsync(invoice.SupplierId, cancellationToken);
        return ApiResponse.Ok(MapToDto(invoice, supplier?.Name ?? "Unknown"));
    }

    public async Task<ApiResponse<PurchaseInvoiceDto>> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default)
    {
        var invoice = await _purchaseRepo.GetByInvoiceNumberAsync(invoiceNumber, cancellationToken);
        if (invoice == null)
            return ApiResponse.Fail<PurchaseInvoiceDto>("Purchase invoice not found.", "PURCHASE_NOT_FOUND");

        var supplier = await _supplierRepo.GetByIdAsync(invoice.SupplierId, cancellationToken);
        return ApiResponse.Ok(MapToDto(invoice, supplier?.Name ?? "Unknown"));
    }

    public async Task<ApiResponse<PagedResult<PurchaseInvoiceDto>>> SearchAsync(
        Guid? supplierId,
        DateTime? fromDateUtc,
        DateTime? toDateUtc,
        int? paymentStatus,
        int? status,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var (items, totalCount) = await _purchaseRepo.SearchAsync(
            supplierId, fromDateUtc, toDateUtc, paymentStatus, status, searchTerm, clampedPage, clampedSize, cancellationToken);

        var dtos = new List<PurchaseInvoiceDto>(items.Count);
        foreach (var inv in items)
        {
            var supplier = await _supplierRepo.GetByIdAsync(inv.SupplierId, cancellationToken);
            dtos.Add(MapToDto(inv, supplier?.Name ?? "Unknown"));
        }

        return ApiResponse.Ok(new PagedResult<PurchaseInvoiceDto>(dtos, totalCount, clampedPage, clampedSize));
    }

    public async Task<ApiResponse<PurchaseCancellationResultDto>> CancelPurchaseAsync(
        Guid id,
        CancelPurchaseRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientTransactionId == Guid.Empty)
            return ApiResponse.Fail<PurchaseCancellationResultDto>("ClientTransactionId is mandatory.", "CLIENT_TX_REQUIRED");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse.Fail<PurchaseCancellationResultDto>("Cancellation reason is mandatory.", "INVALID_CANCELLATION_REASON");

        // Idempotency pre-check
        var existingCancelled = await _purchaseRepo.GetByCancellationClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
        if (existingCancelled != null)
        {
            return ApiResponse.Ok(new PurchaseCancellationResultDto(
                existingCancelled.Id,
                existingCancelled.InvoiceNumber,
                existingCancelled.Status,
                request.SettlementMode,
                existingCancelled.AmountPaid.MinorUnits,
                existingCancelled.CancellationReason ?? request.Reason,
                existingCancelled.UpdatedAtUtc));
        }

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _unitOfWork.ResetChangeTracker();

            existingCancelled = await _purchaseRepo.GetByCancellationClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
            if (existingCancelled != null)
            {
                return ApiResponse.Ok(new PurchaseCancellationResultDto(
                    existingCancelled.Id,
                    existingCancelled.InvoiceNumber,
                    existingCancelled.Status,
                    request.SettlementMode,
                    existingCancelled.AmountPaid.MinorUnits,
                    existingCancelled.CancellationReason ?? request.Reason,
                    existingCancelled.UpdatedAtUtc));
            }

            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var invoice = await _purchaseRepo.GetByIdAsync(id, cancellationToken);
                if (invoice == null)
                    throw new DomainException("Purchase invoice not found.", "PURCHASE_NOT_FOUND");

                if (invoice.Status == PurchaseStatus.Cancelled)
                {
                    if (invoice.CancellationClientTransactionId == request.ClientTransactionId)
                    {
                        return ApiResponse.Ok(new PurchaseCancellationResultDto(
                            invoice.Id,
                            invoice.InvoiceNumber,
                            invoice.Status,
                            request.SettlementMode,
                            invoice.AmountPaid.MinorUnits,
                            invoice.CancellationReason ?? request.Reason,
                            invoice.UpdatedAtUtc));
                    }

                    throw new DomainException("Purchase invoice has already been cancelled under a different transaction ID.", "PURCHASE_ALREADY_CANCELLED");
                }

                // Check stock availability for return
                foreach (var item in invoice.Items)
                {
                    var stock = await _inventoryRepo.GetStockBalanceTrackedAsync(item.ProductId, cancellationToken);
                    if (stock == null || stock.CurrentQuantity.Value < item.Quantity.Value)
                    {
                        var available = stock?.CurrentQuantity.Value ?? 0m;
                        throw new DomainException(
                            string.Format(CultureInfo.InvariantCulture, "Insufficient stock for product '{0}'. Available: {1}, Required for return: {2}.", item.ProductName, available, item.Quantity.Value),
                            "INSUFFICIENT_STOCK_FOR_RETURN");
                    }

                    stock.ApplyMovement(StockMovementType.PurchaseReturn, item.Quantity);

                    var movement = new StockMovement(
                        id: 0,
                        productId: item.ProductId,
                        movementType: StockMovementType.PurchaseReturn,
                        quantityChanged: item.Quantity,
                        resultingQuantity: stock.CurrentQuantity,
                        referenceNumber: invoice.InvoiceNumber,
                        reason: string.Format(CultureInfo.InvariantCulture, "Purchase cancellation for {0}: {1}", invoice.InvoiceNumber, request.Reason),
                        userId: userId);

                    await _inventoryRepo.AddStockMovementAsync(movement, cancellationToken);
                }

                var supplier = await _supplierRepo.GetByIdAsync(invoice.SupplierId, cancellationToken);
                if (supplier == null)
                    throw new DomainException("Supplier not found.", "SUPPLIER_NOT_FOUND");

                var store = await _storeRepo.GetStoreAsync(cancellationToken);
                if (store == null)
                    throw new DomainException("Store configuration not found.", "STORE_CONFIG_NOT_FOUND");

                // Accounting Reversal
                var balanceDue = invoice.BalanceDue;
                var amountPaid = invoice.AmountPaid;

                if (balanceDue.IsPositive)
                {
                    supplier.DecreaseBalanceDue(balanceDue);
                }

                if (amountPaid.IsPositive)
                {
                    if (request.SettlementMode == RefundSettlementMode.SupplierPayout)
                    {
                        if (!request.PayoutMethod.HasValue)
                            throw new DomainException("Payout method is required when receiving a cash/bank refund from supplier.", "PAYOUT_METHOD_REQUIRED");

                        var pMethod = request.PayoutMethod.Value;
                        if (pMethod == PaymentMethod.Card || pMethod == PaymentMethod.Mixed || pMethod == PaymentMethod.Credit)
                            throw new DomainException($"Payout method '{pMethod}' is not permitted for supplier transactions.", "INVALID_SUPPLIER_PAYMENT_METHOD");

                        if (pMethod != PaymentMethod.Cash && string.IsNullOrWhiteSpace(request.PayoutReference))
                            throw new DomainException($"Reference number is mandatory for {pMethod} supplier refunds.", "PAYMENT_REFERENCE_REQUIRED");

                        var refundPayment = new SupplierPayment(
                            Guid.NewGuid(),
                            store.Id,
                            supplier.Id,
                            SupplierPaymentType.RefundFromSupplier,
                            amountPaid,
                            pMethod,
                            request.PayoutReference,
                            DateTime.UtcNow,
                            Guid.NewGuid(),
                            userId,
                            string.Format(CultureInfo.InvariantCulture, "Refund received from supplier for cancelled purchase {0}: {1}", invoice.InvoiceNumber, request.Reason));

                        await _supplierRepo.AddPaymentAsync(refundPayment, cancellationToken);
                    }
                    else if (request.SettlementMode == RefundSettlementMode.StoreCredit)
                    {
                        supplier.IncreaseCreditBalance(amountPaid);
                    }
                }

                invoice.Cancel(request.Reason, request.ClientTransactionId);

                await _auditRepo.RecordAsync(
                    "Purchases",
                    "PURCHASE_CANCELLED",
                    executingUser,
                    string.Format(CultureInfo.InvariantCulture, "Cancelled purchase {0} (Bill #{1}) from supplier '{2}'. Settlement: {3}. Reason: {4}", invoice.InvoiceNumber, invoice.SupplierInvoiceNumber, supplier.Name, request.SettlementMode, request.Reason),
                    cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return ApiResponse.Ok(new PurchaseCancellationResultDto(
                    invoice.Id,
                    invoice.InvoiceNumber,
                    invoice.Status,
                    request.SettlementMode,
                    amountPaid.MinorUnits,
                    invoice.CancellationReason ?? request.Reason,
                    invoice.UpdatedAtUtc));
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

        return ApiResponse.Fail<PurchaseCancellationResultDto>("Failed to cancel purchase after retries.", "TRANSIENT_FAILURE");
    }

    private static PurchaseInvoiceDto MapToDto(PurchaseInvoice p, string supplierName) =>
        new(
            p.Id,
            p.StoreId,
            p.InvoiceNumber,
            p.SupplierInvoiceNumber,
            p.SupplierId,
            supplierName,
            p.InvoiceDate,
            p.ReceivedDateUtc,
            p.Subtotal.MinorUnits,
            p.TotalTax.MinorUnits,
            p.GrandTotal.MinorUnits,
            p.CreditOffsetAmount.MinorUnits,
            p.AmountPaid.MinorUnits,
            p.BalanceDue.MinorUnits,
            p.PaymentStatus,
            p.Status,
            p.Notes,
            p.CreatedByUserId,
            p.CreatedAtUtc,
            p.Items.Select(i => new PurchaseItemDto(
                i.Id,
                i.ProductId,
                i.ProductSku,
                i.ProductName,
                i.UnitOfMeasure,
                i.Quantity.Value,
                i.UnitCost.MinorUnits,
                i.TaxRatePercentage,
                i.TaxAmount.MinorUnits,
                i.LineTotal.MinorUnits,
                i.BatchNumber,
                i.ExpiryDate,
                i.TaxComponents.Select(tc => new PurchaseTaxComponentDto(tc.ComponentName, tc.RatePercentage, tc.TaxAmount.MinorUnits)).ToList()
            )).ToList());
}
