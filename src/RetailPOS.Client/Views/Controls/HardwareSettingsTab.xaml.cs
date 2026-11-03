using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class HardwareSettingsTab : UserControl
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public string TargetTerminalDisplay => ViewModel?.HardwareTargetTerminalId != Guid.Empty
        ? $"Terminal ID: {ViewModel?.HardwareTargetTerminalId}"
        : "No terminal selected";

    public bool IsSaveEnabled => ViewModel != null && !ViewModel.IsSaving && !ViewModel.IsLoading;
    public Visibility SavingVisibility => (ViewModel?.IsSaving == true) ? Visibility.Visible : Visibility.Collapsed;

    public bool IsHealthCheckEnabled => ViewModel != null && !ViewModel.IsCheckingHardwareStatus;
    public bool IsPrinterTestEnabled => ViewModel != null && !ViewModel.IsTestingPrinter;
    public bool IsDrawerTestEnabled => ViewModel != null && !ViewModel.IsTestingDrawer;
    public bool IsScalePollEnabled => ViewModel != null && !ViewModel.IsPollingScale;

    public int PrinterConnectionIndex
    {
        get => ViewModel?.PrinterConnectionType switch
        {
            PrinterConnectionType.Simulated => 0,
            PrinterConnectionType.WindowsSpooler => 1,
            PrinterConnectionType.NetworkTcp => 2,
            PrinterConnectionType.RawUsb => 3,
            _ => 0
        };
        set
        {
            if (ViewModel == null) return;
            ViewModel.PrinterConnectionType = value switch
            {
                0 => PrinterConnectionType.Simulated,
                1 => PrinterConnectionType.WindowsSpooler,
                2 => PrinterConnectionType.NetworkTcp,
                3 => PrinterConnectionType.RawUsb,
                _ => PrinterConnectionType.Simulated
            };
        }
    }

    public int PaperWidthIndex
    {
        get => ViewModel?.PaperWidth == PaperWidth.Width58mm ? 1 : 0;
        set
        {
            if (ViewModel == null) return;
            ViewModel.PaperWidth = (value == 1) ? PaperWidth.Width58mm : PaperWidth.Width80mm;
        }
    }

    public HardwareSettingsTab()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += (_, _) =>
                {
                    DispatcherQueue?.TryEnqueue(() => Bindings.Update());
                };
            }
        };
    }

    private async void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.LoadHardwareConfigAsync();
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.SaveHardwareConfigAsync();
        }
    }

    private async void OnCheckHealthClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.CheckHardwareStatusAsync();
        }
    }

    private async void OnTestPrintClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.RunTestPrintAsync();
        }
    }

    private async void OnTestDrawerClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.RunTestDrawerKickAsync();
        }
    }

    private async void OnPollScaleClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.PollScaleWeightAsync();
        }
    }

    private async void OnZeroScaleClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.ZeroScaleAsync();
        }
    }

    private async void OnTareScaleClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.TareScaleAsync();
        }
    }
}
