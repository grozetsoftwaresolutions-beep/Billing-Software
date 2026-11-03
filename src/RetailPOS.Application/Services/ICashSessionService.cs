using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public interface ICashSessionService
{
    Task<ApiResponse<CashSessionDto>> OpenSessionAsync(OpenCashSessionRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> GetActiveSessionAsync(Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionTransactionDto>> AddTransactionAsync(Guid sessionId, AddCashTransactionRequest request, Guid userId, string executingUser, Guid? authorizedByUserId, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> CloseSessionAsync(Guid sessionId, CloseCashSessionRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> ForceCloseSessionAsync(Guid sessionId, ForceCloseCashSessionRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionReportDto>> GetXReportAsync(Guid sessionId, Guid? callerUserId = null, RoleType? callerRole = null, Guid? callerTerminalId = null, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionReportDto>> GetZReportAsync(Guid sessionId, Guid? callerUserId = null, RoleType? callerRole = null, Guid? callerTerminalId = null, CancellationToken cancellationToken = default);
}
