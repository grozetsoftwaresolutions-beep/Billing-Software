using RetailPOS.Application.Hardware;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Hardware;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Hardware;

namespace RetailPOS.Application.Services;

public sealed record RenderedReceiptPayload(
    byte[] RawBytes,
    string AsciiPreview,
    int PendingReprintCount);

public interface IReceiptService
{
    Task<RenderedReceiptPayload> GenerateSaleReceiptPayloadAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        bool isReprint = false,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResponseDto> PrintDirectSaleReceiptAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        Guid cashierId,
        bool isReprint = false,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResponseDto> ConfirmReprintAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        Guid userId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResponseDto> ReportReprintFailedAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        Guid userId,
        string errorMessage,
        CancellationToken cancellationToken = default);

    Task<TestPrintResponseDto> PrintTestReceiptAsync(
        TestPrintRequestDto request,
        Guid terminalId,
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed class ReceiptService : IReceiptService
{
    private readonly ISaleRepository _saleRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly ITerminalHardwareConfigRepository _hwConfigRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IReceiptPrinter _printer;
    private readonly ICustomerRepository? _customerRepo;

    public ReceiptService(
        ISaleRepository saleRepo,
        IStoreRepository storeRepo,
        ITerminalHardwareConfigRepository hwConfigRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork,
        IReceiptPrinter printer,
        ICustomerRepository? customerRepo = null)
    {
        _saleRepo = saleRepo;
        _storeRepo = storeRepo;
        _hwConfigRepo = hwConfigRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
        _printer = printer;
        _customerRepo = customerRepo;
    }

    public async Task<RenderedReceiptPayload> GenerateSaleReceiptPayloadAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        bool isReprint = false,
        CancellationToken cancellationToken = default)
    {
        var invoice = await _saleRepo.GetByIdAsync(saleInvoiceId, cancellationToken);
        if (invoice == null)
            throw new DomainException($"Sale invoice with ID '{saleInvoiceId}' not found.", "SALE_NOT_FOUND");

        var store = await _storeRepo.GetStoreAsync(cancellationToken);
        var storeInfo = new ReceiptStoreInfo(
            store?.StoreName ?? "Retail Supermarket",
            store?.Address,
            store?.Phone,
            store?.TaxRegistrationNumber,
            store?.ReceiptFooterMessage);

        var config = await _hwConfigRepo.GetByTerminalIdAsync(terminalId, cancellationToken);
        var paperWidth = config?.PaperWidth ?? PaperWidth.Width80mm;
        var encoding = config?.PrinterEncoding ?? PrinterEncoding.Ascii;
        var codePage = config?.PrinterCodePage ?? 0;
        var autoCut = config?.AutoCut ?? true;
        var openDrawer = (config?.OpenDrawerOnCashSale ?? true) && invoice.Payments.Any(p => p.Method == PaymentMethod.Cash && p.Amount.MinorUnits > 0);

        string? custName = null;
        string? custPhone = null;
        long? custBalance = null;
        if (invoice.CustomerId.HasValue && _customerRepo != null)
        {
            var cust = await _customerRepo.GetByIdAsync(invoice.CustomerId.Value, cancellationToken);
            if (cust != null)
            {
                custName = cust.Name;
                custPhone = cust.Phone;
                custBalance = cust.OutstandingCredit.MinorUnits;
            }
        }

        var invInfo = new ReceiptInvoiceInfo(
            invoice.InvoiceNumber,
            invoice.CreatedAtUtc,
            "Cashier",
            terminalId.ToString()[..8].ToUpperInvariant(),
            custName,
            custPhone,
            custBalance);

        var items = invoice.Items.Select(i => new ReceiptLineItem(
            i.ProductName,
            i.Quantity.Value,
            i.Quantity.Unit.ToString(),
            i.UnitSellingPrice.MinorUnits,
            i.DiscountAmount.MinorUnits,
            i.LineTotal.MinorUnits)).ToList();

        var taxes = invoice.Items
            .SelectMany(i => i.TaxComponents)
            .GroupBy(c => new { c.ComponentName, c.RatePercentage })
            .Select(g => new ReceiptTaxLine(g.Key.ComponentName, g.Key.RatePercentage, g.Sum(x => x.Amount.MinorUnits)))
            .ToList();

        var payments = invoice.Payments.Select(p => new ReceiptPaymentLine(
            p.Method,
            p.Amount.MinorUnits,
            p.TenderedAmount.MinorUnits,
            p.ChangeAmount.MinorUnits)).ToList();

        var summary = new ReceiptFinancialSummary(
            invoice.GrossSubtotal.MinorUnits,
            invoice.TotalLineDiscounts.MinorUnits,
            invoice.InvoiceDiscount.MinorUnits,
            invoice.TaxableSubtotal.MinorUnits,
            invoice.TotalTax.MinorUnits,
            invoice.RoundingAdjustment.MinorUnits,
            invoice.FinalPayable.MinorUnits,
            items,
            taxes,
            payments);

        var pendingReprintCount = isReprint ? invoice.ReprintCount + 1 : invoice.ReprintCount;
        var options = new ReceiptRenderOptions(
            paperWidth,
            encoding,
            codePage,
            autoCut,
            openDrawer,
            isReprint,
            pendingReprintCount,
            DateTime.UtcNow,
            store?.TimeZoneId);

        var builder = ReceiptLayoutEngine.BuildReceipt(storeInfo, invInfo, summary, options);
        return new RenderedReceiptPayload(builder.ToBytes(), builder.ToAsciiPreview(), pendingReprintCount);
    }

    public async Task<ReceiptPrintResponseDto> PrintDirectSaleReceiptAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        Guid cashierId,
        bool isReprint = false,
        CancellationToken cancellationToken = default)
    {
        var invoice = await _saleRepo.GetByIdAsync(saleInvoiceId, cancellationToken);
        if (invoice == null)
            throw new DomainException($"Sale invoice with ID '{saleInvoiceId}' not found.", "SALE_NOT_FOUND");

        RenderedReceiptPayload payload;
        try
        {
            payload = await GenerateSaleReceiptPayloadAsync(saleInvoiceId, terminalId, isReprint, cancellationToken);
        }
        catch (Exception ex)
        {
            await LogAuditAsync("RECEIPT_PRINT_FAILED", invoice.Id.ToString(), cashierId, terminalId, $"Layout generation failed: {ex.Message}", cancellationToken);
            return new ReceiptPrintResponseDto(invoice.Id, invoice.InvoiceNumber, PrintStatus.Failed, ex.Message, isReprint, invoice.ReprintCount, DateTime.UtcNow);
        }

        var printResult = await _printer.PrintRawBytesAsync(payload.RawBytes, cancellationToken);
        if (!printResult.Success)
        {
            var auditAction = isReprint ? "REPRINT_FAILED" : "RECEIPT_PRINT_FAILED";
            await LogAuditAsync(auditAction, invoice.Id.ToString(), cashierId, terminalId, $"Print I/O failed: {printResult.ErrorMessage}", cancellationToken);
            return new ReceiptPrintResponseDto(invoice.Id, invoice.InvoiceNumber, PrintStatus.Failed, printResult.ErrorMessage, isReprint, invoice.ReprintCount, DateTime.UtcNow);
        }

        // Print Succeeded
        if (isReprint)
        {
            var nowUtc = DateTime.UtcNow;
            await _saleRepo.IncrementReprintCountAsync(saleInvoiceId, nowUtc, cancellationToken);
            invoice = await _saleRepo.GetByIdAsync(saleInvoiceId, cancellationToken) ?? invoice;
            await LogAuditAsync("RECEIPT_REPRINTED", invoice.Id.ToString(), cashierId, terminalId, $"Reprint #{invoice.ReprintCount} for {invoice.InvoiceNumber}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await LogAuditAsync("RECEIPT_PRINTED", invoice.Id.ToString(), cashierId, terminalId, $"Receipt printed for {invoice.InvoiceNumber}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new ReceiptPrintResponseDto(invoice.Id, invoice.InvoiceNumber, PrintStatus.Success, null, isReprint, invoice.ReprintCount, DateTime.UtcNow);
    }

    public async Task<ReceiptPrintResponseDto> ConfirmReprintAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        Guid userId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var invoice = await _saleRepo.GetByIdAsync(saleInvoiceId, cancellationToken);
        if (invoice == null)
            throw new DomainException($"Sale invoice with ID '{saleInvoiceId}' not found.", "SALE_NOT_FOUND");

        var nowUtc = DateTime.UtcNow;
        await _saleRepo.IncrementReprintCountAsync(saleInvoiceId, nowUtc, cancellationToken);
        invoice = await _saleRepo.GetByIdAsync(saleInvoiceId, cancellationToken) ?? invoice;

        var details = $"Reprint #{invoice.ReprintCount} for {invoice.InvoiceNumber}" + (string.IsNullOrWhiteSpace(reason) ? "" : $". Reason: {reason}");
        await LogAuditAsync("RECEIPT_REPRINTED", invoice.Id.ToString(), userId, terminalId, details, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ReceiptPrintResponseDto(invoice.Id, invoice.InvoiceNumber, PrintStatus.Success, null, true, invoice.ReprintCount, nowUtc);
    }

    public async Task<ReceiptPrintResponseDto> ReportReprintFailedAsync(
        Guid saleInvoiceId,
        Guid terminalId,
        Guid userId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        var invoice = await _saleRepo.GetByIdAsync(saleInvoiceId, cancellationToken);
        if (invoice == null)
            throw new DomainException($"Sale invoice with ID '{saleInvoiceId}' not found.", "SALE_NOT_FOUND");

        await LogAuditAsync("REPRINT_FAILED", invoice.Id.ToString(), userId, terminalId, $"Reprint failed for {invoice.InvoiceNumber}: {errorMessage}", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new ReceiptPrintResponseDto(invoice.Id, invoice.InvoiceNumber, PrintStatus.Failed, errorMessage, true, invoice.ReprintCount, DateTime.UtcNow);
    }

    public async Task<TestPrintResponseDto> PrintTestReceiptAsync(
        TestPrintRequestDto request,
        Guid terminalId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var store = await _storeRepo.GetStoreAsync(cancellationToken);
        var storeInfo = new ReceiptStoreInfo(
            request.CustomHeader ?? store?.StoreName ?? "Retail Supermarket",
            store?.Address,
            store?.Phone,
            store?.TaxRegistrationNumber,
            "Diagnostic Test Receipt");

        var printerName = request.PrinterName ?? "SimulatedPrinter";
        var builder = ReceiptLayoutEngine.BuildDiagnosticTestReceipt(
            storeInfo,
            terminalId.ToString()[..8].ToUpperInvariant(),
            printerName,
            request.PaperWidth,
            PrinterEncoding.Ascii,
            0,
            request.OpenDrawer);

        var bytes = builder.ToBytes();
        var printResult = await _printer.PrintRawBytesAsync(bytes, cancellationToken);

        await LogAuditAsync("TEST_PRINT_EXECUTED", terminalId.ToString(), userId, terminalId, $"Diagnostic test print on {printerName} ({request.PaperWidth})", cancellationToken);

        return new TestPrintResponseDto(printResult.Success, printerName, request.PaperWidth, bytes.Length, printResult.ErrorMessage, DateTime.UtcNow);
    }

    private Task LogAuditAsync(string action, string entityId, Guid userId, Guid terminalId, string details, CancellationToken ct)
    {
        return _auditRepo.RecordAsync("SaleInvoice", action, userId.ToString(), $"[Terminal:{terminalId}] {details}", ct);
    }
}
