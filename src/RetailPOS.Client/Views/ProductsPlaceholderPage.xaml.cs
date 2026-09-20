using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RetailPOS.Client.Views;

public sealed partial class ProductsPlaceholderPage : Page
{
    public ProductsPlaceholderPage()
    {
        this.InitializeComponent();
    }

    private void OnReturnHomeClicked(object sender, RoutedEventArgs e)
    {
        ShellPage.Current?.NavigateTo("Home");
    }
}
