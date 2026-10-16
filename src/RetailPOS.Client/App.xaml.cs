using System;
using Microsoft.UI.Xaml;

namespace RetailPOS.Client;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _mainWindow;

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Console.Error.WriteLine($"[FATAL] WinUI UnhandledException: {e.Message}");
        System.Diagnostics.Debug.WriteLine($"[FATAL] WinUI UnhandledException: {e.Exception}");
    }

    private void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        Console.Error.WriteLine($"[FATAL] AppDomain UnhandledException: {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Console.Error.WriteLine($"[ERROR] UnobservedTaskException: {e.Exception.Message}");
        e.SetObserved();
    }

    internal static void ProcessCommandLineArgs(string[] cmdArgs)
    {
#if DEBUG
        foreach (var arg in cmdArgs)
        {
            if (arg.Equals("--demo-locked", StringComparison.OrdinalIgnoreCase))
            {
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    "demo-lock-token",
                    Guid.NewGuid(),
                    "cashier1",
                    "Sarah Jenkins",
                    RetailPOS.Domain.Enums.RoleType.Cashier,
                    Guid.NewGuid(),
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
                RetailPOS.Client.Services.ClientSessionManager.Instance.TransitionToLocked();
            }
            else if (arg.Equals("--demo-authenticated", StringComparison.OrdinalIgnoreCase))
            {
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    "demo-auth-token",
                    Guid.NewGuid(),
                    "manager1",
                    "Robert Smith",
                    RetailPOS.Domain.Enums.RoleType.Manager,
                    null,
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
            }
            else if (arg.Equals("--demo-pin", StringComparison.OrdinalIgnoreCase))
            {
                RetailPOS.Client.ViewModels.LoginViewModel.ForceInitialPinMode = true;
            }
            else if (arg.Equals("--demo-pos", StringComparison.OrdinalIgnoreCase))
            {
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    "demo-cashier-token",
                    Guid.NewGuid(),
                    "cashier1",
                    "Sarah Jenkins",
                    RetailPOS.Domain.Enums.RoleType.Cashier,
                    Guid.NewGuid(),
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
                RetailPOS.Client.Views.ShellPage.ForceInitialNavigationTag = "POS";
            }
            else if (arg.Equals("--demo-pos-cart", StringComparison.OrdinalIgnoreCase))
            {
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    "demo-cashier-token",
                    Guid.NewGuid(),
                    "cashier1",
                    "Sarah Jenkins",
                    RetailPOS.Domain.Enums.RoleType.Cashier,
                    Guid.NewGuid(),
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
                RetailPOS.Client.Views.ShellPage.ForceInitialNavigationTag = "POS";
                RetailPOS.Client.Views.PosPage.InitialDemoMode = "cart";
            }
            else if (arg.Equals("--demo-pos-payment", StringComparison.OrdinalIgnoreCase))
            {
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    "demo-cashier-token",
                    Guid.NewGuid(),
                    "cashier1",
                    "Sarah Jenkins",
                    RetailPOS.Domain.Enums.RoleType.Cashier,
                    Guid.NewGuid(),
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
                RetailPOS.Client.Views.ShellPage.ForceInitialNavigationTag = "POS";
                RetailPOS.Client.Views.PosPage.InitialDemoMode = "payment";
            }
            else if (arg.Equals("--demo-pos-checkout", StringComparison.OrdinalIgnoreCase))
            {
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    "demo-cashier-token",
                    Guid.NewGuid(),
                    "cashier1",
                    "Sarah Jenkins",
                    RetailPOS.Domain.Enums.RoleType.Cashier,
                    Guid.NewGuid(),
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
                RetailPOS.Client.Views.ShellPage.ForceInitialNavigationTag = "POS";
                RetailPOS.Client.Views.PosPage.InitialDemoMode = "checkout";
            }
            else if (arg.Equals("--demo-products", StringComparison.OrdinalIgnoreCase))
            {
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    "demo-manager-token",
                    Guid.NewGuid(),
                    "manager1",
                    "Robert Smith",
                    RetailPOS.Domain.Enums.RoleType.Manager,
                    null,
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
                RetailPOS.Client.Views.ShellPage.ForceInitialNavigationTag = "Products";
            }
            else if (arg.StartsWith("--auth-token=", StringComparison.OrdinalIgnoreCase))
            {
                var token = arg["--auth-token=".Length..];
                var response = new RetailPOS.Contracts.Auth.LoginResponse(
                    token,
                    Guid.NewGuid(),
                    "manager1",
                    "Robert Smith",
                    RetailPOS.Domain.Enums.RoleType.Manager,
                    null,
                    DateTime.UtcNow.AddHours(8));
                RetailPOS.Client.Services.ClientSessionManager.Instance.SetActiveSession(response);
                RetailPOS.Client.Views.ShellPage.ForceInitialNavigationTag = "Products";
            }
            else if (arg.StartsWith("--login-username=", StringComparison.OrdinalIgnoreCase))
            {
                AutomationInitialUsername = arg["--login-username=".Length..];
            }
            else if (arg.StartsWith("--login-password=", StringComparison.OrdinalIgnoreCase))
            {
                AutomationInitialPassword = arg["--login-password=".Length..];
            }
            else if (arg.StartsWith("--login-pin=", StringComparison.OrdinalIgnoreCase))
            {
                AutomationInitialPin = arg["--login-pin=".Length..];
            }
            else if (arg.StartsWith("--enrolled-terminal-id=", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(arg["--enrolled-terminal-id=".Length..], out var tId))
                {
                    AutomationTerminalId = tId;
                }
            }
        }
#endif
    }

#if DEBUG
    public static string? AutomationInitialUsername { get; set; }
    public static string? AutomationInitialPassword { get; set; }
    public static string? AutomationInitialPin { get; set; }
    public static Guid? AutomationTerminalId { get; set; }
#endif

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ProcessCommandLineArgs(Environment.GetCommandLineArgs());

        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}
