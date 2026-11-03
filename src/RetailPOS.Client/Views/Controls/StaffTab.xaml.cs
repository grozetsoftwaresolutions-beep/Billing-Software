using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class StaffTab : UserControl
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public int NewUserRoleIndex
    {
        get => ViewModel?.NewUserRole switch
        {
            RoleType.Cashier => 0,
            RoleType.Manager => 1,
            RoleType.Viewer => 2,
            RoleType.Admin => 3,
            RoleType.Owner => 4,
            _ => 0
        };
        set
        {
            if (ViewModel == null) return;
            ViewModel.NewUserRole = value switch
            {
                0 => RoleType.Cashier,
                1 => RoleType.Manager,
                2 => RoleType.Viewer,
                3 => RoleType.Admin,
                4 => RoleType.Owner,
                _ => RoleType.Cashier
            };
        }
    }

    public StaffTab()
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

    private async void OnCreateUserClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.CreateUserAsync();
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.LoadUsersAsync();
        }
    }

    private async void OnUnlockClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UserItemViewModel user } && ViewModel != null)
        {
            await ViewModel.UnlockUserAsync(user);
        }
    }

    private async void OnToggleUserActiveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UserItemViewModel user } && ViewModel != null)
        {
            if (user.IsActive)
            {
                await ViewModel.DeactivateUserAsync(user);
            }
            else
            {
                await ViewModel.ActivateUserAsync(user);
            }
        }
    }

    private async void OnResetCredentialsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UserItemViewModel user } || ViewModel == null)
            return;

        var passwordBox = new PasswordBox { Header = "New Password (optional)" };
        var pinBox = new PasswordBox { Header = "New PIN (optional, 4-6 digits)", MaxLength = 6 };

        var dialog = new ContentDialog
        {
            Title = $"Reset Credentials for {user.Username}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { passwordBox, pinBox }
            },
            PrimaryButtonText = "Reset Credentials",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.ResetUserCredentialsAsync(user, passwordBox.Password, pinBox.Password);
        }
    }

    private async void OnChangeRoleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UserItemViewModel user } || ViewModel == null)
            return;

        if (!ViewModel.IsOwner)
        {
            ViewModel.ErrorMessage = "Role modification is restricted to the Owner role.";
            return;
        }

        var roleCombo = new ComboBox
        {
            Header = "Select New Role",
            Width = 240,
            Items = { "Cashier", "Manager", "Viewer", "Admin", "Owner" },
            SelectedIndex = user.Role switch
            {
                RoleType.Cashier => 0,
                RoleType.Manager => 1,
                RoleType.Viewer => 2,
                RoleType.Admin => 3,
                RoleType.Owner => 4,
                _ => 0
            }
        };

        var dialog = new ContentDialog
        {
            Title = $"Change Role for {user.Username}",
            Content = roleCombo,
            PrimaryButtonText = "Update Role",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var newRole = roleCombo.SelectedIndex switch
            {
                0 => RoleType.Cashier,
                1 => RoleType.Manager,
                2 => RoleType.Viewer,
                3 => RoleType.Admin,
                4 => RoleType.Owner,
                _ => RoleType.Cashier
            };

            await ViewModel.ChangeUserRoleAsync(user, newRole);
        }
    }
}
