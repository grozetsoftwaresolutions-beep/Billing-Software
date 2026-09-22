using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Common;

namespace RetailPOS.Client.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    public static bool ForceInitialPinMode { get; set; }

    private readonly AuthenticationClient _authClient;
    private readonly TerminalEnrollmentClient _terminalClient;

    [ObservableProperty]
    public partial bool IsPinMode { get; set; } = true;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool IsServerOnline { get; set; }

    [ObservableProperty]
    public partial bool IsTerminalEnrolled { get; set; }

    [ObservableProperty]
    public partial string TerminalNotice { get; set; } = string.Empty;

    public Guid? EnrolledTerminalId { get; set; }

    public LoginViewModel(AuthenticationClient? authClient = null, TerminalEnrollmentClient? terminalClient = null)
    {
        _authClient = authClient ?? new AuthenticationClient();
        _terminalClient = terminalClient ?? new TerminalEnrollmentClient(new System.Net.Http.HttpClient());

        RefreshEnvironmentStatus();
    }

    public void RefreshEnvironmentStatus()
    {
        try
        {
            var pairingToken = _terminalClient.LoadPairingToken();
            IsTerminalEnrolled = !string.IsNullOrWhiteSpace(pairingToken);

            if (!IsTerminalEnrolled)
            {
                TerminalNotice = "Terminal is not enrolled. Cashier PIN login requires an enrolled counter terminal. Management password login is available.";
                IsPinMode = false; // Default to password login if terminal not enrolled
            }
            else
            {
                TerminalNotice = "Terminal enrolled. Cashier fast counter PIN login is active.";
                IsPinMode = true;
            }

            if (ForceInitialPinMode)
            {
                IsPinMode = true;
            }
        }
        catch
        {
            IsTerminalEnrolled = false;
            IsPinMode = ForceInitialPinMode;
            TerminalNotice = "Terminal enrollment status unavailable.";
        }
    }

    /// <summary>
    /// Executes login with password or PIN.
    /// Notice: Credential string is passed directly into this method from PasswordBox
    /// and is NOT stored in any ViewModel field, property, or log.
    /// </summary>
    public async Task<bool> LoginAsync(string credential, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        HasError = false;

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "Please enter your username.";
            HasError = true;
            return false;
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            ErrorMessage = IsPinMode ? "Please enter your PIN." : "Please enter your password.";
            HasError = true;
            return false;
        }

        if (IsPinMode && !IsTerminalEnrolled)
        {
            ErrorMessage = "Counter PIN login requires an enrolled terminal. Please switch to Password login or enroll this terminal.";
            HasError = true;
            return false;
        }

        IsLoading = true;
        ClientSessionManager.Instance.SetState(ClientSessionState.Authenticating);

        try
        {
            if (IsPinMode)
            {
                // TerminalId is required for cashier PIN login. If enrolled, use EnrolledTerminalId or Guid.NewGuid() fallback
                var terminalId = EnrolledTerminalId ?? Guid.Empty;
                var result = await _authClient.LoginWithPinAsync(Username.Trim(), credential, terminalId, cancellationToken);

                if (result.Success && result.Data != null)
                {
                    ClientSessionManager.Instance.SetActiveSession(result.Data);
                    return true;
                }

                ErrorMessage = result.ErrorMessage ?? "Invalid credentials. Please verify your username and PIN.";
                HasError = true;
                ClientSessionManager.Instance.SetState(ClientSessionState.AuthenticationError);
                return false;
            }
            else
            {
                var result = await _authClient.LoginWithPasswordAsync(Username.Trim(), credential, EnrolledTerminalId, cancellationToken);

                if (result.Success && result.Data != null)
                {
                    ClientSessionManager.Instance.SetActiveSession(result.Data);
                    return true;
                }

                ErrorMessage = result.ErrorMessage ?? "Invalid credentials. Please verify your username and password.";
                HasError = true;
                ClientSessionManager.Instance.SetState(ClientSessionState.AuthenticationError);
                return false;
            }
        }
        catch (Exception)
        {
            ErrorMessage = "Server unreachable. Please verify that the RetailPOS server is running.";
            HasError = true;
            ClientSessionManager.Instance.SetState(ClientSessionState.AuthenticationError);
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
