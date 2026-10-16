using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.ViewModels;
using Windows.System;
using Windows.UI;

namespace RetailPOS.Client.Views;

public sealed partial class LoginPage : Page
{
    private static readonly SolidColorBrush ActiveTabBackground = new(Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush InactiveTabBackground = new(Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush ActiveTextBrush = new(Color.FromArgb(255, 15, 23, 42));
    private static readonly SolidColorBrush InactiveTextBrush = new(Color.FromArgb(255, 100, 116, 139));
    private static readonly SolidColorBrush ActiveIconBrush = new(Color.FromArgb(255, 2, 132, 199));
    private static readonly SolidColorBrush InactiveIconBrush = new(Color.FromArgb(255, 148, 163, 184));

    public LoginViewModel ViewModel { get; }

    public Visibility PinModeVisibility => ViewModel.IsPinMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PasswordModeVisibility => !ViewModel.IsPinMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => ViewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadingVisibility => ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public bool SubmitEnabled => !ViewModel.IsLoading;

    public LoginPage()
    {
        ViewModel = new LoginViewModel();
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, _) =>
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                Bindings.Update();
                UpdateTabVisuals();
            });
        };

        UpdateTabVisuals();

#if DEBUG
        if (!string.IsNullOrEmpty(App.AutomationInitialUsername))
        {
            ViewModel.Username = App.AutomationInitialUsername;
        }
        if (!string.IsNullOrEmpty(App.AutomationInitialPassword))
        {
            PasswordInput.Password = App.AutomationInitialPassword;
            ViewModel.IsPinMode = false;
        }
        if (!string.IsNullOrEmpty(App.AutomationInitialPin))
        {
            PinInput.Password = App.AutomationInitialPin;
            ViewModel.IsPinMode = true;
        }
        if (App.AutomationTerminalId.HasValue)
        {
            ViewModel.EnrolledTerminalId = App.AutomationTerminalId.Value;
            ViewModel.IsTerminalEnrolled = true;
        }
        UpdateTabVisuals();
#endif
    }

    private void UpdateTabVisuals()
    {
        if (ViewModel.IsPinMode)
        {
            PinModeButton.Background = ActiveTabBackground;
            PasswordModeButton.Background = InactiveTabBackground;
        }
        else
        {
            PinModeButton.Background = InactiveTabBackground;
            PasswordModeButton.Background = ActiveTabBackground;
        }
    }

    private void OnPinModeClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPinMode = true;
        ViewModel.ErrorMessage = null;
        ViewModel.HasError = false;
        UpdateTabVisuals();
        Bindings.Update();
    }

    private void OnPasswordModeClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPinMode = false;
        ViewModel.ErrorMessage = null;
        ViewModel.HasError = false;
        UpdateTabVisuals();
        Bindings.Update();
    }

    private void OnKeypadDigitClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string digit)
        {
            if (PinInput.Password.Length < 8)
            {
                PinInput.Password += digit;
            }
        }
    }

    private void OnKeypadClearClicked(object sender, RoutedEventArgs e)
    {
        PinInput.Password = string.Empty;
    }

    private void OnKeypadBackspaceClicked(object sender, RoutedEventArgs e)
    {
        if (PinInput.Password.Length > 0)
        {
            PinInput.Password = PinInput.Password[..^1];
        }
    }

    private async void OnPinSubmitClicked(object sender, RoutedEventArgs e)
    {
        var pin = PinInput.Password;
        PinInput.Password = string.Empty; // Zero immediately from UI control

        await ViewModel.LoginAsync(pin);
    }

    private async void OnPasswordSubmitClicked(object sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Password;
        PasswordInput.Password = string.Empty; // Zero immediately from UI control

        await ViewModel.LoginAsync(password);
    }

    private void OnPinInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnPinSubmitClicked(sender, e);
        }
    }

    private void OnPasswordInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnPasswordSubmitClicked(sender, e);
        }
    }
}
