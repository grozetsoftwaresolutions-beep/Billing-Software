using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Entities.Customers;
using System.Globalization;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Services;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class SalesService : ISalesService
{
    private readonly ISaleRepository _saleRepo;
    private readonly ISaleRefundRepository _refundRepo;
    private readonly IProductRepository _productRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly ICategoryRepository _categoryRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly IInvoiceSequenceRepository _sequenceRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPricingCalculator _pricingCalculator;
    private readonly ICustomerRepository? _customerRepo;
    private readonly ICashSessionRepository? _cashSessionRepo;
    private readonly RetailPOS.Application.Security.ISupervisorApprovalService? _supervisorApprovalService;

    public SalesService(
        ISaleRepository saleRepo,
        ISaleRefundRepository refundRepo,
        IProductRepository productRepo,
        IInventoryRepository inventoryRepo,
        ICategoryRepository categoryRepo,
        IStoreRepository storeRepo,
        IInvoiceSequenceRepository sequenceRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork,
        IPricingCalculator? pricingCalculator = null,
        ICustomerRepository? customerRepo = null,
        ICashSessionRepository? cashSessionRepo = null,
        RetailPOS.Application.Security.ISupervisorApprovalService? supervisorApprovalService = null)
    {
        _saleRepo = saleRepo;
        _refundRepo = refundRepo;
        _productRepo = productRepo;
        _inventoryRepo = inventoryRepo;
        _categoryRepo = categoryRepo;
        _storeRepo = storeRepo;
        _sequenceRepo = sequenceRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
        _pricingCalculator = pricingCalculator ?? new PricingCalculator();
        _customerRepo = customerRepo;
        _cashSessionRepo = cashSessionRepo;
        _supervisorApprovalService = supervisorApprovalService;
    }

    public async Task<ApiResponse<CartCalculationDto>> CalculateCartAsync(
        CalculateSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Items == null || request.Items.Count == 0)
            return ApiResponse.Fail<CartCalculationDto>("Cart must contain at least one item.", "CART_EMPTY");

        var store = await _storeRepo.GetStoreAsync(cancellationToken);
        var pricingModel = store?.DefaultPricingModel ?? PricingModel.TaxInclusive;
        var roundToWholeRupee = store != null && store.CashRoundingEnabled && request.EstimatedPaymentMethod == PaymentMethod.Cash;

        var products = new List<Product>();
        var taxCategoryCache = new Dictionary<int, TaxCategory?>();

        foreach (var item in request.Items)
        {
            var product = await _productRepo.GetByIdAsync(item.ProductId, cancellationToken);
            if (product == null || !product.IsActive)
            {
                return ApiResponse.Fail<CartCalculationDto>(
                    string.Format(CultureInfo.InvariantCulture, "Product with ID '{0}' not found or inactive.", item.ProductId),
                    "PRODUCT_NOT_FOUND");
            }
            products.Add(product);

            if (!taxCategoryCache.ContainsKey(product.TaxCategoryId))
            {
                taxCategoryCache[product.TaxCategoryId] = await _categoryRepo.GetTaxCategoryByIdAsync(product.TaxCategoryId, cancellationToken);
            }
        }

        var lines = new List<PricedLineResult>();
        for (int i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            var prod = products[i];
            var qty = Quantity.From(item.Quantity, prod.Unit);
            var taxCat = taxCategoryCache[prod.TaxCategoryId];

            var priced = _pricingCalculator.CalculateLine(
                qty,
                prod.SellingPrice,
                taxCat,
                pricingModel,
                item.DiscountType,
                item.DiscountValue);
            lines.Add(priced);
        }

        Money? invoiceDiscount = null;
        if (request.OrderDiscountType != DiscountType.None && request.OrderDiscountValue > 0m)
        {
            var grossSum = lines.Aggregate(Money.Zero(Currency.INR), (s, l) => s + l.GrossAmount - l.DiscountAmount);
            invoiceDiscount = DiscountCalculation.CalculateDiscount(grossSum, request.OrderDiscountType, request.OrderDiscountValue);
        }

        var invoicePriced = _pricingCalculator.CalculateInvoice(lines, invoiceDiscount, roundToWholeRupee);

        var lineDtos = new List<SaleItemDto>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var p = products[i];
            var taxBreakdown = l.TaxComponents.Select(tc => new TaxComponentBreakdownDto(tc.ComponentName, tc.RatePercentage, tc.Amount.MinorUnits)).ToList();
            lineDtos.Add(new SaleItemDto(
                0,
                p.Id,
                p.SKU,
                p.Name,
                l.Quantity.Value,
                l.Quantity.Unit.Code,
                l.UnitPrice.MinorUnits,
                l.GrossAmount.MinorUnits,
                l.DiscountAmount.MinorUnits,
                l.TaxableAmount.MinorUnits,
                l.TaxAmount.MinorUnits,
                l.LineTotal.MinorUnits,
                taxBreakdown));
        }

        var compTotals = invoicePriced.TaxComponentTotals.Select(ct => new TaxComponentBreakdownDto(ct.ComponentName, 0m, ct.Amount.MinorUnits)).ToList();

        var resultDto = new CartCalculationDto(
            invoicePriced.GrossSubtotal.MinorUnits,
            invoicePriced.TotalLineDiscounts.MinorUnits,
            invoicePriced.InvoiceDiscount.MinorUnits,
            invoicePriced.TaxableSubtotal.MinorUnits,
            invoicePriced.TotalTax.MinorUnits,
            invoicePriced.RoundingAdjustment.MinorUnits,
            invoicePriced.FinalPayable.MinorUnits,
            lineDtos,
            compTotals);

        return ApiResponse.Ok(resultDto);
    }

    public async Task<ApiResponse<SaleInvoiceDto>> CreateSaleAsync(
        CreateSaleRequest request,
        Guid cashierId,
        string cashierName,
        Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientTransactionId == Guid.Empty)
            return ApiResponse.Fail<SaleInvoiceDto>("ClientTransactionId is required.", "CLIENT_TX_ID_REQUIRED");

        if (request.Items == null || request.Items.Count == 0)
            return ApiResponse.Fail<SaleInvoiceDto>("Sale must contain at least one item.", "SALE_ITEMS_REQUIRED");

        if (request.Payments == null || request.Payments.Count == 0)
            return ApiResponse.Fail<SaleInvoiceDto>("At least one payment is required.", "PAYMENTS_REQUIRED");

        // Validate payment methods
        foreach (var p in request.Payments)
        {
            if (p.Method == PaymentMethod.Mixed)
                return ApiResponse.Fail<SaleInvoiceDto>("Mixed payment cannot be submitted as an individual payment item.", "INVALID_PAYMENT_METHOD");
        }

        var creditPaymentAmount = request.Payments.Where(p => p.Method == PaymentMethod.Credit).Sum(p => p.AmountAppliedPaise);
        if (creditPaymentAmount > 0 && !request.CustomerId.HasValue)
        {
            return ApiResponse.Fail<SaleInvoiceDto>("Customer is required for credit sales.", "CUSTOMER_REQUIRED_FOR_CREDIT_SALE");
        }

        // Idempotency fast-path check
        var existingSale = await _saleRepo.GetByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
        if (existingSale != null)
        {
            return ApiResponse.Ok(MapToDto(existingSale));
        }

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _unitOfWork.ResetChangeTracker();

            // Check again inside loop in case previous retry or concurrent request committed
            var existing = await _saleRepo.GetByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
            if (existing != null)
            {
                return ApiResponse.Ok(MapToDto(existing));
            }

            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var store = await _storeRepo.GetStoreAsync(cancellationToken);
                if (store == null)
                    throw new DomainException("Store configuration not found.", "STORE_CONFIG_NOT_FOUND");

                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(store.TimeZoneId);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
                var periodDate = localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                // Validate and lock products
                var products = new List<Product>();
                var taxCategoryCache = new Dictionary<int, TaxCategory?>();

                foreach (var item in request.Items)
                {
                    if (item.Quantity <= 0)
                        throw new DomainException("Item quantity must be strictly greater than zero.", "INVALID_QUANTITY");

                    var product = await _productRepo.GetByIdAsync(item.ProductId, cancellationToken);
                    if (product == null || !product.IsActive)
                        throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Product '{0}' not found or inactive.", item.ProductId), "PRODUCT_NOT_FOUND");

                    products.Add(product);

                    if (!taxCategoryCache.ContainsKey(product.TaxCategoryId))
                    {
                        taxCategoryCache[product.TaxCategoryId] = await _categoryRepo.GetTaxCategoryByIdAsync(product.TaxCategoryId, cancellationToken);
                    }
                }

                // Calculate pricing
                var lines = new List<PricedLineResult>();
                for (int i = 0; i < request.Items.Count; i++)
                {
                    var item = request.Items[i];
                    var prod = products[i];
                    var qty = Quantity.From(item.Quantity, prod.Unit);
                    var taxCat = taxCategoryCache[prod.TaxCategoryId];

                    var priced = _pricingCalculator.CalculateLine(
                        qty,
                        prod.SellingPrice,
                        taxCat,
                        store.DefaultPricingModel,
                        item.DiscountType,
                        item.DiscountValue);
                    lines.Add(priced);
                }

                Money? invoiceDiscount = null;
                if (request.OrderDiscountType != DiscountType.None && request.OrderDiscountValue > 0m)
                {
                    var grossSum = lines.Aggregate(Money.Zero(Currency.INR), (s, l) => s + l.GrossAmount - l.DiscountAmount);
                    invoiceDiscount = DiscountCalculation.CalculateDiscount(grossSum, request.OrderDiscountType, request.OrderDiscountValue);
                }

                var roundToWholeRupee = store.CashRoundingEnabled && request.Payments.All(p => p.Method == PaymentMethod.Cash);
                var pricedResult = _pricingCalculator.CalculateInvoice(lines, invoiceDiscount, roundToWholeRupee);

                // Allocate invoice number inside transaction
                var invoiceNumber = await _sequenceRepo.GetNextSequenceNumberAsync("SALE_INVOICE", "INV", periodDate, cancellationToken);

                var sale = new SaleInvoice(
                    id: Guid.NewGuid(),
                    invoiceNumber: invoiceNumber,
                    clientTransactionId: request.ClientTransactionId,
                    terminalId: terminalId,
                    cashierId: cashierId,
                    customerId: request.CustomerId,
                    pricingModel: store.DefaultPricingModel,
                    grossSubtotal: pricedResult.GrossSubtotal,
                    totalLineDiscounts: pricedResult.TotalLineDiscounts,
                    invoiceDiscount: pricedResult.InvoiceDiscount,
                    taxableSubtotal: pricedResult.TaxableSubtotal,
                    totalTax: pricedResult.TotalTax,
                    roundingAdjustment: pricedResult.RoundingAdjustment,
                    finalPayable: pricedResult.FinalPayable);

                // Process line items & stock balances
                for (int i = 0; i < request.Items.Count; i++)
                {
                    var item = request.Items[i];
                    var prod = products[i];
                    var pricedLine = pricedResult.Lines[i];
                    var qtyVo = Quantity.From(item.Quantity, prod.Unit);

                    var stock = await _inventoryRepo.GetStockBalanceTrackedAsync(prod.Id, cancellationToken)
                        ?? throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Stock balance record not found for product '{0}'.", prod.Name), "STOCK_BALANCE_NOT_FOUND");

                    stock.ApplyMovement(StockMovementType.Sale, qtyVo, store.NegativeStockPolicy);

                    var movement = new StockMovement(
                        id: 0,
                        productId: prod.Id,
                        movementType: StockMovementType.Sale,
                        quantityChanged: -qtyVo,
                        resultingQuantity: stock.CurrentQuantity,
                        referenceNumber: invoiceNumber,
                        reason: string.Format(CultureInfo.InvariantCulture, "Sale checkout: {0}", invoiceNumber),
                        userId: cashierId,
                        terminalId: terminalId);

                    await _inventoryRepo.AddStockMovementAsync(movement, cancellationToken);

                    var saleItem = new SaleItem(
                        id: 0,
                        saleId: sale.Id,
                        productId: prod.Id,
                        productSku: prod.SKU,
                        productName: prod.Name,
                        quantity: qtyVo,
                        unitSellingPrice: pricedLine.UnitPrice,
                        unitCostPrice: prod.CostPrice,
                        grossAmount: pricedLine.GrossAmount,
                        discountAmount: pricedLine.DiscountAmount,
                        taxRatePercentage: pricedLine.TaxComponents.Sum(tc => tc.RatePercentage),
                        taxableAmount: pricedLine.TaxableAmount,
                        taxAmount: pricedLine.TaxAmount,
                        lineTotal: pricedLine.LineTotal);

                    foreach (var tc in pricedLine.TaxComponents)
                    {
                        saleItem.AddTaxComponent(new SaleItemTaxComponent(
                            id: 0,
                            saleItemId: 0,
                            componentName: tc.ComponentName,
                            ratePercentage: tc.RatePercentage,
                            amount: tc.Amount));
                    }

                    sale.AddItem(saleItem);
                }

                // Add payments & enforce tender invariants
                foreach (var p in request.Payments)
                {
                    Money? tendered = p.TenderedAmountPaise.HasValue ? Money.FromMinorUnits(p.TenderedAmountPaise.Value, Currency.INR) : null;
                    var payRecord = new PaymentRecord(
                        id: Guid.NewGuid(),
                        saleId: sale.Id,
                        method: p.Method,
                        amount: Money.FromMinorUnits(p.AmountAppliedPaise, Currency.INR),
                        tenderedAmount: tendered,
                        changeAmount: null,
                        referenceNumber: p.ReferenceNumber);

                    sale.AddPayment(payRecord);
                }

                sale.ValidatePaymentsReconcile();

                // Phase 8: Customer credit update
                var creditPaidPaise = request.Payments.Where(p => p.Method == PaymentMethod.Credit).Sum(p => p.AmountAppliedPaise);
                if (creditPaidPaise > 0)
                {
                    if (!request.CustomerId.HasValue)
                        throw new DomainException("Customer is required for credit sales.", "CUSTOMER_REQUIRED_FOR_CREDIT_SALE");

                    if (_customerRepo != null)
                    {
                        var customer = await _customerRepo.GetByIdTrackedAsync(request.CustomerId.Value, cancellationToken);
                        if (customer == null)
                            throw new DomainException("Customer not found.", "CUSTOMER_NOT_FOUND");

                        if (!customer.IsActive)
                            throw new DomainException("Customer is inactive. Cannot process credit sale.", "CUSTOMER_INACTIVE");

                        // Store credit consumed first
                        long storeCreditConsumed = 0;
                        if (customer.CreditBalance.IsPositive)
                        {
                            storeCreditConsumed = Math.Min(customer.CreditBalance.MinorUnits, creditPaidPaise);
                            customer.DecreaseCreditBalance(Money.FromMinorUnits(storeCreditConsumed, Currency.INR));
                        }

                        long remainingCreditCharged = creditPaidPaise - storeCreditConsumed;
                        if (remainingCreditCharged > 0)
                        {
                            customer.AddCredit(Money.FromMinorUnits(remainingCreditCharged, Currency.INR));
                        }
                    }
                }

                // Phase 8: Cash session cash contribution (including mixed payments)
                var cashPaidPaise = request.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountAppliedPaise);
                if (cashPaidPaise > 0 && _cashSessionRepo != null)
                {
                    var activeSession = await _cashSessionRepo.GetActiveSessionAsync(terminalId, cancellationToken);
                    if (activeSession != null)
                    {
                        var cashMoney = Money.FromMinorUnits(cashPaidPaise, Currency.INR);
                        activeSession.AddCashSale(cashMoney);
                        var cashTx = new CashSessionTransaction(
                            0,
                            activeSession.Id,
                            CashTransactionType.CashSale,
                            cashMoney,
                            $"Cash sale invoice {sale.InvoiceNumber}",
                            Guid.NewGuid(),
                            cashierId,
                            authorizedByUserId: null,
                            saleInvoiceId: sale.Id,
                            refundId: null,
                            customerPaymentId: null);
                        await _cashSessionRepo.AddTransactionAsync(cashTx, cancellationToken);
                    }
                }

                await _saleRepo.AddAsync(sale, cancellationToken);

                var auditDetails = string.Format(CultureInfo.InvariantCulture, "Sale completed. Invoice: {0}, Total: {1:N2}, Items: {2}, Cashier: {3}.", invoiceNumber, pricedResult.FinalPayable.AmountInMajorUnits, sale.Items.Count, cashierName);
                await _auditRepo.RecordAsync("Sales", "SALE_COMPLETED", cashierName, auditDetails, cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return ApiResponse.Ok(MapToDto(sale));
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

        return ApiResponse.Fail<SaleInvoiceDto>("Failed to complete sale due to concurrent updates.", "CONCURRENCY_ERROR");
    }

    public async Task<ApiResponse<SaleRefundDto>> RefundSaleAsync(
        Guid saleId,
        RefundSaleRequest request,
        Guid authorizedByUserId,
        string authorizedByUserName,
        Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientTransactionId == Guid.Empty)
            return ApiResponse.Fail<SaleRefundDto>("ClientTransactionId is required.", "CLIENT_TX_ID_REQUIRED");

        if (request.Items == null || request.Items.Count == 0)
            return ApiResponse.Fail<SaleRefundDto>("Refund must contain at least one item.", "REFUND_ITEMS_REQUIRED");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse.Fail<SaleRefundDto>("Refund reason is required.", "REFUND_REASON_REQUIRED");

        if (request.RefundMethod == PaymentMethod.Mixed)
            return ApiResponse.Fail<SaleRefundDto>("Mixed payment method is not allowed for refunds.", "INVALID_REFUND_METHOD");

        // Idempotency fast-path check
        var existingRefund = await _refundRepo.GetByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
        if (existingRefund != null)
        {
            var origSale = await _saleRepo.GetByIdAsync(existingRefund.SaleId, cancellationToken);
            return ApiResponse.Ok(MapRefundToDto(existingRefund, origSale?.InvoiceNumber ?? "UNKNOWN"));
        }

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _unitOfWork.ResetChangeTracker();

            var existing = await _refundRepo.GetByClientTransactionIdAsync(request.ClientTransactionId, cancellationToken);
            if (existing != null)
            {
                var origSale = await _saleRepo.GetByIdAsync(existing.SaleId, cancellationToken);
                return ApiResponse.Ok(MapRefundToDto(existing, origSale?.InvoiceNumber ?? "UNKNOWN"));
            }

            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var store = await _storeRepo.GetStoreAsync(cancellationToken);
                if (store == null)
                    throw new DomainException("Store configuration not found.", "STORE_CONFIG_NOT_FOUND");

                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(store.TimeZoneId);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
                var periodDate = localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                var sale = await _saleRepo.GetByIdAsync(saleId, cancellationToken);
                if (sale == null)
                    throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Sale with ID '{0}' not found.", saleId), "SALE_NOT_FOUND");

                if (sale.Status == SaleStatus.Voided)
                    throw new DomainException("Cannot refund a voided sale.", "SALE_VOIDED");

                if (sale.Status == SaleStatus.Refunded)
                    throw new DomainException("Sale has already been fully refunded.", "SALE_ALREADY_REFUNDED");

                var previousRefunds = await _refundRepo.GetRefundsBySaleIdAsync(saleId, cancellationToken);
                var totalPreviouslyRefunded = previousRefunds.Sum(r => r.TotalRefundAmount.MinorUnits);
                var remainingRefundable = sale.FinalPayable.MinorUnits - totalPreviouslyRefunded;

                if (remainingRefundable <= 0)
                    throw new DomainException("No remaining refundable amount on this sale.", "NO_REFUNDABLE_AMOUNT");

                var refundId = Guid.NewGuid();
                var refundItems = new List<SaleRefundItem>();
                long calculatedRefundTotalPaise = 0;

                foreach (var rItem in request.Items)
                {
                    if (rItem.Quantity <= 0)
                        throw new DomainException("Refund quantity must be strictly greater than zero.", "INVALID_REFUND_QUANTITY");

                    var saleItem = sale.Items.FirstOrDefault(i => i.Id == rItem.SaleItemId);
                    if (saleItem == null)
                        throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Sale item with ID '{0}' does not belong to sale '{1}'.", rItem.SaleItemId, sale.InvoiceNumber), "INVALID_SALE_ITEM");

                    var cumulativeRefundedQty = await _refundRepo.GetRefundedQuantityForSaleItemAsync(saleItem.Id, cancellationToken);
                    if (cumulativeRefundedQty + rItem.Quantity > saleItem.Quantity.Value)
                        throw new DomainException(
                            string.Format(CultureInfo.InvariantCulture, "Refund quantity {0} exceeds available quantity {1} for item '{2}'.", rItem.Quantity, saleItem.Quantity.Value - cumulativeRefundedQty, saleItem.ProductName),
                            "REFUND_QUANTITY_EXCEEDS_PURCHASED");

                    long itemRefundAmountPaise;
                    long taxRefundPaise;

                    if (rItem.Quantity == saleItem.Quantity.Value && cumulativeRefundedQty == 0)
                    {
                        itemRefundAmountPaise = saleItem.LineTotal.MinorUnits;
                        taxRefundPaise = saleItem.TaxAmount.MinorUnits;
                    }
                    else
                    {
                        itemRefundAmountPaise = (long)Math.Round((decimal)saleItem.LineTotal.MinorUnits * rItem.Quantity / saleItem.Quantity.Value, MidpointRounding.AwayFromZero);
                        taxRefundPaise = (long)Math.Round((decimal)saleItem.TaxAmount.MinorUnits * rItem.Quantity / saleItem.Quantity.Value, MidpointRounding.AwayFromZero);
                    }

                    calculatedRefundTotalPaise += itemRefundAmountPaise;

                    var refundItem = new SaleRefundItem(
                        id: 0,
                        refundId: refundId,
                        saleItemId: saleItem.Id,
                        productId: saleItem.ProductId,
                        quantityRefunded: Quantity.From(rItem.Quantity, saleItem.Quantity.Unit),
                        refundAmount: Money.FromMinorUnits(itemRefundAmountPaise, Currency.INR),
                        taxRefundAmount: Money.FromMinorUnits(taxRefundPaise, Currency.INR));

                    refundItems.Add(refundItem);

                    // Restore inventory
                    var product = await _productRepo.GetByIdAsync(saleItem.ProductId, cancellationToken);
                    var stock = await _inventoryRepo.GetStockBalanceTrackedAsync(saleItem.ProductId, cancellationToken)
                        ?? throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Stock balance record not found for product '{0}'.", saleItem.ProductName), "STOCK_BALANCE_NOT_FOUND");

                    var refundQtyVo = Quantity.From(rItem.Quantity, product?.Unit ?? UnitOfMeasure.Piece);
                    stock.ApplyMovement(StockMovementType.SaleReturn, refundQtyVo, NegativeStockPolicy.Disallowed);

                    var returnMovement = new StockMovement(
                        id: 0,
                        productId: saleItem.ProductId,
                        movementType: StockMovementType.SaleReturn,
                        quantityChanged: refundQtyVo,
                        resultingQuantity: stock.CurrentQuantity,
                        referenceNumber: sale.InvoiceNumber,
                        reason: string.Format(CultureInfo.InvariantCulture, "Sale return refund for invoice: {0}", sale.InvoiceNumber),
                        userId: authorizedByUserId,
                        terminalId: terminalId);

                    await _inventoryRepo.AddStockMovementAsync(returnMovement, cancellationToken);
                }

                if (calculatedRefundTotalPaise > remainingRefundable)
                    throw new DomainException(
                        string.Format(CultureInfo.InvariantCulture, "Calculated refund total {0} paise exceeds remaining refundable balance {1} paise.", calculatedRefundTotalPaise, remainingRefundable),
                        "REFUND_AMOUNT_EXCEEDS_PAYABLE");

                // Allocate refund sequence number inside transaction
                var refundNumber = await _sequenceRepo.GetNextSequenceNumberAsync("SALE_REFUND", "REF", periodDate, cancellationToken);

                var refund = new SaleRefund(
                    id: refundId,
                    saleId: sale.Id,
                    refundNumber: refundNumber,
                    clientTransactionId: request.ClientTransactionId,
                    authorizedByUserId: authorizedByUserId,
                    authorizedByUserName: authorizedByUserName,
                    terminalId: terminalId,
                    refundMethod: request.RefundMethod,
                    totalRefundAmount: Money.FromMinorUnits(calculatedRefundTotalPaise, Currency.INR),
                    reason: request.Reason.Trim());

                foreach (var rItem in refundItems)
                {
                    refund.AddItem(rItem);
                }

                await _refundRepo.AddRefundAsync(refund, cancellationToken);

                // Phase 8: Cash session refund integration
                if (request.RefundMethod == PaymentMethod.Cash && _cashSessionRepo != null)
                {
                    var activeSession = await _cashSessionRepo.GetActiveSessionAsync(terminalId, cancellationToken);
                    if (activeSession != null)
                    {
                        activeSession.AddCashRefund(refund.TotalRefundAmount);
                        var refundTx = new CashSessionTransaction(
                            0,
                            activeSession.Id,
                            CashTransactionType.CashRefund,
                            refund.TotalRefundAmount,
                            $"Cash refund {refund.RefundNumber}",
                            Guid.NewGuid(),
                            authorizedByUserId,
                            authorizedByUserId: authorizedByUserId,
                            saleInvoiceId: sale.Id,
                            refundId: refund.Id,
                            customerPaymentId: null);
                        await _cashSessionRepo.AddTransactionAsync(refundTx, cancellationToken);
                    }
                }

                // Phase 8: Customer credit refund integration
                if (request.RefundMethod == PaymentMethod.Credit)
                {
                    if (!sale.CustomerId.HasValue)
                        throw new DomainException("Customer is required for credit refunds.", "CUSTOMER_REQUIRED_FOR_CREDIT_REFUND");

                    if (_customerRepo != null)
                    {
                        var customer = await _customerRepo.GetByIdTrackedAsync(sale.CustomerId.Value, cancellationToken);
                        if (customer != null)
                        {
                            var refundAmount = refund.TotalRefundAmount;
                            if (customer.OutstandingCredit.MinorUnits >= refundAmount.MinorUnits)
                            {
                                customer.RecordRepayment(refundAmount);
                            }
                            else
                            {
                                var debtReduction = customer.OutstandingCredit;
                                var excessToStoreCredit = refundAmount - debtReduction;
                                if (debtReduction.IsPositive)
                                    customer.RecordRepayment(debtReduction);
                                if (excessToStoreCredit.IsPositive)
                                    customer.IncreaseCreditBalance(excessToStoreCredit);
                            }
                        }
                    }
                }

                // Update sale invoice status
                var newTotalRefunded = totalPreviouslyRefunded + calculatedRefundTotalPaise;
                if (newTotalRefunded >= sale.FinalPayable.MinorUnits)
                {
                    sale.MarkFullyRefunded();
                }
                else
                {
                    sale.MarkPartiallyRefunded();
                }

                var auditDetails = string.Format(CultureInfo.InvariantCulture, "Sale refunded. Refund: {0}, Invoice: {1}, Amount: {2:N2}, Reason: {3}, Authorized: {4}.", refundNumber, sale.InvoiceNumber, refund.TotalRefundAmount.AmountInMajorUnits, request.Reason, authorizedByUserName);
                await _auditRepo.RecordAsync("Sales", "SALE_REFUNDED", authorizedByUserName, auditDetails, cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return ApiResponse.Ok(MapRefundToDto(refund, sale.InvoiceNumber));
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

        return ApiResponse.Fail<SaleRefundDto>("Failed to process refund due to concurrent updates.", "CONCURRENCY_ERROR");
    }

    public async Task<ApiResponse<SaleInvoiceDto>> GetSaleByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var sale = await _saleRepo.GetByIdAsync(id, cancellationToken);
        if (sale == null)
            return ApiResponse.Fail<SaleInvoiceDto>(string.Format(CultureInfo.InvariantCulture, "Sale with ID '{0}' not found.", id), "SALE_NOT_FOUND");

        return ApiResponse.Ok(MapToDto(sale));
    }

    public async Task<ApiResponse<SaleInvoiceDto>> GetSaleByInvoiceNumberAsync(
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            return ApiResponse.Fail<SaleInvoiceDto>("Invoice number is required.", "INVALID_INVOICE_NUMBER");

        var sale = await _saleRepo.GetByInvoiceNumberAsync(invoiceNumber.Trim(), cancellationToken);
        if (sale == null)
            return ApiResponse.Fail<SaleInvoiceDto>(string.Format(CultureInfo.InvariantCulture, "Sale with invoice number '{0}' not found.", invoiceNumber), "SALE_NOT_FOUND");

        return ApiResponse.Ok(MapToDto(sale));
    }

    public async Task<ApiResponse<PagedResult<SaleSummaryDto>>> SearchSalesAsync(
        SaleSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var (items, totalCount) = await _saleRepo.SearchAsync(request, cancellationToken);

        var summaries = items.Select(s => new SaleSummaryDto(
            s.Id,
            s.InvoiceNumber,
            s.TerminalId,
            s.CashierId,
            s.FinalPayable.MinorUnits,
            s.Status,
            s.CreatedAtUtc,
            s.Items.Count,
            s.Payments.Count > 0 ? s.Payments[0].Method.ToString() : "Unknown")).ToList();

        var pagedResult = new PagedResult<SaleSummaryDto>(summaries, totalCount, request.Page, request.PageSize);
        return ApiResponse.Ok(pagedResult);
    }

    public async Task<ApiResponse<IReadOnlyList<SaleRefundDto>>> GetRefundsForSaleAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        var sale = await _saleRepo.GetByIdAsync(saleId, cancellationToken);
        if (sale == null)
            return ApiResponse.Fail<IReadOnlyList<SaleRefundDto>>(string.Format(CultureInfo.InvariantCulture, "Sale with ID '{0}' not found.", saleId), "SALE_NOT_FOUND");

        var refunds = await _refundRepo.GetRefundsBySaleIdAsync(saleId, cancellationToken);
        var dtos = refunds.Select(r => MapRefundToDto(r, sale.InvoiceNumber)).ToList();

        return ApiResponse.Ok<IReadOnlyList<SaleRefundDto>>(dtos);
    }

    private static SaleInvoiceDto MapToDto(SaleInvoice s)
    {
        var itemDtos = s.Items.Select(i =>
        {
            var taxBreakdown = i.TaxComponents.Select(tc => new TaxComponentBreakdownDto(tc.ComponentName, tc.RatePercentage, tc.Amount.MinorUnits)).ToList();

            return new SaleItemDto(
                i.Id,
                i.ProductId,
                i.ProductSKU,
                i.ProductName,
                i.Quantity.Value,
                i.Quantity.Unit.Code,
                i.UnitSellingPrice.MinorUnits,
                i.GrossAmount.MinorUnits,
                i.DiscountAmount.MinorUnits,
                i.TaxableAmount.MinorUnits,
                i.TaxAmount.MinorUnits,
                i.LineTotal.MinorUnits,
                taxBreakdown);
        }).ToList();

        var paymentDtos = s.Payments.Select(p => new PaymentRecordDto(
            p.Id,
            p.Method,
            p.Amount.MinorUnits,
            p.TenderedAmount.MinorUnits,
            p.ChangeAmount.MinorUnits,
            p.ReferenceNumber)).ToList();

        var cashPayments = s.Payments.Where(p => p.Method == PaymentMethod.Cash && p.Amount.MinorUnits > 0).ToList();
        var hasCash = cashPayments.Count > 0;
        var drawerKick = new RetailPOS.Contracts.Hardware.DrawerKickDirective(
            hasCash,
            hasCash ? (s.Payments.Count > 1 ? "MIXED_CASH_SALE" : "CASH_SALE") : null,
            hasCash ? cashPayments.Sum(p => p.Amount.MinorUnits) : null);

        return new SaleInvoiceDto(
            s.Id,
            s.InvoiceNumber,
            s.ClientTransactionId,
            s.TerminalId,
            s.CashierId,
            "Cashier",
            s.CustomerId,
            s.Status,
            s.CreatedAtUtc,
            s.GrossSubtotal.MinorUnits,
            s.TotalLineDiscounts.MinorUnits,
            s.InvoiceDiscount.MinorUnits,
            s.TaxableSubtotal.MinorUnits,
            s.TotalTax.MinorUnits,
            s.RoundingAdjustment.MinorUnits,
            s.FinalPayable.MinorUnits,
            itemDtos,
            paymentDtos,
            s.ReprintCount,
            s.LastReprintedAtUtc,
            drawerKick);
    }

    private static SaleRefundDto MapRefundToDto(SaleRefund r, string invoiceNumber)
    {
        var itemDtos = r.Items.Select(i => new SaleRefundItemDto(
            i.Id,
            i.RefundId,
            i.SaleItemId,
            i.ProductId,
            i.QuantityRefunded.Value,
            i.RefundAmount.MinorUnits,
            i.TaxRefundAmount.MinorUnits)).ToList();

        var isCash = r.RefundMethod == PaymentMethod.Cash && r.TotalRefundAmount.MinorUnits > 0;
        var drawerKick = new RetailPOS.Contracts.Hardware.DrawerKickDirective(
            isCash,
            isCash ? "CASH_REFUND" : null,
            isCash ? r.TotalRefundAmount.MinorUnits : null);

        return new SaleRefundDto(
            r.Id,
            r.SaleId,
            invoiceNumber,
            r.RefundNumber,
            r.ClientTransactionId,
            r.AuthorizedByUserId,
            r.AuthorizedByUserName,
            r.TerminalId,
            r.RefundMethod,
            r.TotalRefundAmount.MinorUnits,
            r.Reason,
            r.CreatedAtUtc,
            itemDtos,
            drawerKick);
    }

    public async Task<ApiResponse<VoidSaleResponse>> VoidSaleAsync(
        Guid saleId,
        VoidSaleRequest request,
        Guid cashierId,
        string cashierName,
        string sessionIdentityHash,
        Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ApiResponse.Fail<VoidSaleResponse>("A reason is required to void a sale.", "REASON_REQUIRED");
        }

        // Verify supervisor authorization before entering transaction
        if (_supervisorApprovalService != null)
        {
            var authResult = await _supervisorApprovalService.AuthorizeInlineOrTokenAsync(
                request.SupervisorApprovalToken,
                request.SupervisorUsername,
                request.SupervisorPin,
                request.SupervisorPassword,
                cashierId,
                sessionIdentityHash,
                terminalId,
                Permission.VoidSale,
                "SaleInvoice",
                saleId.ToString(),
                request.RequestNonce,
                request.Reason,
                cancellationToken);

            if (!authResult.IsAuthorized)
            {
                return ApiResponse.Fail<VoidSaleResponse>(authResult.ErrorMessage ?? "Supervisor approval failed.", "SUPERVISOR_APPROVAL_FAILED");
            }
        }

        const int maxRetries = 5;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            _unitOfWork.ResetChangeTracker();
            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var sale = await _saleRepo.GetByIdAsync(saleId, cancellationToken);
                if (sale == null)
                {
                    return ApiResponse.Fail<VoidSaleResponse>("Sale not found.", "SALE_NOT_FOUND");
                }

                if (sale.Status == SaleStatus.Voided)
                {
                    return ApiResponse.Fail<VoidSaleResponse>("Sale has already been voided.", "SALE_ALREADY_VOIDED");
                }

                // Reverse inventory for each item
                foreach (var item in sale.Items)
                {
                    var product = await _productRepo.GetByIdAsync(item.ProductId, cancellationToken);
                    var stock = await _inventoryRepo.GetStockBalanceTrackedAsync(item.ProductId, cancellationToken)
                        ?? throw new DomainException(string.Format(CultureInfo.InvariantCulture, "Stock balance record not found for product '{0}'.", item.ProductName), "STOCK_BALANCE_NOT_FOUND");

                    var qtyVo = item.Quantity;
                    stock.ApplyMovement(StockMovementType.SaleReturn, qtyVo, NegativeStockPolicy.Disallowed);

                    var returnMovement = new StockMovement(
                        id: 0,
                        productId: item.ProductId,
                        movementType: StockMovementType.SaleReturn,
                        quantityChanged: qtyVo,
                        resultingQuantity: stock.CurrentQuantity,
                        referenceNumber: sale.InvoiceNumber,
                        reason: string.Format(CultureInfo.InvariantCulture, "Void of invoice {0}: {1}", sale.InvoiceNumber, request.Reason),
                        userId: cashierId,
                        terminalId: terminalId);

                    await _inventoryRepo.AddStockMovementAsync(returnMovement, cancellationToken);
                }

                // Payment Reconciliation
                long cashReversedPaise = 0;
                foreach (var payment in sale.Payments)
                {
                    if (payment.Method == PaymentMethod.Cash)
                    {
                        cashReversedPaise += payment.Amount.MinorUnits;
                    }
                    else if (payment.Method == PaymentMethod.Credit && sale.CustomerId.HasValue && _customerRepo != null)
                    {
                        var customer = await _customerRepo.GetByIdTrackedAsync(sale.CustomerId.Value, cancellationToken);
                        if (customer != null)
                        {
                            var refundAmount = payment.Amount;
                            if (customer.OutstandingCredit.MinorUnits >= refundAmount.MinorUnits)
                            {
                                customer.RecordRepayment(refundAmount);
                            }
                            else
                            {
                                var debtReduction = customer.OutstandingCredit;
                                var excessToStoreCredit = refundAmount - debtReduction;
                                if (debtReduction.IsPositive)
                                    customer.RecordRepayment(debtReduction);
                                if (excessToStoreCredit.IsPositive)
                                    customer.IncreaseCreditBalance(excessToStoreCredit);
                            }
                        }
                    }
                }

                // If cash was paid and cash session repo available, record cash refund in drawer session
                if (cashReversedPaise > 0 && _cashSessionRepo != null)
                {
                    var activeSession = await _cashSessionRepo.GetActiveSessionAsync(terminalId, cancellationToken);
                    if (activeSession != null)
                    {
                        var cashMoney = Money.FromMinorUnits(cashReversedPaise, Currency.INR);
                        activeSession.AddCashRefund(cashMoney);
                        var refundTx = new CashSessionTransaction(
                            0,
                            activeSession.Id,
                            CashTransactionType.CashRefund,
                            cashMoney,
                            $"Void of invoice {sale.InvoiceNumber}",
                            Guid.NewGuid(),
                            cashierId,
                            authorizedByUserId: cashierId,
                            saleInvoiceId: sale.Id,
                            refundId: null,
                            customerPaymentId: null);
                        await _cashSessionRepo.AddTransactionAsync(refundTx, cancellationToken);
                    }
                }

                // Domain transition
                sale.Void(cashierId, cashierName, request.Reason);

                // Immutable audit log
                var audit = new AuditLog(
                    0,
                    "SALE_VOIDED",
                    "SaleInvoice",
                    sale.Id.ToString(),
                    cashierId,
                    terminalId,
                    $"Sale invoice {sale.InvoiceNumber} voided. Cash reversed: {cashReversedPaise} paise. Reason: {request.Reason}",
                    success: true,
                    userName: cashierName,
                    reason: request.Reason,
                    beforeJson: $"{{\"status\": \"Completed\", \"invoice\": \"{sale.InvoiceNumber}\", \"finalPayable\": {sale.FinalPayable.AmountInMajorUnits}}}",
                    afterJson: $"{{\"status\": \"Voided\", \"invoice\": \"{sale.InvoiceNumber}\", \"finalPayable\": {sale.FinalPayable.AmountInMajorUnits}}}");

                await _auditRepo.RecordAsync(audit, cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return ApiResponse.Ok(new VoidSaleResponse(
                    sale.Id,
                    sale.InvoiceNumber,
                    sale.Status,
                    sale.VoidedAtUtc ?? DateTime.UtcNow,
                    cashierName,
                    request.Reason,
                    StockReversed: true,
                    CashReversedPaise: cashReversedPaise));
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

        return ApiResponse.Fail<VoidSaleResponse>("Failed to process sale void due to concurrent updates.", "CONCURRENCY_ERROR");
    }
}
