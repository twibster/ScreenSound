using AudioMonitorRouter.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace AudioMonitorRouter.Views;

public partial class MainWindow : UiWindow
{
    private readonly MainWindowViewModel _viewModel;
    private Forms.NotifyIcon? _trayIcon;
    private bool _forceClose;

    private Border? _activePage;
    private readonly Border[] _pages;

    public bool ShouldStartMinimized => _viewModel.StartMinimized;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        // Order must match the Tag indices on the sidebar RadioButtons:
        //   0 = Home, 1 = Pinned, 2 = Settings, 3 = About.
        _pages = new[] { HomePage, PinnedPage, SettingsPage, AboutPage };

        SetupTrayIcon();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Listen for system theme changes to re-override when in manual mode
        Theme.Changed += OnGlobalThemeChanged;

        Loaded += (_, _) =>
        {
            // Start watcher for Mica backdrop effect (always needed)
            Watcher.Watch(this, BackgroundType.Mica, true);

            // Override with the user's chosen theme
            ApplyTheme(_viewModel.SelectedThemeIndex);

            ShowPage(0, animate: false);
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedThemeIndex))
        {
            ApplyTheme(_viewModel.SelectedThemeIndex);
        }
    }

    private void OnGlobalThemeChanged(ThemeType currentTheme, System.Windows.Media.Color systemAccent)
    {
        // When the watcher changes the theme (system theme changed),
        // re-override if user has a manual theme selected
        var themeIndex = _viewModel.SelectedThemeIndex;
        if (themeIndex == 1 || themeIndex == 2)
        {
            // User wants a fixed theme, re-apply it
            Dispatcher.BeginInvoke(() => ApplyTheme(themeIndex));
        }
    }

    private void ApplyTheme(int themeIndex)
    {
        switch (themeIndex)
        {
            case 1: // Light
                Theme.Apply(ThemeType.Light, BackgroundType.Mica, true, true);
                break;
            case 2: // Dark
                Theme.Apply(ThemeType.Dark, BackgroundType.Mica, true, true);
                break;
            default: // System - detect current system theme and apply it
                var systemTheme = Theme.GetSystemTheme();
                var isDark = systemTheme is SystemThemeType.Dark
                    or SystemThemeType.CapturedMotion or SystemThemeType.Glow;
                Theme.Apply(isDark ? ThemeType.Dark : ThemeType.Light, BackgroundType.Mica, true, true);
                break;
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon();

        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath != null)
                _trayIcon.Icon = Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch
        {
            _trayIcon.Icon = Drawing.SystemIcons.Application;
        }

        _trayIcon.Text = "Audio Monitor Router";
        _trayIcon.Visible = true;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ForceClose());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        // Guard: Checked fires during InitializeComponent before _pages is set
        if (_pages == null) return;

        if (sender is RadioButton rb && rb.Tag is string page
            && int.TryParse(page, out var index))
        {
            _viewModel.CurrentPage = index;
            ShowPage(index, animate: true);
        }
    }

    private void ShowPage(int index, bool animate)
    {
        if (index < 0 || index >= _pages.Length) return;

        var target = _pages[index];
        if (target == _activePage) return;

        // Hide the previous page instantly
        if (_activePage != null)
        {
            _activePage.Visibility = Visibility.Collapsed;
            _activePage.Opacity = 0;
        }

        // Show and animate the new page
        target.Visibility = Visibility.Visible;

        if (animate)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(250));

            // Fade in
            var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            target.BeginAnimation(OpacityProperty, fadeIn);

            // Slide up
            var transform = target.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                target.RenderTransform = transform;
            }
            var slideUp = new DoubleAnimation(20, 0, duration) { EasingFunction = ease };
            transform.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
        else
        {
            target.Opacity = 1;
        }

        _activePage = target;
    }

    // ── Per-app override (pin) UI handlers ────────────────────────────────
    //
    // The session-row ContextMenu is declared with two MenuItems by name
    // (PinToDeviceMenu, RemovePinMenu). On open we rebuild the "Pin to" submenu
    // from the live AudioDevices list and toggle "Remove pin" visibility based
    // on the row's IsOverridden state. The session VM is carried via the row
    // Border's Tag and read back through PlacementTarget.Tag — ContextMenu lives
    // in its own popup window and doesn't inherit the row's DataContext.

    private void SessionRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.ContextMenu is not ContextMenu menu) return;
        if (fe.Tag is not SessionDisplayViewModel session)
        {
            e.Handled = true;
            return;
        }

        var pinSubmenu = menu.Items.OfType<WpfMenuItem>()
            .FirstOrDefault(m => m.Name == "PinToDeviceMenu");
        var removeItem = menu.Items.OfType<WpfMenuItem>()
            .FirstOrDefault(m => m.Name == "RemovePinMenu");
        var separator = menu.Items.OfType<Separator>()
            .FirstOrDefault(s => s.Name == "PinSeparator");

        if (pinSubmenu == null || removeItem == null || separator == null) return;

        // Rebuild the device submenu fresh each open — devices can be hot-plugged
        // between menu uses, and the user's currently-pinned device should appear
        // checked even if it's not the row's currently-routed device.
        pinSubmenu.Items.Clear();

        // Skip the synthetic "(No mapping)" entry — its Id is empty and pinning
        // to it would mean "no device", which the override schema doesn't model
        // (use Remove pin for that).
        var devices = _viewModel.AudioDevices.Where(d => !string.IsNullOrEmpty(d.Id)).ToList();
        if (devices.Count == 0)
        {
            pinSubmenu.Items.Add(new WpfMenuItem { Header = "(No audio devices)", IsEnabled = false });
        }
        else
        {
            // Find the existing pin (if any) so we can show a check next to the
            // device the user already chose for this app.
            var existingPin = _viewModel.AppOverrides.FirstOrDefault(
                o => string.Equals(o.ProcessName, session.ProcessName,
                                   StringComparison.OrdinalIgnoreCase));

            foreach (var device in devices)
            {
                var item = new WpfMenuItem
                {
                    Header = device.FriendlyName,
                    Tag = device.Id,
                    IsCheckable = true,
                    IsChecked = existingPin != null && existingPin.AudioDeviceId == device.Id,
                };
                item.Click += PinToDevice_Click;
                pinSubmenu.Items.Add(item);
            }
        }

        // Hide remove pin (and its separator) when there's nothing to remove.
        var hasPin = session.IsOverridden ||
                     _viewModel.AppOverrides.Any(o => string.Equals(
                         o.ProcessName, session.ProcessName, StringComparison.OrdinalIgnoreCase));
        removeItem.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
        separator.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PinToDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem item) return;
        if (item.Tag is not string deviceId || string.IsNullOrEmpty(deviceId)) return;

        // Walk up to the owning ContextMenu to recover the row's session VM.
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu
                   ?? FindParentContextMenu(item);
        if (menu?.PlacementTarget is FrameworkElement fe && fe.Tag is SessionDisplayViewModel session)
        {
            _viewModel.SetAppOverride(session.ProcessName, deviceId);
        }
    }

    private void RemovePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem item) return;
        var menu = FindParentContextMenu(item);
        if (menu?.PlacementTarget is FrameworkElement fe && fe.Tag is SessionDisplayViewModel session)
        {
            _viewModel.RemoveAppOverride(session.ProcessName);
        }
    }

    // Walks up logical/visual parents to find the enclosing ContextMenu. Needed
    // because ItemsControlFromItemContainer doesn't reach across submenu levels.
    private static ContextMenu? FindParentContextMenu(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is ContextMenu cm) return cm;
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void RemovePinFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AppOverrideViewModel ovr)
        {
            _viewModel.RemoveAppOverride(ovr.ProcessName);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose && _viewModel.MinimizeOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Theme.Changed -= OnGlobalThemeChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
