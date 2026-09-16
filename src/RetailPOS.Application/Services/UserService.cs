using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Services;

public sealed class UserService : IUserService
{
    private readonly IUserRepository _userRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISessionManager? _sessionManager;

    public UserService(
        IUserRepository userRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ISessionManager? sessionManager = null)
    {
        _userRepo = userRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _sessionManager = sessionManager;
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await _userRepo.GetAllAsync(cancellationToken);
        var now = DateTime.UtcNow;

        return users.Select(u => new UserDto(
            u.Id,
            u.Username,
            u.FullName,
            u.Role,
            u.IsActive,
            u.IsLockedOut(now))).ToList();
    }

    public async Task<ApiResponse<UserDto>> CreateUserAsync(
        CreateUserRequest request,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        // Enforce role hierarchy:
        // Manager can only create Cashier
        // Admin can create Cashier and Manager
        // Owner can create Cashier, Manager, Admin, Owner
        if (executingUserRole == RoleType.Manager && request.Role != RoleType.Cashier)
        {
            throw new DomainException("Store Managers can only create Cashier accounts.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        if (executingUserRole == RoleType.Admin && request.Role == RoleType.Owner)
        {
            throw new DomainException("System Administrators cannot create Owner accounts.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        if (executingUserRole == RoleType.Cashier || executingUserRole == RoleType.Viewer)
        {
            throw new DomainException("Cashiers cannot manage user accounts.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        var normalizedUsername = request.Username.Trim().ToLowerInvariant();
        var existing = await _userRepo.GetByUsernameAsync(normalizedUsername, cancellationToken);
        if (existing != null)
        {
            throw new DomainException("Username is already in use.", "USERNAME_TAKEN");
        }

        _passwordHasher.ValidatePasswordPolicy(request.Password);
        _passwordHasher.ValidatePinPolicy(request.Pin);

        var passwordHash = _passwordHasher.HashPassword(request.Password);
        var pinHash = _passwordHasher.HashPin(request.Pin);

        var newUser = new User(
            Guid.NewGuid(),
            normalizedUsername,
            request.FullName.Trim(),
            pinHash,
            passwordHash,
            salt: string.Empty,
            request.Role);

        await _userRepo.AddAsync(newUser, cancellationToken);
        await _auditRepo.RecordAsync("Users", "USER_CREATED", executingUsername, $"Created user {normalizedUsername} with role {request.Role}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var dto = new UserDto(newUser.Id, newUser.Username, newUser.FullName, newUser.Role, newUser.IsActive, false);
        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<object>> DeactivateUserAsync(
        Guid targetUserId,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        if (targetUserId == executingUserId)
        {
            throw new DomainException("You cannot deactivate your own account.", "CANNOT_DEACTIVATE_SELF");
        }

        var targetUser = await _userRepo.GetByIdAsync(targetUserId, cancellationToken);
        if (targetUser == null)
        {
            throw new DomainException("Target user was not found.", "USER_NOT_FOUND");
        }

        // Enforce hierarchy for deactivation
        if (executingUserRole == RoleType.Manager && targetUser.Role != RoleType.Cashier)
        {
            throw new DomainException("Managers can only deactivate Cashier accounts.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        if (executingUserRole == RoleType.Admin && targetUser.Role == RoleType.Owner)
        {
            throw new DomainException("Administrators cannot deactivate Owner accounts.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        // Prevent deactivating the last active Owner
        if (targetUser.Role == RoleType.Owner)
        {
            var allUsers = await _userRepo.GetAllAsync(cancellationToken);
            var activeOwnerCount = allUsers.Count(u => u.Role == RoleType.Owner && u.IsActive);
            if (activeOwnerCount <= 1)
            {
                throw new DomainException("Cannot deactivate the last remaining Store Owner.", "CANNOT_DEACTIVATE_LAST_OWNER");
            }
        }

        targetUser.Deactivate();
        await _auditRepo.RecordAsync("Users", "USER_DEACTIVATED", executingUsername, $"Deactivated user {targetUser.Username}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok<object>(new { Message = $"User {targetUser.Username} has been deactivated." });
    }

    public async Task<ApiResponse<object>> ActivateUserAsync(
        Guid targetUserId,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        var targetUser = await _userRepo.GetByIdAsync(targetUserId, cancellationToken);
        if (targetUser == null)
        {
            throw new DomainException("Target user was not found.", "USER_NOT_FOUND");
        }

        if (executingUserRole == RoleType.Manager && targetUser.Role != RoleType.Cashier)
        {
            throw new DomainException("Managers can only activate Cashier accounts.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        if (executingUserRole == RoleType.Admin && targetUser.Role == RoleType.Owner)
        {
            throw new DomainException("Administrators cannot activate Owner accounts.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        targetUser.Activate();
        await _auditRepo.RecordAsync("Users", "USER_ACTIVATED", executingUsername, $"Activated user {targetUser.Username}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok<object>(new { Message = $"User {targetUser.Username} has been activated." });
    }

    public async Task<ApiResponse<object>> ChangeUserRoleAsync(
        Guid targetUserId,
        RoleType newRole,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        if (executingUserRole != RoleType.Owner)
        {
            throw new DomainException("Only the Store Owner can change user roles.", "PERMISSION_DENIED");
        }

        var targetUser = await _userRepo.GetByIdAsync(targetUserId, cancellationToken);
        if (targetUser == null)
        {
            throw new DomainException("Target user was not found.", "USER_NOT_FOUND");
        }

        if (targetUser.Role == RoleType.Owner && newRole != RoleType.Owner)
        {
            var allUsers = await _userRepo.GetAllAsync(cancellationToken);
            var activeOwnerCount = allUsers.Count(u => u.Role == RoleType.Owner && u.IsActive);
            if (activeOwnerCount <= 1)
            {
                throw new DomainException("Cannot demote the last remaining Store Owner.", "CANNOT_DEMOTE_LAST_OWNER");
            }
        }

        var oldRole = targetUser.Role;
        targetUser.UpdateRole(newRole);

        // Instant session revocation
        _sessionManager?.RevokeUserSessions(targetUserId);

        await _auditRepo.RecordAsync("Users", "USER_ROLE_CHANGED", executingUsername,
            $"Changed role of user {targetUser.Username} from {oldRole} to {newRole}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok<object>(new { Message = $"Role of user {targetUser.Username} updated to {newRole}." });
    }

    public async Task<ApiResponse<object>> ResetUserCredentialsAsync(
        Guid targetUserId,
        string? newPassword,
        string? newPin,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        if (executingUserRole != RoleType.Owner && executingUserRole != RoleType.Admin)
        {
            throw new DomainException("Insufficient permissions to reset user credentials.", "PERMISSION_DENIED");
        }

        var targetUser = await _userRepo.GetByIdAsync(targetUserId, cancellationToken);
        if (targetUser == null)
        {
            throw new DomainException("Target user was not found.", "USER_NOT_FOUND");
        }

        if (executingUserRole == RoleType.Admin && targetUser.Role == RoleType.Owner)
        {
            throw new DomainException("Administrators cannot reset Owner credentials.", "INSUFFICIENT_ROLE_PERMISSIONS");
        }

        string pwHash = targetUser.PasswordHash;
        string salt = targetUser.Salt;
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            _passwordHasher.ValidatePasswordPolicy(newPassword);
            pwHash = _passwordHasher.HashPassword(newPassword);
        }

        string pinHash = targetUser.PinHash;
        if (!string.IsNullOrWhiteSpace(newPin))
        {
            _passwordHasher.ValidatePinPolicy(newPin);
            pinHash = _passwordHasher.HashPin(newPin);
        }

        targetUser.UpdateCredentials(pwHash, pinHash, salt);

        // Instant session revocation
        _sessionManager?.RevokeUserSessions(targetUserId);

        await _auditRepo.RecordAsync("Users", "USER_CREDENTIALS_RESET", executingUsername,
            $"Reset credentials for user {targetUser.Username}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok<object>(new { Message = $"Credentials reset for user {targetUser.Username}." });
    }

    public async Task<ApiResponse<object>> UnlockUserAsync(
        Guid targetUserId,
        Guid executingUserId,
        RoleType executingUserRole,
        string executingUsername,
        CancellationToken cancellationToken = default)
    {
        if (executingUserRole != RoleType.Owner && executingUserRole != RoleType.Admin)
        {
            throw new DomainException("Insufficient permissions to unlock user account.", "PERMISSION_DENIED");
        }

        var targetUser = await _userRepo.GetByIdAsync(targetUserId, cancellationToken);
        if (targetUser == null)
        {
            throw new DomainException("Target user was not found.", "USER_NOT_FOUND");
        }

        targetUser.ResetPinLockout();

        await _auditRepo.RecordAsync("Users", "AUTH_USER_UNLOCKED", executingUsername,
            $"Unlocked account for user {targetUser.Username}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ApiResponse.Ok<object>(new { Message = $"Account for user {targetUser.Username} has been unlocked." });
    }
}
