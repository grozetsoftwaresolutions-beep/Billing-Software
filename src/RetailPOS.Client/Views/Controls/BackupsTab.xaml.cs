using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetailPOS.Client.ViewModels;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class BackupsTab : UserControl
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public bool IsCreateEnabled => ViewModel != null && !ViewModel.IsCreatingBackup && !ViewModel.IsLoading;

    public BackupsTab()
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

    private async void OnCreateBackupClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.CreateBackupAsync();
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.LoadBackupsAsync();
        }
    }
}
