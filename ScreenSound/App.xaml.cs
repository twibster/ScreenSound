using ScreenSound.Interop;
using ScreenSound.Views;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Data;

namespace ScreenSound;

public partial class App : Application
{
    // Single-instance guard. Two ScreenSound processes would race on the
    // same HKCU Run-at-startup state, both create tray icons, and both
    // fight over the per-session audio-policy calls for the same sessions.
    // The "Local\" prefix scopes the mutex to the current Windows login
    // session, so fast-user-switching still lets each user have their own
    // copy while a double-click on the shortcut won't spawn a second one.
    private const string SingleInstanceMutexName = @"Local\ScreenSound-SingleInstance";
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Acquire the mutex FIRST, before any side effects (DPI config,
        // window creation, tray icon). If another copy is already running
        // we want to bail out cleanly with zero partial initialisation.
        _singleInstanceMutex = new Mutex(initiallyOwned: false, name: SingleInstanceMutexName);
        bool acquired;
        try
        {
            acquired = _singleInstanceMutex.WaitOne(millisecondsTimeout: 0);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing. WaitOne still
            // hands us ownership in that case, so treat as success — the
            // mutex is now ours.
            acquired = true;
        }

        if (!acquired)
        {
            MessageBox.Show(
                "ScreenSound is already running — check the system tray.",
                "ScreenSound",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

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

    protected override void OnExit(ExitEventArgs e)
    {
        // Release + dispose so an immediate relaunch can acquire cleanly.
        // Without this, the OS would still reap the mutex on process exit,
        // but explicit release avoids a brief race window where a fast
        // double-click sees the mutex as still held.
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* Not owned (e.g. after an AbandonedMutexException path) — nothing to release. */ }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
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
