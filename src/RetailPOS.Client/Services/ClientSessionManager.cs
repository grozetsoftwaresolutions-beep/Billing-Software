using System;
using RetailPOS.Contracts.Auth;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.Services;

public enum ClientSessionState
{
    NotAuthenticated = 0,
    Authenticating = 1,
    Active = 2,
    Locked = 3,
    Expired = 4,
    LoggingOut = 5,
    AuthenticationError = 6
}

public sealed class ClientSessionManager
{
    private static readonly Lazy<ClientSessionManager> _instance = new(() => new ClientSessionManager());
    public static ClientSessionManager Instance => _instance.Value;

    private readonly object _lock = new();

    public ClientSessionState State { get; private set; } = ClientSessionState.NotAuthenticated;

    // IN-MEMORY ONLY - Never persisted to disk, registry, SQLite, DPAPI, or app settings
    public string? CurrentToken { get; private set; }
    public Guid? CurrentUserId { get; private set; }
    public string? CurrentUsername { get; private set; }
    public string? CurrentFullName { get; private set; }
    public RoleType? CurrentRole { get; private set; }
    public Guid? CurrentTerminalId { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }

    public bool IsAuthenticated => State == ClientSessionState.Active || State == ClientSessionState.Locked;
    public bool IsActive => State == ClientSessionState.Active;
    public bool IsLocked => State == ClientSessionState.Locked;

    public event Action<ClientSessionState>? StateChanged;

    public void SetActiveSession(LoginResponse login)
    {
        ArgumentNullException.ThrowIfNull(login);

        lock (_lock)
        {
            CurrentToken = login.Token;
            CurrentUserId = login.UserId;
            CurrentUsername = login.Username;
            CurrentFullName = login.FullName;
            CurrentRole = login.Role;
            CurrentTerminalId = login.TerminalId;
            ExpiresAtUtc = login.ExpiresAtUtc;
            State = ClientSessionState.Active;
        }

        StateChanged?.Invoke(ClientSessionState.Active);
    }

    /// <summary>
    /// Transitions the session to Locked state.
    /// CRITICAL: Retains the bearer token in memory so POST /api/v1/auth/unlock can authenticate.
    /// </summary>
    public void TransitionToLocked()
    {
        lock (_lock)
        {
            if (!IsAuthenticated)
                return;

            State = ClientSessionState.Locked;
            // Notice: CurrentToken remains IN-MEMORY and is NOT cleared.
        }

        StateChanged?.Invoke(ClientSessionState.Locked);
    }

    /// <summary>
    /// Transitions a locked session back to Active upon successful unlock.
    /// </summary>
    public void TransitionToActive()
    {
        lock (_lock)
        {
            if (State != ClientSessionState.Locked)
                return;

            State = ClientSessionState.Active;
        }

        StateChanged?.Invoke(ClientSessionState.Active);
    }

    /// <summary>
    /// Sets a transient state (Authenticating, LoggingOut, AuthenticationError).
    /// </summary>
    public void SetState(ClientSessionState newState)
    {
        lock (_lock)
        {
            State = newState;
        }

        StateChanged?.Invoke(newState);
    }

    /// <summary>
    /// Clears all in-memory session tokens and user credentials upon logout or expiration.
    /// </summary>
    public void ClearSession(ClientSessionState finalState = ClientSessionState.NotAuthenticated)
    {
        lock (_lock)
        {
            CurrentToken = null;
            CurrentUserId = null;
            CurrentUsername = null;
            CurrentFullName = null;
            CurrentRole = null;
            CurrentTerminalId = null;
            ExpiresAtUtc = null;
            State = finalState;
        }

        StateChanged?.Invoke(finalState);
    }
}
