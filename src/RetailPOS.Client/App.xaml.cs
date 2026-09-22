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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();
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
        }

        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}
