using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using Windows.UI;

namespace RetailPOS.Client.Views;

public sealed partial class ShellPage : Page
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromArgb(255, 234, 179, 8));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(255, 239, 68, 68));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromArgb(255, 245, 158, 11));

    public static ShellPage? Current { get; private set; }
#if DEBUG
    public static string? ForceInitialNavigationTag { get; set; }
#endif

    public ShellViewModel ViewModel { get; }

    public SolidColorBrush ServerStatusDotBrush =>
        ViewModel.IsServerConnected
            ? GreenBrush
            : (ViewModel.IsLoading ? YellowBrush : RedBrush);

    public SolidColorBrush TerminalStatusDotBrush =>
        ViewModel.IsTerminalReady
            ? GreenBrush
            : AmberBrush;

    public Visibility AuthenticatedVisibility =>
        ViewModel.IsUserAuthenticated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UnauthenticatedVisibility =>
        !ViewModel.IsUserAuthenticated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LockOverlayVisibility =>
        ViewModel.IsSessionLocked ? Visibility.Visible : Visibility.Collapsed;

    public ShellPage()
    {
        Current = this;
        ViewModel = new ShellViewModel();
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, e) =>
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                this.Bindings.Update();

                if (e.PropertyName == nameof(ViewModel.IsUserAuthenticated))
                {
                    HandleAuthStateChanged();
                }
                else if (e.PropertyName == nameof(ViewModel.IsSessionLocked))
                {
                    if (ViewModel.IsSessionLocked)
                    {
                        LockOverlay.PrepareForLock();
                    }
                }
            });
        };

        ClientSessionManager.Instance.StateChanged += OnSessionStateChanged;
    }

    private void OnSessionStateChanged(ClientSessionState state)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            Bindings.Update();
            HandleAuthStateChanged();
            if (state == ClientSessionState.Locked)
            {
                LockOverlay.PrepareForLock();
            }
        });
    }

    private void HandleAuthStateChanged()
    {
        if (ViewModel.IsUserAuthenticated)
        {
            ApplyRolePermissions();
            if (ContentFrame.Content == null)
            {
#if DEBUG
                if (!string.IsNullOrEmpty(ForceInitialNavigationTag))
                {
                    NavigateTo(ForceInitialNavigationTag);
                }
                else
#endif
                {
                    ContentFrame.Navigate(typeof(HomePage));
                    ViewModel.CurrentModuleName = "Home / Dashboard";
                    NavView.SelectedItem = NavHome;
                }
            }
        }
        else
        {
            // Show login page
            if (LoginHostFrame.Content is not LoginPage)
            {
                LoginHostFrame.Navigate(typeof(LoginPage));
            }
            ContentFrame.Content = null;
        }
    }

    private void ApplyRolePermissions()
    {
        var role = ClientSessionManager.Instance.CurrentRole;
        if (role == null) return;

        var currentRole = role.Value;
        NavHome.Visibility = Visibility.Visible;

        NavPos.Visibility = RolePermissions.HasPermission(currentRole, Permission.ProcessSale)
            ? Visibility.Visible : Visibility.Collapsed;

        NavSalesHistory.Visibility = (currentRole == RoleType.Owner || currentRole == RoleType.Admin || currentRole == RoleType.Manager)
            ? Visibility.Visible : Visibility.Collapsed;

        NavCustomers.Visibility = (currentRole != RoleType.Viewer)
            ? Visibility.Visible : Visibility.Collapsed;

        NavProducts.Visibility = (currentRole == RoleType.Owner || currentRole == RoleType.Admin || currentRole == RoleType.Manager)
            ? Visibility.Visible : Visibility.Collapsed;

        NavReports.Visibility = RolePermissions.HasPermission(currentRole, Permission.ViewReports)
            ? Visibility.Visible : Visibility.Collapsed;

        var canSettings = RolePermissions.HasPermission(currentRole, Permission.ManageUsers)
            || RolePermissions.HasPermission(currentRole, Permission.ManageStoreSettings)
            || RolePermissions.HasPermission(currentRole, Permission.ManageTerminals);

        NavView.IsSettingsVisible = canSettings;
    }

    private void OnNavViewLoaded(object sender, RoutedEventArgs e)
    {
        HandleAuthStateChanged();

        // Run non-blocking server discovery in background
        _ = ViewModel.InitializeAsync();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPlaceholderPage));
            ViewModel.CurrentModuleName = "Settings & Administration";
            return;
        }

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    public void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "Home":
                ContentFrame.Navigate(typeof(HomePage));
                ViewModel.CurrentModuleName = "Home / Dashboard";
                SelectMenuItemByTag("Home");
                break;

            case "POS":
                ContentFrame.Navigate(typeof(PosPage));
                ViewModel.CurrentModuleName = "POS & Billing";
                SelectMenuItemByTag("POS");
                break;

            case "SalesHistory":
                if (ClientSessionManager.Instance.CurrentRole is RoleType.Cashier or RoleType.Viewer)
                {
                    // Programmatic guard: Cashier and Viewer must not access Sales History
                    return;
                }
                ContentFrame.Navigate(typeof(SalesHistoryPage));
                ViewModel.CurrentModuleName = "Sales History & Invoices";
                SelectMenuItemByTag("SalesHistory");
                break;

            case "Customers":
                if (ClientSessionManager.Instance.CurrentRole is RoleType.Viewer)
                {
                    // Programmatic guard: Viewer must not access Customers & Credit
                    return;
                }
                ContentFrame.Navigate(typeof(CustomersPage));
                ViewModel.CurrentModuleName = "Customers & Credit";
                SelectMenuItemByTag("Customers");
                break;

            case "Products":
                ContentFrame.Navigate(typeof(ProductsPage));
                ViewModel.CurrentModuleName = "Products & Stock";
                SelectMenuItemByTag("Products");
                break;

            case "Reports":
                ContentFrame.Navigate(typeof(ReportsPlaceholderPage));
                ViewModel.CurrentModuleName = "Reports & Analytics";
                SelectMenuItemByTag("Reports");
                break;

            case "Settings":
                ContentFrame.Navigate(typeof(SettingsPlaceholderPage));
                ViewModel.CurrentModuleName = "Settings & Administration";
                NavView.SelectedItem = NavView.SettingsItem;
                break;
        }
    }

    private void SelectMenuItemByTag(string tag)
    {
        foreach (var menuItem in NavView.MenuItems)
        {
            if (menuItem is NavigationViewItem navItem && navItem.Tag as string == tag)
            {
                NavView.SelectedItem = navItem;
                break;
            }
        }
    }

    private async void OnHeaderLockClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.LockAsync();
    }

    private async void OnHeaderLogoutClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.LogoutAsync();
    }
}
