using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;

namespace RetailPOS.Client.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }

    public HomePage()
    {
        ViewModel = new HomeViewModel();
        this.InitializeComponent();
    }

    private void OnNavigateToPosClicked(object sender, RoutedEventArgs e)
    {
        ShellPage.Current?.NavigateTo("POS");
    }

    private void OnNavigateToProductsClicked(object sender, RoutedEventArgs e)
    {
        ShellPage.Current?.NavigateTo("Products");
    }

    private void OnNavigateToReportsClicked(object sender, RoutedEventArgs e)
    {
        ShellPage.Current?.NavigateTo("Reports");
    }

    private void OnNavigateToSettingsClicked(object sender, RoutedEventArgs e)
    {
        ShellPage.Current?.NavigateTo("Settings");
    }
}
