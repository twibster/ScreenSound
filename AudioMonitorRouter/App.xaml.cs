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

/// <summary>
/// <c>null</c> → Visible, non-null → Collapsed. Drives fallback visuals
/// (e.g. a pin glyph in place of an app icon that hasn't been captured yet
/// because the pinned app isn't currently playing audio).
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Non-null → Visible, <c>null</c> → Collapsed. Pairs with
/// <see cref="NullToVisibilityConverter"/> for the primary/fallback pattern.
/// </summary>
public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Non-empty/whitespace string → Visible, empty/null → Collapsed. Used by the
/// About page to show the "Open release page" hyperlink only when the update
/// check has produced a URL to link to.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Flips a bool. Lets us bind an <c>IsEnabled</c> to a "busy" flag without
/// duplicating the inverse state in the ViewModel — e.g. "button is enabled
/// when <c>IsCheckingForUpdate</c> is false".
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
