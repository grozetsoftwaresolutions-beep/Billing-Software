using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class StoreSettingsTab : UserControl
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public bool IsSaveEnabled => ViewModel != null && !ViewModel.IsSaving && !ViewModel.IsLoading;
    public Visibility SavingVisibility => (ViewModel?.IsSaving == true) ? Visibility.Visible : Visibility.Collapsed;

    public StoreSettingsTab()
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

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.SaveStoreSettingsAsync();
        }
    }
}
