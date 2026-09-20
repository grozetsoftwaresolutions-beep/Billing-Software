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
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}
