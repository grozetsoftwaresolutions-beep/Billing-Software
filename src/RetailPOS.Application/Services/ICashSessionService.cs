using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Application.Services;

public interface ICashSessionService
{
    Task<ApiResponse<CashSessionDto>> OpenSessionAsync(OpenCashSessionRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> GetActiveSessionAsync(Guid terminalId, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionTransactionDto>> AddTransactionAsync(Guid sessionId, AddCashTransactionRequest request, Guid userId, string executingUser, Guid? authorizedByUserId, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> CloseSessionAsync(Guid sessionId, CloseCashSessionRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionDto>> ForceCloseSessionAsync(Guid sessionId, ForceCloseCashSessionRequest request, Guid userId, string executingUser, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionReportDto>> GetXReportAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ApiResponse<CashSessionReportDto>> GetZReportAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
