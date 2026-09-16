using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Security;

public sealed record SessionValidationResult(
    bool IsValid,
    Guid? UserId = null,
    string? Username = null,
    string? FullName = null,
    RoleType? Role = null,
    Guid? TerminalId = null,
    DateTime? ExpiresAtUtc = null,
    string? ErrorMessage = null,
    bool IsLocked = false,
    DateTime? LastActivityUtc = null,
    string? SessionIdentityHash = null);

public interface ISessionManager
{
    string CreateSession(Guid userId, string username, string fullName, RoleType role, Guid? terminalId, TimeSpan duration);

    Task<SessionValidationResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default);

    void RevokeSession(string sessionToken);

    void TouchSession(string sessionToken);

    void LockSession(string sessionToken);

    Task<bool> UnlockSessionAsync(string sessionToken, string pin, CancellationToken cancellationToken = default);

    void RevokeUserSessions(Guid userId);

    void RevokeTerminalSessions(Guid terminalId);
}
