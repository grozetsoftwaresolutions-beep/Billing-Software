using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RetailPOS.Client.ViewModels;
using Windows.System;

namespace RetailPOS.Client.Views;

public sealed partial class LockOverlayControl : UserControl
{
    public LockViewModel ViewModel { get; }

    public Visibility ErrorVisibility => ViewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadingVisibility => ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public bool SubmitEnabled => !ViewModel.IsLoading;

    public LockOverlayControl()
    {
        ViewModel = new LockViewModel();
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, _) =>
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                Bindings.Update();
            });
        };
    }

    public void PrepareForLock()
    {
        LockPinInput.Password = string.Empty;
        ViewModel.RefreshUserContext();
        Bindings.Update();
        LockPinInput.Focus(FocusState.Programmatic);
    }

    private void OnKeypadDigitClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string digit)
        {
            if (LockPinInput.Password.Length < 8)
            {
                LockPinInput.Password += digit;
            }
        }
    }

    private void OnKeypadClearClicked(object sender, RoutedEventArgs e)
    {
        LockPinInput.Password = string.Empty;
    }

    private void OnKeypadBackspaceClicked(object sender, RoutedEventArgs e)
    {
        if (LockPinInput.Password.Length > 0)
        {
            LockPinInput.Password = LockPinInput.Password[..^1];
        }
    }

    private async void OnUnlockClicked(object sender, RoutedEventArgs e)
    {
        var pin = LockPinInput.Password;
        LockPinInput.Password = string.Empty; // Zero immediately from UI control

        await ViewModel.UnlockAsync(pin);
    }

    private async void OnSwitchUserClicked(object sender, RoutedEventArgs e)
    {
        LockPinInput.Password = string.Empty;
        await ViewModel.SwitchUserOrLogoutAsync();
    }

    private void OnLockPinKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnUnlockClicked(sender, e);
        }
    }
}
