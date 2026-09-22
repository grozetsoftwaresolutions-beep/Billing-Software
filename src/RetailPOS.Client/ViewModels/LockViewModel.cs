using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Client.Services;

namespace RetailPOS.Client.ViewModels;

public sealed partial class LockViewModel : ObservableObject
{
    private readonly AuthenticationClient _authClient;

    [ObservableProperty]
    public partial string FullName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Role { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    public LockViewModel(AuthenticationClient? authClient = null)
    {
        _authClient = authClient ?? new AuthenticationClient();
        RefreshUserContext();
    }

    public void RefreshUserContext()
    {
        var session = ClientSessionManager.Instance;
        FullName = session.CurrentFullName ?? session.CurrentUsername ?? "Operator";
        Username = session.CurrentUsername ?? "unknown";
        Role = session.CurrentRole?.ToString() ?? "User";
        ErrorMessage = null;
        HasError = false;
    }

    public async Task<bool> UnlockAsync(string pin, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        HasError = false;

        if (string.IsNullOrWhiteSpace(pin))
        {
            ErrorMessage = "Please enter your PIN to unlock.";
            HasError = true;
            return false;
        }

        var token = ClientSessionManager.Instance.CurrentToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            // Token was cleared; require full login
            ClientSessionManager.Instance.ClearSession(ClientSessionState.NotAuthenticated);
            return false;
        }

        IsLoading = true;
        try
        {
            var result = await _authClient.UnlockSessionAsync(token, pin.Trim(), cancellationToken);
            if (result.Success && result.Data?.Success == true)
            {
                ClientSessionManager.Instance.TransitionToActive();
                return true;
            }

            ErrorMessage = result.ErrorMessage ?? "Invalid PIN or account locked.";
            HasError = true;
            // Notice: Session remains Locked; token is retained in memory.
            return false;
        }
        catch (Exception)
        {
            ErrorMessage = "Could not contact server to verify PIN.";
            HasError = true;
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SwitchUserOrLogoutAsync(CancellationToken cancellationToken = default)
    {
        var token = ClientSessionManager.Instance.CurrentToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await _authClient.LogoutAsync(token, cancellationToken);
            }
            catch
            {
                // Best-effort remote revocation
            }
        }

        ClientSessionManager.Instance.ClearSession(ClientSessionState.NotAuthenticated);
    }
}
