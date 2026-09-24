using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class ProductEditDialog : ContentDialog
{
    public ProductEditViewModel ViewModel { get; }

    public event EventHandler<string>? AddBarcodeRequested;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    public Visibility CreateOnlyVisibility => ViewModel.IsCreateMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditOnlyVisibility => !ViewModel.IsCreateMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnitWarningVisibility => !string.IsNullOrWhiteSpace(ViewModel.UnitImmutabilityReason) ? Visibility.Visible : Visibility.Collapsed;

    public ProductEditDialog(ProductEditViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.InitializeComponent();

        Title = ViewModel.IsCreateMode ? "Add New Product" : "Edit Product";

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ViewModel.Validate(out _))
        {
            args.Cancel = true;
            Bindings.Update();
        }
    }

    private void OnAddBarcodeClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.NewBarcodeInput))
        {
            AddBarcodeRequested?.Invoke(this, ViewModel.NewBarcodeInput.Trim());
        }
    }
}
