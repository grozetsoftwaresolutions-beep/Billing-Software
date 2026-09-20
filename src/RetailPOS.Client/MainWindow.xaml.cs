using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RetailPOS.Client.ViewModels;
using Windows.UI;

namespace RetailPOS.Client;

public sealed partial class MainWindow : Window
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromArgb(255, 234, 179, 8));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(255, 239, 68, 68));

    private bool _initialized;

    public MainWindowViewModel ViewModel { get; }

    public SolidColorBrush ServerStatusDotBrush =>
        ViewModel.IsServerConnected
            ? GreenBrush
            : (ViewModel.IsLoading ? YellowBrush : RedBrush);

    public MainWindow()
    {
        ViewModel = new MainWindowViewModel();
        this.InitializeComponent();
        this.Title = "GROZET RETAILPOS";

        ViewModel.PropertyChanged += (_, _) =>
        {
            this.Bindings.Update();
        };

        // Configure initial window size and position
        ConfigureWindowPlacement();

        // Initialize server discovery when window is activated
        this.Activated += OnWindowActivated;
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_initialized)
        {
            _initialized = true;
            await ViewModel.InitializeAsync();
            this.Bindings.Update();
        }
    }

    private void ConfigureWindowPlacement()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(1150, 750));
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var centeredX = Math.Max(0, (displayArea.WorkArea.Width - 1150) / 2);
                    var centeredY = Math.Max(0, (displayArea.WorkArea.Height - 750) / 2);
                    appWindow.Move(new Windows.Graphics.PointInt32(centeredX, centeredY));
                }
            }
        }
        catch
        {
            // Best-effort placement
        }
    }
}
