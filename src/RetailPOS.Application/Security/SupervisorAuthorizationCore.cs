using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;

namespace RetailPOS.Application.Security;

public sealed class SupervisorAuthorizationCore : ISupervisorAuthorizationCore
{
    private readonly IUserRepository _userRepo;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public SupervisorAuthorizationCore(
        IUserRepository userRepo,
        IPasswordHasher passwordHasher,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _userRepo = userRepo;
        _passwordHasher = passwordHasher;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<SupervisorAuthorizationResult> AuthorizeSupervisorCredentialsAsync(
        string username,
        string? pin,
        string? password,
        Permission requiredPermission,
        Guid terminalId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return SupervisorAuthorizationResult.Failed("INVALID_SUPERVISOR_USERNAME");

        if (string.IsNullOrWhiteSpace(pin) && string.IsNullOrWhiteSpace(password))
            return SupervisorAuthorizationResult.Failed("CREDENTIALS_REQUIRED");

        var user = await _userRepo.GetByUsernameAsync(username.Trim(), ct);
        if (user == null || !user.IsActive)
        {
            await RecordAuditAsync("SUPERVISOR_APPROVAL_REJECTED", "User", username, null, username, terminalId,
                $"Supervisor authorization rejected: user not found or inactive for permission {requiredPermission}", ct);
            return SupervisorAuthorizationResult.Failed("INVALID_SUPERVISOR_CREDENTIALS");
        }

        // Check account lockout
        var now = DateTime.UtcNow;
        if (user.IsLockedOut(now))
        {
            await RecordAuditAsync("AUTH_USER_LOCKOUT", "User", user.Id.ToString(), user.Id, user.Username, terminalId,
                $"Account locked out. Lockout expires at {user.LockoutEndUtc:u}", ct);
            return SupervisorAuthorizationResult.Failed("ACCOUNT_LOCKED_OUT");
        }

        // Check hierarchy and role permission
        if (user.Role == RoleType.Cashier || user.Role == RoleType.Viewer)
        {
            await RecordAuditAsync("SUPERVISOR_APPROVAL_REJECTED", "User", user.Id.ToString(), user.Id, user.Username, terminalId,
                $"Insufficient role '{user.Role}' for supervisor override {requiredPermission}", ct);
            return SupervisorAuthorizationResult.Failed("INSUFFICIENT_SUPERVISOR_PRIVILEGES");
        }

        if (!RolePermissions.HasPermission(user.Role, requiredPermission) &&
            !RolePermissions.HasPermission(user.Role, Permission.SupervisorOverride))
        {
            await RecordAuditAsync("SUPERVISOR_APPROVAL_REJECTED", "User", user.Id.ToString(), user.Id, user.Username, terminalId,
                $"Role '{user.Role}' does not hold required permission {requiredPermission}", ct);
            return SupervisorAuthorizationResult.Failed("PERMISSION_DENIED");
        }

        // Verify PIN or Password
        bool verified = false;
        if (!string.IsNullOrWhiteSpace(pin))
        {
            try
            {
                _passwordHasher.ValidatePinPolicy(pin);
                verified = _passwordHasher.VerifyPin(pin, user.PinHash);
            }
            catch
            {
                verified = false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(password))
        {
            verified = _passwordHasher.VerifyPassword(password, user.PasswordHash);
        }

        if (!verified)
        {
            user.RecordFailedPinAttempt(5, TimeSpan.FromMinutes(15), now);
            await _unitOfWork.SaveChangesAsync(ct);

            await RecordAuditAsync("AUTH_UNLOCK_FAILED", "User", user.Id.ToString(), user.Id, user.Username, terminalId,
                $"Invalid credential attempt for supervisor override {requiredPermission}. Failed attempts: {user.FailedPinAttempts}", ct);

            return SupervisorAuthorizationResult.Failed("INVALID_SUPERVISOR_CREDENTIALS");
        }

        // Reset lockout upon successful verification
        user.ResetPinLockout();
        await _unitOfWork.SaveChangesAsync(ct);

        return SupervisorAuthorizationResult.Success(user.Id, user.Username, user.Role);
    }

    private async Task RecordAuditAsync(
        string action,
        string entityName,
        string? entityId,
        Guid? userId,
        string? username,
        Guid? terminalId,
        string details,
        CancellationToken ct)
    {
        var log = new AuditLog(
            0,
            action,
            entityName,
            entityId,
            userId,
            terminalId,
            details,
            success: false,
            userName: username);
        await _auditRepo.RecordAsync(log, ct);
        await _unitOfWork.SaveChangesAsync(ct);
    }
}
