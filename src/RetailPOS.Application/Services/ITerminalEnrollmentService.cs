using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public interface ITerminalEnrollmentService
{
    Task<EnrollmentTokenResponseDto> GenerateEnrollmentTokenAsync(EnrollmentTokenRequestDto request, string currentUserId, CancellationToken ct = default);
    Task<TerminalEnrollResponseDto> EnrollTerminalAsync(TerminalEnrollRequestDto request, CancellationToken ct = default);
    Task<TerminalEnrollResponseDto> RepairTerminalAsync(Guid terminalId, string repairToken, string? clientIpAddress, CancellationToken ct = default);
    Task<RePinningTokenResponseDto> GenerateRePinningTokenAsync(RePinningTokenRequestDto request, string currentUserId, CancellationToken ct = default);
    Task<RePinningResponseDto> RePinTerminalAsync(RePinningRequestDto request, CancellationToken ct = default);
    Task DecommissionTerminalAsync(Guid terminalId, string currentUserId, CancellationToken ct = default);
    Task UpdateTerminalStatusAsync(Guid terminalId, TerminalStatus status, string currentUserId, CancellationToken ct = default);
    Task<IReadOnlyList<TerminalDetailDto>> GetAllTerminalDetailsAsync(CancellationToken ct = default);
    Task<TerminalDetailDto?> GetTerminalDetailAsync(Guid terminalId, CancellationToken ct = default);
}
