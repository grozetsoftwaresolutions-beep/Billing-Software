using RetailPOS.Domain.Enums;

namespace RetailPOS.Server.Authentication;

public sealed record SessionValidationResult(
    bool IsValid,
    Guid? UserId = null,
    string? Username = null,
    RoleType? Role = null,
    string? ErrorMessage = null);

public interface ILocalSessionValidator
{
    Task<SessionValidationResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default);
}
