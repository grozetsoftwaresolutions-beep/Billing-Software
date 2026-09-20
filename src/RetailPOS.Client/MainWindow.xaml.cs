using System;
using Microsoft.UI.Xaml;

namespace RetailPOS.Client;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "GROZET RETAILPOS";

        // Configure initial window size and position
        ConfigureWindowPlacement();
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
                appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var centeredX = Math.Max(0, (displayArea.WorkArea.Width - 1280) / 2);
                    var centeredY = Math.Max(0, (displayArea.WorkArea.Height - 800) / 2);
                    appWindow.Move(new Windows.Graphics.PointInt32(centeredX, centeredY));
                }
            }
        }
        catch
        {
            // Best-effort window placement
        }
    }
}
