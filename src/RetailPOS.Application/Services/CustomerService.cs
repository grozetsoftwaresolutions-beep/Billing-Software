using System.Globalization;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class CustomerService : ICustomerService
{
    private readonly ICustomerRepository _customerRepo;
    private readonly ICashSessionRepository _cashSessionRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public CustomerService(
        ICustomerRepository customerRepo,
        ICashSessionRepository cashSessionRepo,
        IStoreRepository storeRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _customerRepo = customerRepo;
        _cashSessionRepo = cashSessionRepo;
        _storeRepo = storeRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<CustomerDto>> CreateCustomerAsync(
        CreateCustomerRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return ApiResponse.Fail<CustomerDto>("Customer name is required.", "INVALID_CUSTOMER_NAME");

        if (request.CreditLimitPaise < 0)
            return ApiResponse.Fail<CustomerDto>("Credit limit cannot be negative.", "INVALID_CREDIT_LIMIT");

        string? canonicalPhone = null;
        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            try
            {
                canonicalPhone = Customer.CanonicalizePhone(request.Phone);
            }
            catch (DomainException ex)
            {
                return ApiResponse.Fail<CustomerDto>(ex.Message, ex.ErrorCode);
            }

            if (canonicalPhone != null)
            {
                var existing = await _customerRepo.GetByPhoneAsync(canonicalPhone, cancellationToken);
                if (existing != null)
                    return ApiResponse.Fail<CustomerDto>(
                        string.Format(CultureInfo.InvariantCulture, "A customer with phone number '{0}' already exists ({1}).", canonicalPhone, existing.Name),
                        "PHONE_ALREADY_EXISTS");
            }
        }

        string? canonicalGstin = null;
        if (!string.IsNullOrWhiteSpace(request.Gstin))
        {
            try
            {
                canonicalGstin = Customer.ValidateAndNormalizeGstin(request.Gstin);
            }
            catch (DomainException ex)
            {
                return ApiResponse.Fail<CustomerDto>(ex.Message, ex.ErrorCode);
            }
        }

        var customer = new Customer(
            Guid.NewGuid(),
            request.Name,
            canonicalPhone,
            request.Email,
            request.Address,
            canonicalGstin,
            creditLimit: request.CreditLimitPaise / 100m,
            notes: request.Notes);

        await _customerRepo.AddAsync(customer, cancellationToken);

        await _auditRepo.RecordAsync(
            "Customer",
            "CUSTOMER_CREATED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Created customer '{0}' (Phone: {1}, Credit Limit: {2} paise).", customer.Name, customer.Phone ?? "N/A", customer.CreditLimit.MinorUnits),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(customer));
    }

    public async Task<ApiResponse<CustomerDto>> UpdateCustomerAsync(
        Guid id,
        UpdateCustomerRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        var customer = await _customerRepo.GetByIdTrackedAsync(id, cancellationToken);
        if (customer == null)
            return ApiResponse.Fail<CustomerDto>("Customer not found.", "CUSTOMER_NOT_FOUND");

        string? canonicalPhone = null;
        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            try
            {
                canonicalPhone = Customer.CanonicalizePhone(request.Phone);
            }
            catch (DomainException ex)
            {
                return ApiResponse.Fail<CustomerDto>(ex.Message, ex.ErrorCode);
            }

            if (canonicalPhone != null && canonicalPhone != customer.Phone)
            {
                var existing = await _customerRepo.GetByPhoneAsync(canonicalPhone, cancellationToken);
                if (existing != null && existing.Id != id)
                    return ApiResponse.Fail<CustomerDto>(
                        string.Format(CultureInfo.InvariantCulture, "A customer with phone number '{0}' already exists ({1}).", canonicalPhone, existing.Name),
                        "PHONE_ALREADY_EXISTS");
            }
        }

        string? canonicalGstin = null;
        if (!string.IsNullOrWhiteSpace(request.Gstin))
        {
            try
            {
                canonicalGstin = Customer.ValidateAndNormalizeGstin(request.Gstin);
            }
            catch (DomainException ex)
            {
                return ApiResponse.Fail<CustomerDto>(ex.Message, ex.ErrorCode);
            }
        }

        customer.UpdateProfile(request.Name, canonicalPhone, request.Email, request.Address, canonicalGstin, request.Notes);

        await _auditRepo.RecordAsync(
            "Customer",
            "CUSTOMER_UPDATED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Updated customer '{0}' (ID: {1}).", customer.Name, customer.Id),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(customer));
    }

    public async Task<ApiResponse<CustomerDto>> UpdateCreditLimitAsync(
        Guid id,
        UpdateCreditLimitRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.CreditLimitPaise < 0)
            return ApiResponse.Fail<CustomerDto>("Credit limit cannot be negative.", "INVALID_CREDIT_LIMIT");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse.Fail<CustomerDto>("Reason is mandatory when updating credit limit.", "REASON_REQUIRED");

        var customer = await _customerRepo.GetByIdTrackedAsync(id, cancellationToken);
        if (customer == null)
            return ApiResponse.Fail<CustomerDto>("Customer not found.", "CUSTOMER_NOT_FOUND");

        var oldLimit = customer.CreditLimit.MinorUnits;
        customer.UpdateCreditLimit(Money.FromMinorUnits(request.CreditLimitPaise, Currency.INR));

        await _auditRepo.RecordAsync(
            "Customer",
            "CUSTOMER_CREDIT_LIMIT_UPDATED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Updated credit limit for '{0}' from {1} to {2} paise. Reason: {3}", customer.Name, oldLimit, request.CreditLimitPaise, request.Reason),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(customer));
    }

    public async Task<ApiResponse<CustomerDto>> SetStatusAsync(
        Guid id,
        SetCustomerStatusRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        var customer = await _customerRepo.GetByIdTrackedAsync(id, cancellationToken);
        if (customer == null)
            return ApiResponse.Fail<CustomerDto>("Customer not found.", "CUSTOMER_NOT_FOUND");

        if (request.IsActive)
            customer.Activate();
        else
            customer.Deactivate();

        await _auditRepo.RecordAsync(
            "Customer",
            "CUSTOMER_STATUS_CHANGED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Customer '{0}' active status set to {1}. Reason: {2}", customer.Name, request.IsActive, request.Reason),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(customer));
    }

    public async Task<ApiResponse<CustomerDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _customerRepo.GetByIdAsync(id, cancellationToken);
        if (customer == null)
            return ApiResponse.Fail<CustomerDto>("Customer not found.", "CUSTOMER_NOT_FOUND");

        return ApiResponse.Ok(MapToDto(customer));
    }

    public async Task<ApiResponse<CustomerDto>> GetByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        string? canonical;
        try
        {
            canonical = Customer.CanonicalizePhone(phone);
        }
        catch (DomainException ex)
        {
            return ApiResponse.Fail<CustomerDto>(ex.Message, ex.ErrorCode);
        }

        if (canonical == null)
            return ApiResponse.Fail<CustomerDto>("Phone number is required.", "PHONE_REQUIRED");

        var customer = await _customerRepo.GetByPhoneAsync(canonical, cancellationToken);
        if (customer == null)
            return ApiResponse.Fail<CustomerDto>("Customer not found for phone number.", "CUSTOMER_NOT_FOUND");

        return ApiResponse.Ok(MapToDto(customer));
    }

    public async Task<ApiResponse<PagedResult<CustomerDto>>> SearchAsync(
        string? searchTerm,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var clampedPage = Math.Max(1, page);
        var clampedSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var (items, totalCount) = await _customerRepo.SearchAsync(searchTerm, isActive, clampedPage, clampedSize, cancellationToken);
        var dtos = items.Select(MapToDto).ToList();

        return ApiResponse.Ok(new PagedResult<CustomerDto>(dtos, totalCount, clampedPage, clampedSize));
    }

    public async Task<ApiResponse<CustomerPaymentDto>> RecordPaymentAsync(
        Guid customerId,
        RecordCustomerPaymentRequest request,
        Guid userId,
        string executingUser,
        Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientTransactionId == Guid.Empty)
            return ApiResponse.Fail<CustomerPaymentDto>("ClientTransactionId is required.", "CLIENT_TX_REQUIRED");

        if (request.AmountPaise <= 0)
            return ApiResponse.Fail<CustomerPaymentDto>("Payment amount must be positive.", "INVALID_PAYMENT_AMOUNT");

        if (request.Method == PaymentMethod.Credit || request.Method == PaymentMethod.Mixed)
            return ApiResponse.Fail<CustomerPaymentDto>("Payment method cannot be Credit or Mixed for repayments.", "INVALID_PAYMENT_METHOD");

        if (request.Method != PaymentMethod.Cash && string.IsNullOrWhiteSpace(request.ReferenceNumber))
            return ApiResponse.Fail<CustomerPaymentDto>($"Reference number is mandatory for {request.Method} payments.", "PAYMENT_REFERENCE_REQUIRED");

        // Idempotency fast-path
        var existingPayment = await _customerRepo.GetPaymentByClientTxIdAsync(request.ClientTransactionId, cancellationToken);
        if (existingPayment != null)
            return ApiResponse.Ok(MapPaymentToDto(existingPayment));

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _unitOfWork.ResetChangeTracker();

            existingPayment = await _customerRepo.GetPaymentByClientTxIdAsync(request.ClientTransactionId, cancellationToken);
            if (existingPayment != null)
                return ApiResponse.Ok(MapPaymentToDto(existingPayment));

            try
            {
                await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

                var customer = await _customerRepo.GetByIdTrackedAsync(customerId, cancellationToken);
                if (customer == null)
                    throw new DomainException("Customer not found.", "CUSTOMER_NOT_FOUND");

                if (!customer.IsActive)
                    throw new DomainException("Customer is inactive. Cannot record repayment.", "CUSTOMER_INACTIVE");

                var store = await _storeRepo.GetStoreAsync(cancellationToken);
                if (store == null)
                    throw new DomainException("Store configuration not found.", "STORE_CONFIG_NOT_FOUND");

                var paymentMoney = Money.FromMinorUnits(request.AmountPaise, Currency.INR);

                var payment = new CustomerPayment(
                    Guid.NewGuid(),
                    store.Id,
                    customer.Id,
                    paymentMoney,
                    request.Method,
                    request.ReferenceNumber,
                    DateTime.UtcNow,
                    request.ClientTransactionId,
                    userId,
                    request.Notes);

                // Update customer balances (debt reduction; excess to store credit)
                customer.RecordRepayment(paymentMoney);

                // Update active cash session if cash repayment
                if (request.Method == PaymentMethod.Cash)
                {
                    var cashSession = await _cashSessionRepo.GetActiveSessionAsync(terminalId, cancellationToken);
                    if (cashSession != null)
                    {
                        cashSession.AddCashRepayment(paymentMoney);
                    }
                }

                await _customerRepo.AddPaymentAsync(payment, cancellationToken);

                await _auditRepo.RecordAsync(
                    "Customer",
                    "CUSTOMER_REPAYMENT_RECORDED",
                    executingUser,
                    string.Format(CultureInfo.InvariantCulture, "Recorded {0} payment of {1} paise for customer '{2}'. Outstanding: {3} paise, CreditBalance: {4} paise.", request.Method, request.AmountPaise, customer.Name, customer.OutstandingCredit.MinorUnits, customer.CreditBalance.MinorUnits),
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

        return ApiResponse.Fail<CustomerPaymentDto>("Failed to record customer payment due to concurrency.", "CONCURRENCY_ERROR");
    }

    public async Task<ApiResponse<IReadOnlyList<CustomerPaymentDto>>> GetPaymentsAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var payments = await _customerRepo.GetPaymentsByCustomerIdAsync(customerId, cancellationToken);
        var dtos = payments.Select(MapPaymentToDto).ToList();
        return ApiResponse.Ok<IReadOnlyList<CustomerPaymentDto>>(dtos);
    }

    public async Task<ApiResponse<IReadOnlyList<CustomerLedgerEntryDto>>> GetLedgerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var customer = await _customerRepo.GetByIdAsync(customerId, cancellationToken);
        if (customer == null)
            return ApiResponse.Fail<IReadOnlyList<CustomerLedgerEntryDto>>("Customer not found.", "CUSTOMER_NOT_FOUND");

        var sales = await _customerRepo.GetSalesByCustomerIdAsync(customerId, cancellationToken);
        var payments = await _customerRepo.GetPaymentsByCustomerIdAsync(customerId, cancellationToken);

        var timeline = new List<(DateTime Time, string Type, string Ref, long DebtDelta, long CreditDelta, string? Notes)>();

        foreach (var s in sales)
        {
            var creditPayment = s.Payments.FirstOrDefault(p => p.Method == PaymentMethod.Credit);
            if (creditPayment != null && creditPayment.Amount.MinorUnits > 0)
            {
                timeline.Add((s.CreatedAtUtc, "CreditSale", s.InvoiceNumber, creditPayment.Amount.MinorUnits, 0, $"Sale #{s.InvoiceNumber}"));
            }
        }

        foreach (var p in payments)
        {
            timeline.Add((p.PaymentDateUtc, "DebtRepayment", p.ReferenceNumber ?? p.Id.ToString(), 0, 0, $"Payment via {p.Method}"));
        }

        timeline.Sort((a, b) => a.Time.CompareTo(b.Time));

        long runningDebt = 0;
        long runningCredit = 0;
        var ledger = new List<CustomerLedgerEntryDto>();

        foreach (var item in timeline)
        {
            if (item.Type == "CreditSale")
            {
                long saleAmount = item.DebtDelta;
                long offset = 0;
                if (runningCredit > 0)
                {
                    offset = Math.Min(runningCredit, saleAmount);
                    runningCredit -= offset;
                    saleAmount -= offset;
                }
                runningDebt += saleAmount;

                ledger.Add(new CustomerLedgerEntryDto(
                    item.Time,
                    "CreditSale",
                    item.Ref,
                    DebtChangePaise: item.DebtDelta - offset,
                    StoreCreditChangePaise: -offset,
                    RunningOutstandingCreditPaise: runningDebt,
                    RunningCreditBalancePaise: runningCredit,
                    item.Notes));
            }
            else if (item.Type == "DebtRepayment")
            {
                var p = payments.First(pay => (pay.ReferenceNumber ?? pay.Id.ToString()) == item.Ref);
                long payAmount = p.Amount.MinorUnits;
                long debtPaid;
                long storeCreditAdd = 0;

                if (payAmount <= runningDebt)
                {
                    debtPaid = payAmount;
                    runningDebt -= debtPaid;
                }
                else
                {
                    debtPaid = runningDebt;
                    storeCreditAdd = payAmount - runningDebt;
                    runningDebt = 0;
                    runningCredit += storeCreditAdd;
                }

                ledger.Add(new CustomerLedgerEntryDto(
                    item.Time,
                    "DebtRepayment",
                    item.Ref,
                    DebtChangePaise: -debtPaid,
                    StoreCreditChangePaise: storeCreditAdd,
                    RunningOutstandingCreditPaise: runningDebt,
                    RunningCreditBalancePaise: runningCredit,
                    item.Notes));
            }
        }

        return ApiResponse.Ok<IReadOnlyList<CustomerLedgerEntryDto>>(ledger);
    }

        public async Task<ApiResponse<IReadOnlyList<SaleInvoiceDto>>> GetSalesAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var sales = await _customerRepo.GetSalesByCustomerIdAsync(customerId, cancellationToken);
        var dtos = sales.Select(s =>
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
                paymentDtos);
        }).ToList();

        return ApiResponse.Ok<IReadOnlyList<SaleInvoiceDto>>(dtos);
    }

    private static CustomerDto MapToDto(Customer c) => new(
        c.Id,
        c.Name,
        c.Phone,
        c.Email,
        c.Address,
        c.Gstin,
        c.OutstandingCredit.MinorUnits,
        c.CreditBalance.MinorUnits,
        c.CreditLimit.MinorUnits,
        c.IsActive,
        c.Notes,
        c.CreatedAtUtc,
        c.UpdatedAtUtc);

    private static CustomerPaymentDto MapPaymentToDto(CustomerPayment p) => new(
        p.Id,
        p.CustomerId,
        p.Amount.MinorUnits,
        p.Method,
        p.ReferenceNumber,
        p.PaymentDateUtc,
        p.ClientTransactionId,
        p.Notes,
        p.CreatedByUserId,
        p.CreatedAtUtc);
}
