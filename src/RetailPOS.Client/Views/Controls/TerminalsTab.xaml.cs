using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class TerminalsTab : UserControl
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public Visibility GeneratedTokenVisibility => (ViewModel?.HasGeneratedToken == true)
        ? Visibility.Visible : Visibility.Collapsed;

    public TerminalsTab()
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

    private async void OnGenerateEnrollmentTokenClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.GenerateEnrollmentTokenAsync();
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.LoadTerminalsAsync();
        }
    }

    private void OnCopyTokenClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ViewModel?.GeneratedEnrollmentToken))
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(ViewModel.GeneratedEnrollmentToken);
            Clipboard.SetContent(dataPackage);
            if (ViewModel != null)
            {
                ViewModel.StatusMessage = "Token copied to clipboard.";
            }
        }
    }

    private async void OnToggleStatusClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalItemViewModel terminal } && ViewModel != null)
        {
            await ViewModel.ToggleTerminalStatusAsync(terminal);
        }
    }

    private async void OnRepairTokenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalItemViewModel terminal } && ViewModel != null)
        {
            await ViewModel.GenerateRepairTokenAsync(terminal.Id);
        }
    }

    private async void OnDecommissionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TerminalItemViewModel terminal } || ViewModel == null)
            return;

        var inputTextBox = new TextBox
        {
            PlaceholderText = "Type DECOMMISSION to confirm"
        };

        var dialog = new ContentDialog
        {
            Title = $"Decommission Terminal {terminal.TerminalCode}?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Decommissioning is IRREVERSIBLE. The terminal pairing key and certificates will be permanently revoked.",
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Please type 'DECOMMISSION' below to confirm:",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    inputTextBox
                }
            },
            PrimaryButtonText = "Decommission Permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DecommissionTerminalAsync(terminal, inputTextBox.Text);
        }
    }
}
