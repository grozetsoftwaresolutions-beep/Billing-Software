using RetailPOS.Application.Security;

namespace RetailPOS.Server.Authentication;

public interface ILocalSessionValidator
{
    Task<SessionValidationResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default);
}
