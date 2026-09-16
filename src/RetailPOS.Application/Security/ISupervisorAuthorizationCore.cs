using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Security;

public sealed record SupervisorAuthorizationResult(
    bool IsAuthorized,
    Guid? SupervisorUserId = null,
    string? SupervisorUsername = null,
    RoleType? SupervisorRole = null,
    string? ErrorMessage = null)
{
    public static SupervisorAuthorizationResult Success(Guid userId, string username, RoleType role) =>
        new(true, userId, username, role, null);

    public static SupervisorAuthorizationResult Failed(string error) =>
        new(false, null, null, null, error);
}

public interface ISupervisorAuthorizationCore
{
    Task<SupervisorAuthorizationResult> AuthorizeSupervisorCredentialsAsync(
        string username,
        string? pin,
        string? password,
        Permission requiredPermission,
        Guid terminalId,
        CancellationToken ct = default);
}
