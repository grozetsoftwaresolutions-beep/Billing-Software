using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.ViewModels;
using Windows.UI;

namespace RetailPOS.Client.Views;

public sealed partial class ShellPage : Page
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromArgb(255, 234, 179, 8));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(255, 239, 68, 68));

    public static ShellPage? Current { get; private set; }

    public ShellViewModel ViewModel { get; }

    public SolidColorBrush ServerStatusDotBrush =>
        ViewModel.IsServerConnected
            ? GreenBrush
            : (ViewModel.IsLoading ? YellowBrush : RedBrush);

    public ShellPage()
    {
        Current = this;
        ViewModel = new ShellViewModel();
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, _) =>
        {
            this.Bindings.Update();
        };
    }

    private void OnNavViewLoaded(object sender, RoutedEventArgs e)
    {
        if (NavView.MenuItems.Count > 0)
        {
            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(HomePage));
            ViewModel.CurrentModuleName = "Home / Dashboard";
        }

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
                ContentFrame.Navigate(typeof(PosPlaceholderPage));
                ViewModel.CurrentModuleName = "POS & Billing";
                SelectMenuItemByTag("POS");
                break;

            case "Products":
                ContentFrame.Navigate(typeof(ProductsPlaceholderPage));
                ViewModel.CurrentModuleName = "Products & Inventory";
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
}
