using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.ViewModels;

namespace RetailPOS.Client.Views.Controls;

public sealed partial class SecuritySettingsTab : UserControl
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public bool IsOwnerEditable => ViewModel?.IsOwner == true;
    public Visibility OwnerControlsVisibility => (ViewModel?.IsOwner == true) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NonOwnerWarningVisibility => (ViewModel?.IsOwner == false) ? Visibility.Visible : Visibility.Collapsed;
    public bool IsSaveEnabled => ViewModel != null && ViewModel.IsOwner && !ViewModel.IsSaving && !ViewModel.IsLoading;
    public Visibility SavingVisibility => (ViewModel?.IsSaving == true) ? Visibility.Visible : Visibility.Collapsed;

    public string EpisodeIdDisplay => string.IsNullOrEmpty(ViewModel?.FailsafeEpisodeId) ? "N/A" : ViewModel.FailsafeEpisodeId;
    public string EnteredAtDisplay => string.IsNullOrEmpty(ViewModel?.FailsafeEnteredAt) ? "N/A" : ViewModel.FailsafeEnteredAt;
    public string ReasonDisplay => string.IsNullOrEmpty(ViewModel?.FailsafeReason) ? "No active telemetry" : ViewModel.FailsafeReason;

    public SolidColorBrush FailsafeBadgeBrush
    {
        get
        {
            var hex = ViewModel?.FailsafeBadgeColor ?? "#64748B";
            try
            {
                if (hex.StartsWith('#') && hex.Length == 7)
                {
                    var r = Convert.ToByte(hex.Substring(1, 2), 16);
                    var g = Convert.ToByte(hex.Substring(3, 2), 16);
                    var b = Convert.ToByte(hex.Substring(5, 2), 16);
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
                }
            }
            catch
            {
                // Fallback
            }
            return new SolidColorBrush(Colors.SlateGray);
        }
    }

    public SecuritySettingsTab()
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
            await ViewModel.SaveSecuritySettingsAsync();
        }
    }
}
