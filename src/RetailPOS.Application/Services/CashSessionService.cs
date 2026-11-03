using System.Globalization;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public sealed class CashSessionService : ICashSessionService
{
    private readonly ICashSessionRepository _sessionRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public CashSessionService(
        ICashSessionRepository sessionRepo,
        IStoreRepository storeRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _sessionRepo = sessionRepo;
        _storeRepo = storeRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<CashSessionDto>> OpenSessionAsync(
        OpenCashSessionRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.TerminalId == Guid.Empty)
            return ApiResponse.Fail<CashSessionDto>("Terminal ID is required.", "INVALID_TERMINAL_ID");

        if (request.OpeningFloatPaise < 0)
            return ApiResponse.Fail<CashSessionDto>("Opening float cannot be negative.", "INVALID_OPENING_FLOAT");

        var existing = await _sessionRepo.GetActiveSessionAsync(request.TerminalId, cancellationToken);
        if (existing != null)
            return ApiResponse.Fail<CashSessionDto>("An active cash session already exists for this terminal.", "ACTIVE_SESSION_EXISTS");

        var store = await _storeRepo.GetStoreAsync(cancellationToken);
        if (store == null)
            return ApiResponse.Fail<CashSessionDto>("Store configuration not found.", "STORE_CONFIG_NOT_FOUND");

        var floatMoney = Money.FromMinorUnits(request.OpeningFloatPaise, Currency.INR);
        var session = new CashSession(
            Guid.NewGuid(),
            store.Id,
            request.TerminalId,
            userId,
            floatMoney);

        await _sessionRepo.AddSessionAsync(session, cancellationToken);

        await _auditRepo.RecordAsync(
            "CashSession",
            "CASH_SESSION_OPENED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Cash session opened on terminal {0} with float of {1} paise.", request.TerminalId, request.OpeningFloatPaise),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(session));
    }

    public async Task<ApiResponse<CashSessionDto>> GetActiveSessionAsync(Guid terminalId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionRepo.GetActiveSessionAsync(terminalId, cancellationToken);
        if (session == null)
            return ApiResponse.Fail<CashSessionDto>("No active cash session found for terminal.", "NO_ACTIVE_SESSION");

        return ApiResponse.Ok(MapToDto(session));
    }

    public async Task<ApiResponse<CashSessionDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var session = await _sessionRepo.GetByIdAsync(id, cancellationToken);
        if (session == null)
            return ApiResponse.Fail<CashSessionDto>("Cash session not found.", "SESSION_NOT_FOUND");

        return ApiResponse.Ok(MapToDto(session));
    }

    public async Task<ApiResponse<CashSessionTransactionDto>> AddTransactionAsync(
        Guid sessionId,
        AddCashTransactionRequest request,
        Guid userId,
        string executingUser,
        Guid? authorizedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientTransactionId == Guid.Empty)
            return ApiResponse.Fail<CashSessionTransactionDto>("ClientTransactionId is required.", "CLIENT_TX_REQUIRED");

        if (request.AmountPaise <= 0)
            return ApiResponse.Fail<CashSessionTransactionDto>("Transaction amount must be positive.", "INVALID_AMOUNT");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse.Fail<CashSessionTransactionDto>("Reason is mandatory.", "REASON_REQUIRED");

        // Idempotency fast-path
        var existingTx = await _sessionRepo.GetTransactionByClientTxIdAsync(request.ClientTransactionId, cancellationToken);
        if (existingTx != null)
            return ApiResponse.Ok(MapTxToDto(existingTx));

        var session = await _sessionRepo.GetByIdTrackedAsync(sessionId, cancellationToken);
        if (session == null)
            return ApiResponse.Fail<CashSessionTransactionDto>("Cash session not found.", "SESSION_NOT_FOUND");

        if (session.Status != CashSessionStatus.Open)
            return ApiResponse.Fail<CashSessionTransactionDto>("Cash session is closed.", "SESSION_ALREADY_CLOSED");

        var amountMoney = Money.FromMinorUnits(request.AmountPaise, Currency.INR);

        var tx = new CashSessionTransaction(
            0,
            session.Id,
            request.Type,
            amountMoney,
            request.Reason,
            request.ClientTransactionId,
            userId,
            authorizedByUserId,
            request.SaleInvoiceId,
            request.RefundId,
            request.CustomerPaymentId);

        switch (request.Type)
        {
            case CashTransactionType.PayIn:
                session.AddPayIn(amountMoney);
                break;
            case CashTransactionType.PayOut:
                session.AddPayOut(amountMoney);
                break;
            case CashTransactionType.CashDrop:
                session.AddCashDrop(amountMoney);
                break;
        }

        await _sessionRepo.AddTransactionAsync(tx, cancellationToken);

        await _auditRepo.RecordAsync(
            "CashSession",
            "CASH_TRANSACTION_RECORDED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Recorded {0} of {1} paise on session {2}. Reason: {3}", request.Type, request.AmountPaise, session.Id, request.Reason),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapTxToDto(tx));
    }

    public async Task<ApiResponse<CashSessionDto>> CloseSessionAsync(
        Guid sessionId,
        CloseCashSessionRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.CountedCashPaise < 0)
            return ApiResponse.Fail<CashSessionDto>("Counted cash cannot be negative.", "INVALID_CLOSING_CASH");

        var session = await _sessionRepo.GetByIdTrackedAsync(sessionId, cancellationToken);
        if (session == null)
            return ApiResponse.Fail<CashSessionDto>("Cash session not found.", "SESSION_NOT_FOUND");

        if (session.Status != CashSessionStatus.Open)
            return ApiResponse.Fail<CashSessionDto>("Cash session is already closed.", "SESSION_ALREADY_CLOSED");

        if (session.CashierUserId != userId)
            return ApiResponse.Fail<CashSessionDto>("Only the opening cashier can perform normal close. Use Force Close for administrative close.", "UNAUTHORIZED_CLOSE");

        var countedMoney = Money.FromMinorUnits(request.CountedCashPaise, Currency.INR);
        session.Close(countedMoney, request.Notes, userId);

        await _auditRepo.RecordAsync(
            "CashSession",
            "CASH_SESSION_CLOSED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Closed cash session {0}. Expected: {1} paise, Counted: {2} paise, Discrepancy: {3} paise.", session.Id, session.ExpectedCash.MinorUnits, session.ClosingCash?.MinorUnits, session.Discrepancy?.MinorUnits),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(session));
    }

    public async Task<ApiResponse<CashSessionDto>> ForceCloseSessionAsync(
        Guid sessionId,
        ForceCloseCashSessionRequest request,
        Guid userId,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        if (request.CountedCashPaise < 0)
            return ApiResponse.Fail<CashSessionDto>("Counted cash cannot be negative.", "INVALID_CLOSING_CASH");

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
            return ApiResponse.Fail<CashSessionDto>("A detailed force-close reason is mandatory (minimum 10 characters).", "FORCE_CLOSE_REASON_REQUIRED");

        var session = await _sessionRepo.GetByIdTrackedAsync(sessionId, cancellationToken);
        if (session == null)
            return ApiResponse.Fail<CashSessionDto>("Cash session not found.", "SESSION_NOT_FOUND");

        if (session.Status != CashSessionStatus.Open)
            return ApiResponse.Fail<CashSessionDto>("Cash session is already closed.", "SESSION_ALREADY_CLOSED");

        var countedMoney = Money.FromMinorUnits(request.CountedCashPaise, Currency.INR);
        session.ForceClose(countedMoney, request.Reason, userId);

        await _auditRepo.RecordAsync(
            "CashSession",
            "CASH_SESSION_FORCE_CLOSED",
            executingUser,
            string.Format(CultureInfo.InvariantCulture, "Force-closed cash session {0}. Expected: {1} paise, Counted: {2} paise, Discrepancy: {3} paise. Reason: {4}", session.Id, session.ExpectedCash.MinorUnits, session.ClosingCash?.MinorUnits, session.Discrepancy?.MinorUnits, request.Reason),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok(MapToDto(session));
    }

    public async Task<ApiResponse<CashSessionReportDto>> GetXReportAsync(
        Guid sessionId,
        Guid? callerUserId = null,
        RoleType? callerRole = null,
        Guid? callerTerminalId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessionRepo.GetByIdAsync(sessionId, cancellationToken);
        if (session == null)
            return ApiResponse.Fail<CashSessionReportDto>("Cash session not found.", "SESSION_NOT_FOUND");

        var authResult = ValidateReportAccess(session, callerUserId, callerRole, callerTerminalId);
        if (authResult != null)
            return authResult;

        var txs = await _sessionRepo.GetTransactionsBySessionIdAsync(sessionId, cancellationToken);
        var txDtos = txs.Select(MapTxToDto).ToList();

        var report = new CashSessionReportDto(
            MapToDto(session),
            session.CashierUserId.ToString(),
            session.ClosedByUserId?.ToString(),
            session.TerminalId.ToString(),
            txDtos);

        return ApiResponse.Ok(report);
    }

    public async Task<ApiResponse<CashSessionReportDto>> GetZReportAsync(
        Guid sessionId,
        Guid? callerUserId = null,
        RoleType? callerRole = null,
        Guid? callerTerminalId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessionRepo.GetByIdAsync(sessionId, cancellationToken);
        if (session == null)
            return ApiResponse.Fail<CashSessionReportDto>("Cash session not found.", "SESSION_NOT_FOUND");

        var authResult = ValidateReportAccess(session, callerUserId, callerRole, callerTerminalId);
        if (authResult != null)
            return authResult;

        if (session.Status != CashSessionStatus.Closed)
            return ApiResponse.Fail<CashSessionReportDto>("Z-Report is only available for closed cash sessions.", "SESSION_NOT_CLOSED");

        var txs = await _sessionRepo.GetTransactionsBySessionIdAsync(sessionId, cancellationToken);
        var txDtos = txs.Select(MapTxToDto).ToList();

        var report = new CashSessionReportDto(
            MapToDto(session),
            session.CashierUserId.ToString(),
            session.ClosedByUserId?.ToString(),
            session.TerminalId.ToString(),
            txDtos);

        return ApiResponse.Ok(report);
    }

    private static ApiResponse<CashSessionReportDto>? ValidateReportAccess(
        CashSession session,
        Guid? callerUserId,
        RoleType? callerRole,
        Guid? callerTerminalId)
    {
        if (callerRole != RoleType.Cashier)
            return null;

        if (!callerUserId.HasValue || callerUserId.Value == Guid.Empty)
            return ApiResponse.Fail<CashSessionReportDto>("Authenticated cashier identity is required.", "FORBIDDEN");

        if (!callerTerminalId.HasValue || callerTerminalId.Value == Guid.Empty)
            return ApiResponse.Fail<CashSessionReportDto>("Cashier session is not bound to a registered terminal.", "TERMINAL_REQUIRED");

        if (session.CashierUserId != callerUserId.Value)
            return ApiResponse.Fail<CashSessionReportDto>("Cashiers are only authorized to view reports for their own cash sessions.", "FORBIDDEN");

        if (session.TerminalId != callerTerminalId.Value)
            return ApiResponse.Fail<CashSessionReportDto>("Cashiers are only authorized to view reports for sessions on their assigned terminal.", "FORBIDDEN");

        return null;
    }

    private static CashSessionDto MapToDto(CashSession s) => new(
        s.Id,
        s.StoreId,
        s.TerminalId,
        s.CashierUserId,
        s.OpeningTimeUtc,
        s.ClosingTimeUtc,
        s.OpeningFloat.MinorUnits,
        s.TotalCashSales.MinorUnits,
        s.TotalCashRepayments.MinorUnits,
        s.TotalCashRefunds.MinorUnits,
        s.TotalPayIns.MinorUnits,
        s.TotalPayOuts.MinorUnits,
        s.TotalCashDrops.MinorUnits,
        s.ExpectedCash.MinorUnits,
        s.ClosingCash?.MinorUnits,
        s.Discrepancy?.MinorUnits,
        s.Status,
        s.ClosingNotes,
        s.IsForceClosed,
        s.ClosedByUserId,
        s.ForceCloseReason);

    private static CashSessionTransactionDto MapTxToDto(CashSessionTransaction tx)
    {
        var isCash = tx.Type is RetailPOS.Domain.Enums.CashTransactionType.PayIn or RetailPOS.Domain.Enums.CashTransactionType.PayOut or RetailPOS.Domain.Enums.CashTransactionType.CashDrop;
        var drawerKick = new RetailPOS.Contracts.Hardware.DrawerKickDirective(
            isCash,
            isCash ? tx.Type.ToString().ToUpperInvariant() : null,
            isCash ? tx.Amount.MinorUnits : null);

        return new(
            tx.Id,
            tx.CashSessionId,
            tx.Type,
            tx.Amount.MinorUnits,
            tx.Reason,
            tx.ClientTransactionId,
            tx.CreatedByUserId,
            tx.AuthorizedByUserId,
            tx.TimestampUtc,
            tx.SaleInvoiceId,
            tx.RefundId,
            tx.CustomerPaymentId,
            drawerKick);
    }
}
