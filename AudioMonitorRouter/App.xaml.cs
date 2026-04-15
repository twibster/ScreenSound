using AudioMonitorRouter.Interop;
using AudioMonitorRouter.Views;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioMonitorRouter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Force PerMonitorV2 DPI awareness before WPF creates any windows.
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { /* Already set via manifest, or older Windows */ }

        base.OnStartup(e);

        // Create window manually so we can control visibility
        var window = new MainWindow();
        MainWindow = window;

        if (!window.ShouldStartMinimized)
        {
            window.Show();
        }
        // Otherwise, tray icon is already set up — window stays hidden
    }
}

public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
