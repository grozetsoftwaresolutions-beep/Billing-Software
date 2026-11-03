using System;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.ViewModels;
using RetailPOS.Domain.Enums;
using Windows.UI.Text;

namespace RetailPOS.Client.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly SolidColorBrush ActiveTabBackground = new(Windows.UI.Color.FromArgb(255, 37, 99, 235)); // #2563EB
    private static readonly SolidColorBrush InactiveTabBackground = new(Windows.UI.Color.FromArgb(255, 241, 245, 249)); // #F1F5F9
    private static readonly SolidColorBrush ActiveTabForeground = new(Colors.White);
    private static readonly SolidColorBrush InactiveTabForeground = new(Windows.UI.Color.FromArgb(255, 71, 85, 105)); // #475569

    public SettingsViewModel ViewModel { get; }

    public Visibility AuthorizedVisibility => ViewModel.IsOwnerOrAdmin ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AccessDeniedVisibility => ViewModel.IsAccessDenied ? Visibility.Visible : Visibility.Collapsed;

    public bool HasErrorMessage => !string.IsNullOrEmpty(ViewModel.ErrorMessage);
    public bool HasStatusMessage => !string.IsNullOrEmpty(ViewModel.StatusMessage);
    public Visibility LoadingVisibility => ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;

    // Role Badge
    public string RoleBadgeText => ViewModel.CurrentRole switch
    {
        RoleType.Owner => "Owner",
        RoleType.Admin => "Administrator",
        _ => ViewModel.CurrentRole.ToString()
    };

    public SolidColorBrush RoleBadgeBackground => ViewModel.CurrentRole switch
    {
        RoleType.Owner => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 232, 255)), // Purple #F3E8FF
        RoleType.Admin => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 242, 254)), // Sky #E0F2FE
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 245, 249))
    };

    public SolidColorBrush RoleBadgeForeground => ViewModel.CurrentRole switch
    {
        RoleType.Owner => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 126, 34, 206)), // Purple #7E22CE
        RoleType.Admin => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 3, 105, 161)), // Sky #0369A1
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 71, 85, 105))
    };

    // Tab Visibilities
    public Visibility Tab0Visibility => ViewModel.SelectedTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab1Visibility => ViewModel.SelectedTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab2Visibility => ViewModel.SelectedTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab3Visibility => ViewModel.SelectedTabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab4Visibility => ViewModel.SelectedTabIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Tab5Visibility => ViewModel.SelectedTabIndex == 5 ? Visibility.Visible : Visibility.Collapsed;

    // Tab Button Styling
    public SolidColorBrush Tab0Background => ViewModel.SelectedTabIndex == 0 ? ActiveTabBackground : InactiveTabBackground;
    public SolidColorBrush Tab0Foreground => ViewModel.SelectedTabIndex == 0 ? ActiveTabForeground : InactiveTabForeground;
    public FontWeight Tab0FontWeight => ViewModel.SelectedTabIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal;

    public SolidColorBrush Tab1Background => ViewModel.SelectedTabIndex == 1 ? ActiveTabBackground : InactiveTabBackground;
    public SolidColorBrush Tab1Foreground => ViewModel.SelectedTabIndex == 1 ? ActiveTabForeground : InactiveTabForeground;
    public FontWeight Tab1FontWeight => ViewModel.SelectedTabIndex == 1 ? FontWeights.SemiBold : FontWeights.Normal;

    public SolidColorBrush Tab2Background => ViewModel.SelectedTabIndex == 2 ? ActiveTabBackground : InactiveTabBackground;
    public SolidColorBrush Tab2Foreground => ViewModel.SelectedTabIndex == 2 ? ActiveTabForeground : InactiveTabForeground;
    public FontWeight Tab2FontWeight => ViewModel.SelectedTabIndex == 2 ? FontWeights.SemiBold : FontWeights.Normal;

    public SolidColorBrush Tab3Background => ViewModel.SelectedTabIndex == 3 ? ActiveTabBackground : InactiveTabBackground;
    public SolidColorBrush Tab3Foreground => ViewModel.SelectedTabIndex == 3 ? ActiveTabForeground : InactiveTabForeground;
    public FontWeight Tab3FontWeight => ViewModel.SelectedTabIndex == 3 ? FontWeights.SemiBold : FontWeights.Normal;

    public SolidColorBrush Tab4Background => ViewModel.SelectedTabIndex == 4 ? ActiveTabBackground : InactiveTabBackground;
    public SolidColorBrush Tab4Foreground => ViewModel.SelectedTabIndex == 4 ? ActiveTabForeground : InactiveTabForeground;
    public FontWeight Tab4FontWeight => ViewModel.SelectedTabIndex == 4 ? FontWeights.SemiBold : FontWeights.Normal;

    public SolidColorBrush Tab5Background => ViewModel.SelectedTabIndex == 5 ? ActiveTabBackground : InactiveTabBackground;
    public SolidColorBrush Tab5Foreground => ViewModel.SelectedTabIndex == 5 ? ActiveTabForeground : InactiveTabForeground;
    public FontWeight Tab5FontWeight => ViewModel.SelectedTabIndex == 5 ? FontWeights.SemiBold : FontWeights.Normal;

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel();
        InitializeComponent();

        ViewModel.PropertyChanged += (_, _) =>
        {
            DispatcherQueue?.TryEnqueue(() => Bindings.Update());
        };

        Loaded += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
        };
    }

    private void OnTab0Clicked(object sender, RoutedEventArgs e) => ViewModel.SelectedTabIndex = 0;
    private void OnTab1Clicked(object sender, RoutedEventArgs e) => ViewModel.SelectedTabIndex = 1;
    private void OnTab2Clicked(object sender, RoutedEventArgs e) => ViewModel.SelectedTabIndex = 2;
    private void OnTab3Clicked(object sender, RoutedEventArgs e) => ViewModel.SelectedTabIndex = 3;
    private void OnTab4Clicked(object sender, RoutedEventArgs e) => ViewModel.SelectedTabIndex = 4;
    private void OnTab5Clicked(object sender, RoutedEventArgs e) => ViewModel.SelectedTabIndex = 5;
}
