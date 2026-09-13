using System.Collections.Concurrent;
using System.Security.Cryptography;
using RetailPOS.Application.Security;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Server.Authentication;

public sealed record ActiveSession(
    string Token,
    Guid UserId,
    string Username,
    string FullName,
    RoleType Role,
    Guid? TerminalId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// Thread-safe local session store for offline POS terminals.
/// Completely independent from cloud providers or JWT token overhead.
/// 
/// ARCHITECTURAL NOTES:
/// 1. Process-Local: Sessions are stored in-memory using a thread-safe ConcurrentDictionary.
/// 2. Lifecycle: Server shutdown or restart invalidates active sessions; cashiers re-authenticate upon server reboot.
/// 3. Terminal-Bound: Cashier POS sessions are bound to a specific TerminalId to prevent cross-counter hijacking.
/// 4. Security Hygiene: Credentials, passwords, PINs, and tokens must NEVER be written to operational logs.
/// </summary>
public sealed class LocalSessionManager : ILocalSessionValidator, ISessionManager
{
    private readonly ConcurrentDictionary<string, ActiveSession> _activeSessions = new();

    public string CreateSession(Guid userId, string username, string fullName, RoleType role, Guid? terminalId, TimeSpan duration)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        var now = DateTime.UtcNow;
        var session = new ActiveSession(
            token,
            userId,
            username,
            fullName,
            role,
            terminalId,
            now,
            now.Add(duration));

        _activeSessions[token] = session;
        return token;
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

        if (_activeSessions.TryGetValue(sessionToken.Trim(), out var session))
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
                    session.ExpiresAtUtc));
            }

            // Expired session
            _activeSessions.TryRemove(sessionToken, out _);
            return Task.FromResult(new SessionValidationResult(false, ErrorMessage: "Session has expired."));
        }

        return Task.FromResult(new SessionValidationResult(false, ErrorMessage: "Invalid session token."));
    }

    public void RevokeSession(string sessionToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            _activeSessions.TryRemove(sessionToken.Trim(), out _);
        }
    }
}
