using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Server.Authentication;

public sealed class ActiveSession
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public RoleType Role { get; set; }
    public Guid? TerminalId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }
    public bool IsLocked { get; set; }
    public string SessionIdentityHash { get; set; } = string.Empty;
}

public sealed class LocalSessionManager : ILocalSessionValidator, ISessionManager
{
    private readonly ConcurrentDictionary<string, ActiveSession> _activeSessions = new();
    private readonly IPasswordHasher? _passwordHasher;
    private readonly Func<RetailDbContext>? _dbContextFactory;

    public LocalSessionManager(
        IPasswordHasher? passwordHasher = null,
        Func<RetailDbContext>? dbContextFactory = null)
    {
        _passwordHasher = passwordHasher;
        _dbContextFactory = dbContextFactory;
    }

    public string CreateSession(Guid userId, string username, string fullName, RoleType role, Guid? terminalId, TimeSpan duration)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToHexString(tokenBytes).ToLowerInvariant();
        var tokenHash = HashToken(rawToken);

        var now = DateTime.UtcNow;
        var session = new ActiveSession
        {
            TokenHash = tokenHash,
            UserId = userId,
            Username = username,
            FullName = fullName,
            Role = role,
            TerminalId = terminalId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(duration),
            LastActivityUtc = now,
            IsLocked = false,
            SessionIdentityHash = HashSessionIdentity(userId, rawToken)
        };

        _activeSessions[tokenHash] = session;
        return rawToken;
    }

    public string CreateSession(Guid userId, string username, RoleType role, TimeSpan duration)
    {
        return CreateSession(userId, username, username, role, null, duration);
    }

    public Task<SessionValidationResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return Task.FromResult(new SessionValidationResult(false, ErrorMessage: "Missing session token."));
        }

        var tokenHash = HashToken(sessionToken.Trim());
        if (_activeSessions.TryGetValue(tokenHash, out var session))
        {
            if (session.ExpiresAtUtc > DateTime.UtcNow)
            {
                return Task.FromResult(new SessionValidationResult(
                    true,
                    session.UserId,
                    session.Username,
                    session.FullName,
                    session.Role,
                    session.TerminalId,
                    session.ExpiresAtUtc,
                    IsLocked: session.IsLocked,
                    LastActivityUtc: session.LastActivityUtc,
                    SessionIdentityHash: session.SessionIdentityHash));
            }

            _activeSessions.TryRemove(tokenHash, out _);
            return Task.FromResult(new SessionValidationResult(false, ErrorMessage: "Session has expired."));
        }

        return Task.FromResult(new SessionValidationResult(false, ErrorMessage: "Invalid session token."));
    }

    public void RevokeSession(string sessionToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            _activeSessions.TryRemove(HashToken(sessionToken.Trim()), out _);
        }
    }

    public void TouchSession(string sessionToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            var tokenHash = HashToken(sessionToken.Trim());
            if (_activeSessions.TryGetValue(tokenHash, out var session))
            {
                session.LastActivityUtc = DateTime.UtcNow;
            }
        }
    }

    public void LockSession(string sessionToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            var tokenHash = HashToken(sessionToken.Trim());
            if (_activeSessions.TryGetValue(tokenHash, out var session))
            {
                session.IsLocked = true;
            }
        }
    }

    public async Task<bool> UnlockSessionAsync(string sessionToken, string pin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(pin))
            return false;

        var tokenHash = HashToken(sessionToken.Trim());
        if (!_activeSessions.TryGetValue(tokenHash, out var session))
            return false;

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _activeSessions.TryRemove(tokenHash, out _);
            return false;
        }

        // Verify PIN against DB user
        if (_passwordHasher != null && _dbContextFactory != null)
        {
            await using var db = _dbContextFactory();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);
            if (user == null || !user.IsActive || user.IsLockedOut(DateTime.UtcNow))
                return false;

            if (string.IsNullOrWhiteSpace(user.PinHash))
                return false;

            if (!_passwordHasher.VerifyPin(pin, user.PinHash))
            {
                user.RecordFailedPinAttempt(5, TimeSpan.FromMinutes(15), DateTime.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            user.ResetPinLockout();
            await db.SaveChangesAsync(cancellationToken);
        }

        session.IsLocked = false;
        session.LastActivityUtc = DateTime.UtcNow;
        return true;
    }

    public void RevokeUserSessions(Guid userId)
    {
        foreach (var kvp in _activeSessions)
        {
            if (kvp.Value.UserId == userId)
            {
                _activeSessions.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void RevokeTerminalSessions(Guid terminalId)
    {
        foreach (var kvp in _activeSessions)
        {
            if (kvp.Value.TerminalId == terminalId)
            {
                _activeSessions.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HashSessionIdentity(Guid userId, string token)
    {
        var input = $"{userId:N}:{token}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
