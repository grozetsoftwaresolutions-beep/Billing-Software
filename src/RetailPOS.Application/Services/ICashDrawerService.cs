using RetailPOS.Application.Hardware;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public interface ICashDrawerService
{
    DrawerKickDirective EvaluateSaleDrawerKick(SaleInvoice invoice);
    DrawerKickDirective EvaluateRefundDrawerKick(SaleRefund refund);
    DrawerKickDirective EvaluateCustomerPaymentDrawerKick(PaymentMethod method, long amountMinorUnits);
    DrawerKickDirective EvaluateCashSessionDrawerKick(CashTransactionType transactionType, long amountMinorUnits);
    Task<DrawerKickResponseDto> TriggerDrawerKickAsync(Guid terminalId, Guid userId, string reason, bool isManualOpen = false, CancellationToken cancellationToken = default);
    Task RecordAutomatedDrawerKickAuditAsync(Guid terminalId, Guid userId, string transactionType, Guid transactionId, bool success, string? errorMessage, CancellationToken cancellationToken = default);
}

public sealed class CashDrawerService : ICashDrawerService
{
    private readonly ICashDrawer _drawer;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public CashDrawerService(
        ICashDrawer drawer,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _drawer = drawer;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public DrawerKickDirective EvaluateSaleDrawerKick(SaleInvoice invoice)
    {
        var cashPayments = invoice.Payments.Where(p => p.Method == PaymentMethod.Cash && p.Amount.MinorUnits > 0).ToList();
        var hasCash = cashPayments.Count > 0;
        var cashAmount = hasCash ? cashPayments.Sum(p => p.Amount.MinorUnits) : (long?)null;
        var reason = hasCash ? (invoice.Payments.Count > 1 ? "MIXED_CASH_SALE" : "CASH_SALE") : null;
        return new DrawerKickDirective(hasCash, reason, cashAmount);
    }

    public DrawerKickDirective EvaluateRefundDrawerKick(SaleRefund refund)
    {
        var isCash = refund.RefundMethod == PaymentMethod.Cash && refund.TotalRefundAmount.MinorUnits > 0;
        return new DrawerKickDirective(isCash, isCash ? "CASH_REFUND" : null, isCash ? refund.TotalRefundAmount.MinorUnits : null);
    }

    public DrawerKickDirective EvaluateCustomerPaymentDrawerKick(PaymentMethod method, long amountMinorUnits)
    {
        var isCash = method == PaymentMethod.Cash && amountMinorUnits > 0;
        return new DrawerKickDirective(isCash, isCash ? "CASH_CUSTOMER_REPAYMENT" : null, isCash ? amountMinorUnits : null);
    }

    public DrawerKickDirective EvaluateCashSessionDrawerKick(CashTransactionType transactionType, long amountMinorUnits)
    {
        var isCash = transactionType is CashTransactionType.PayIn or CashTransactionType.PayOut or CashTransactionType.CashDrop;
        var reason = transactionType.ToString().ToUpperInvariant();
        return new DrawerKickDirective(isCash, isCash ? reason : null, isCash ? amountMinorUnits : null);
    }

    public async Task<DrawerKickResponseDto> TriggerDrawerKickAsync(
        Guid terminalId,
        Guid userId,
        string reason,
        bool isManualOpen = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _drawer.OpenDrawerAsync(reason, cancellationToken);
        var action = result.Success
            ? (isManualOpen ? "DRAWER_OPENED_MANUALLY" : "DRAWER_TEST_KICK")
            : "DRAWER_KICK_FAILED";

        var details = $"[Terminal:{terminalId}] Drawer open '{reason}' via {result.DrawerType}" + (result.Success ? "" : $": {result.ErrorMessage}");

        await _auditRepo.RecordAsync("Terminal", action, userId.ToString(), details, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new DrawerKickResponseDto(result.Success, result.DrawerType, result.ErrorMessage, DateTime.UtcNow);
    }

    public async Task RecordAutomatedDrawerKickAuditAsync(
        Guid terminalId,
        Guid userId,
        string transactionType,
        Guid transactionId,
        bool success,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        string action;
        if (!success)
        {
            action = "DRAWER_KICK_FAILED";
        }
        else
        {
            action = transactionType.ToUpperInvariant() switch
            {
                "SALE" => "DRAWER_OPENED_SALE",
                "REFUND" => "DRAWER_OPENED_REFUND",
                _ => "DRAWER_OPENED_MANUALLY"
            };
        }

        var details = $"[Terminal:{terminalId}] Automated drawer kick for {transactionType} {transactionId}" +
            (success ? " succeeded" : $": {errorMessage}");

        await _auditRepo.RecordAsync("Drawer", action, userId.ToString(), details, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
