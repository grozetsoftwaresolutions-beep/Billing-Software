using System;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;
using RetailPOS.Contracts.Inventory;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class StockAdjustmentDialog : ContentDialog
{
    public InventoryViewModel ViewModel { get; }

    public string TargetProductText => $"[{ViewModel.AdjustmentSku}] {ViewModel.AdjustmentProductName}";
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ViewModel.AdjustmentErrorMessage);

    public int AdjustmentTypeIndex
    {
        get => ViewModel.SelectedAdjustmentType == ManualStockAdjustmentType.Increase ? 0 : 1;
        set
        {
            ViewModel.SelectedAdjustmentType = value == 0
                ? ManualStockAdjustmentType.Increase
                : ManualStockAdjustmentType.Decrease;
            ViewModel.UpdateProjectedStock();
            Bindings.Update();
        }
    }

    public StockAdjustmentDialog(InventoryViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.InitializeComponent();

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ViewModel.ValidateAdjustment(out _))
        {
            args.Cancel = true;
            Bindings.Update();
        }
    }

    private void OnQuantityChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.UpdateProjectedStock();
        Bindings.Update();
    }
}
