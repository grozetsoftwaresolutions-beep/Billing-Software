using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;


namespace RetailPOS.Application.Services;

public sealed class AuthenticationService : IAuthenticationService
{
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(8);
    private const int MaxFailedPinAttempts = 5;
    private static readonly TimeSpan PinLockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IUserRepository _userRepo;
    private readonly ITerminalRepository _terminalRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IStoreRepository _storeRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISessionManager _sessionManager;
    private readonly IServerIdentityService? _serverIdentityService;
    private readonly IStoreIdentityService? _storeIdentityService;

    public AuthenticationService(
        IUserRepository userRepo,
        ITerminalRepository terminalRepo,
        IAuditLogRepository auditRepo,
        IStoreRepository storeRepo,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ISessionManager sessionManager,
        IServerIdentityService? serverIdentityService = null,
        IStoreIdentityService? storeIdentityService = null)
    {
        _userRepo = userRepo;
        _terminalRepo = terminalRepo;
        _auditRepo = auditRepo;
        _storeRepo = storeRepo;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _sessionManager = sessionManager;
        _serverIdentityService = serverIdentityService;
        _storeIdentityService = storeIdentityService;
    }

    public async Task<SetupStatusResponse> GetSetupStatusAsync(CancellationToken cancellationToken = default)
    {
        var hasOwner = await _userRepo.HasAnyOwnerAsync(cancellationToken);
        var store = await _storeRepo.GetStoreAsync(cancellationToken);
        return new SetupStatusResponse(!hasOwner, store?.StoreName ?? "Grozet Supermarket");
    }

    public async Task<ApiResponse<object>> SetupOwnerAsync(SetupOwnerRequest request, CancellationToken cancellationToken = default)
    {
        // Atomic serializable transaction: database is the ultimate authority preventing race conditions
        byte[]? recoveryKeyBytes = null;
        byte[]? storeSigningKey = null;
        char[]? recoveryKeyChars = null;
        string? recoveryKeyHex = null;
        Guid storeGuid = Guid.NewGuid();

        try
        {
            await using var tx = await _unitOfWork.BeginSerializableTransactionAsync(cancellationToken);

            var hasOwner = await _userRepo.HasAnyOwnerAsync(cancellationToken);
            if (hasOwner)
            {
                await tx.RollbackAsync(cancellationToken);
                throw new DomainException("Store owner setup has already been completed.", "SETUP_ALREADY_COMPLETED");
            }

            _passwordHasher.ValidatePasswordPolicy(request.Password);
            _passwordHasher.ValidatePinPolicy(request.Pin);

            var normalizedUsername = request.Username.Trim().ToLowerInvariant();
            var existingUser = await _userRepo.GetByUsernameAsync(normalizedUsername, cancellationToken);
            if (existingUser != null)
            {
                await tx.RollbackAsync(cancellationToken);
                throw new DomainException("Username is already in use.", "USERNAME_TAKEN");
            }

            var passwordHash = _passwordHasher.HashPassword(request.Password);
            var pinHash = _passwordHasher.HashPin(request.Pin);

            var owner = new User(
                Guid.NewGuid(),
                normalizedUsername,
                request.FullName.Trim(),
                pinHash,
                passwordHash,
                salt: string.Empty,
                RoleType.Owner);

            await _userRepo.AddAsync(owner, cancellationToken);

            if (_storeIdentityService != null)
            {
                recoveryKeyBytes = RandomNumberGenerator.GetBytes(32);
                recoveryKeyHex = Convert.ToHexString(recoveryKeyBytes);
                recoveryKeyChars = recoveryKeyHex.ToCharArray();
                storeSigningKey = RandomNumberGenerator.GetBytes(32);

                var (validatorHash, wrappedKey) = _storeIdentityService.WrapStoreSigningKey(
                    storeGuid,
                    recoveryKeyChars,
                    storeSigningKey);

                await _storeRepo.InitializeStoreSecurityAsync(
                    storeGuid,
                    request.StoreName.Trim(),
                    validatorHash,
                    wrappedKey,
                    cancellationToken);
            }
            else
            {
                await _storeRepo.UpdateStoreNameAsync(request.StoreName.Trim(), cancellationToken);
            }

            await _auditRepo.RecordAsync("Auth", "OWNER_SETUP", normalizedUsername, "First-run store owner setup completed.", cancellationToken);

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            if (_storeIdentityService != null && recoveryKeyChars != null && storeSigningKey != null)
            {
                await _storeIdentityService.InitializeNewStoreIdentityAsync(
                    storeGuid,
                    request.StoreName.Trim(),
                    storeSigningKey,
                    recoveryKeyChars,
                    cancellationToken);
            }

            if (_serverIdentityService != null)
            {
                try
                {
                    await _serverIdentityService.ProvisionInitialIdentityAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new DomainException($"PROVISION_FAILED: {ex.GetType().Name} - {ex.Message} - {ex.StackTrace}", "PROVISION_FAILED");
                }
            }

            if (!string.IsNullOrEmpty(recoveryKeyHex))
            {
                return ApiResponse.Ok<object>(new SetupOwnerResponse(
                    recoveryKeyHex,
                    storeGuid,
                    request.StoreName.Trim(),
                    "Store owner account configured successfully. Store master recovery key generated."));
            }

            return ApiResponse.Ok<object>(new { Message = "Store owner account configured successfully." });
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception)
        {
            var hasOwner = await _userRepo.HasAnyOwnerAsync(cancellationToken);
            if (hasOwner)
            {
                throw new DomainException("Store owner setup has already been completed.", "SETUP_ALREADY_COMPLETED");
            }

            throw;
        }
        finally
        {
            if (recoveryKeyBytes != null) CryptographicOperations.ZeroMemory(recoveryKeyBytes);
            if (storeSigningKey != null) CryptographicOperations.ZeroMemory(storeSigningKey);
            if (recoveryKeyChars != null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(recoveryKeyChars.AsSpan()));
        }
    }


    public async Task<ApiResponse<LoginResponse>> LoginWithPasswordAsync(LoginPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = request.Username.Trim().ToLowerInvariant();
        var user = await _userRepo.GetByUsernameAsync(normalizedUsername, cancellationToken);

        // Anti-enumeration: Cashiers are strictly restricted to PIN login; Managers/Admins/Owners use password
        if (user == null || !user.IsActive || user.Role == RoleType.Cashier || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            await _auditRepo.RecordAsync("Auth", "LOGIN_FAILURE", normalizedUsername, "Password login failed.", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ApiResponse.Fail<LoginResponse>("Invalid credentials.", "AUTH_FAILED");
        }

        if (request.TerminalId.HasValue)
        {
            var terminal = await _terminalRepo.GetByIdAsync(request.TerminalId.Value, cancellationToken);
            if (terminal == null || !terminal.IsActive)
            {
                await _auditRepo.RecordAsync("Auth", "LOGIN_FAILURE", normalizedUsername, "Unknown or inactive terminal specified.", cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return ApiResponse.Fail<LoginResponse>("Invalid credentials.", "AUTH_FAILED");
            }
        }

        user.ResetPinLockout();

        var token = _sessionManager.CreateSession(user.Id, user.Username, user.FullName, user.Role, request.TerminalId, SessionDuration);
        await _auditRepo.RecordAsync("Auth", "LOGIN_SUCCESS", user.Username, $"Password login successful for role {user.Role}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var expiresAtUtc = DateTime.UtcNow.Add(SessionDuration);
        return ApiResponse.Ok(new LoginResponse(token, user.Id, user.Username, user.FullName, user.Role, request.TerminalId, expiresAtUtc));
    }

    public async Task<ApiResponse<LoginResponse>> LoginWithPinAsync(LoginPinRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = request.Username.Trim().ToLowerInvariant();

        // Validate terminal counter: mandatory for cashier PIN login
        var terminal = await _terminalRepo.GetByIdAsync(request.TerminalId, cancellationToken);
        if (terminal == null || !terminal.IsActive)
        {
            await _auditRepo.RecordAsync("Auth", "LOGIN_FAILURE", normalizedUsername, "Invalid or inactive terminal for PIN login.", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ApiResponse.Fail<LoginResponse>("Invalid credentials.", "AUTH_FAILED");
        }

        var user = await _userRepo.GetByUsernameAsync(normalizedUsername, cancellationToken);

        // Anti-enumeration: only Cashiers can use fast PIN login
        if (user == null || !user.IsActive || user.Role != RoleType.Cashier)
        {
            await _auditRepo.RecordAsync("Auth", "LOGIN_FAILURE", normalizedUsername, "PIN login rejected: unknown user or non-cashier role.", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ApiResponse.Fail<LoginResponse>("Invalid credentials.", "AUTH_FAILED");
        }

        // Persistent brute-force lockout check
        if (user.IsLockedOut(DateTime.UtcNow))
        {
            await _auditRepo.RecordAsync("Auth", "LOGIN_FAILURE", normalizedUsername, "Account currently locked out due to excessive failed PIN attempts.", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ApiResponse.Fail<LoginResponse>("Invalid credentials.", "AUTH_FAILED");
        }

        var isPinValid = _passwordHasher.VerifyPin(request.Pin, user.PinHash);
        if (!isPinValid)
        {
            user.RecordFailedPinAttempt(MaxFailedPinAttempts, PinLockoutDuration, DateTime.UtcNow);
            await _auditRepo.RecordAsync("Auth", "LOGIN_FAILURE", normalizedUsername, "PIN verification failed.", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ApiResponse.Fail<LoginResponse>("Invalid credentials.", "AUTH_FAILED");
        }

        // Successful authentication resets failed attempts
        user.ResetPinLockout();

        var token = _sessionManager.CreateSession(user.Id, user.Username, user.FullName, user.Role, request.TerminalId, SessionDuration);
        await _auditRepo.RecordAsync("Auth", "LOGIN_SUCCESS", user.Username, $"PIN login successful on terminal {terminal.TerminalCode}.", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var expiresAtUtc = DateTime.UtcNow.Add(SessionDuration);
        return ApiResponse.Ok(new LoginResponse(token, user.Id, user.Username, user.FullName, user.Role, request.TerminalId, expiresAtUtc));
    }

    public async Task<ApiResponse<object>> LogoutAsync(string token, CancellationToken cancellationToken = default)
    {
        var session = await _sessionManager.ValidateSessionAsync(token, cancellationToken);
        if (session.IsValid && session.Username != null)
        {
            _sessionManager.RevokeSession(token);
            await _auditRepo.RecordAsync("Auth", "LOGOUT", session.Username, "User logged out.", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return ApiResponse.Ok<object>(new { Message = "Logged out successfully." });
    }
}
