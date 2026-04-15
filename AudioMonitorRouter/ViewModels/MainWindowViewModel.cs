using AudioMonitorRouter.Models;
using AudioMonitorRouter.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AudioMonitorRouter.ViewModels;

public partial class MonitorMappingViewModel : ObservableObject
{
    public MonitorInfo Monitor { get; }

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private int _volume = 100; // 0-100, maps to device master volume

    [ObservableProperty]
    private bool _hasDevice; // true when a real device is selected (not "No mapping")

    public string DisplayName => Monitor.DisplayText;

    public ObservableCollection<AudioDeviceInfo> AvailableDevices { get; }

    public event Action? MappingChanged;
    public event Action<MonitorMappingViewModel>? VolumeChanged;

    public MonitorMappingViewModel(MonitorInfo monitor, ObservableCollection<AudioDeviceInfo> devices)
    {
        Monitor = monitor;
        AvailableDevices = devices;
    }

    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo? value)
    {
        HasDevice = value != null && !string.IsNullOrEmpty(value.Id);
        MappingChanged?.Invoke();
    }

    partial void OnVolumeChanged(int value)
    {
        VolumeChanged?.Invoke(this);
    }
}

public partial class SessionDisplayViewModel : ObservableObject
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private uint _processId;
    [ObservableProperty] private string _monitorName = string.Empty;
    [ObservableProperty] private string _audioDeviceName = string.Empty;
    [ObservableProperty] private ImageSource? _icon;
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly AudioSessionService _sessionService;
    private readonly AudioRouterService _routerService;
    private readonly RoutingEngine _routingEngine;
    private readonly SettingsService _settingsService;
    private readonly AudioDeviceNotifier _deviceNotifier;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly Dispatcher _dispatcher;
    private bool _isLoading;
    private bool _disposed;

    // Debounce window for coalescing bursts of device events. Plugging in a USB
    // DAC routinely fires OnDeviceAdded + OnDeviceStateChanged + OnDefaultDeviceChanged
    // back-to-back; without this we'd rebuild the dropdown three times in a row.
    private static readonly TimeSpan DeviceRefreshDebounce = TimeSpan.FromMilliseconds(300);

    private const string AutoStartRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "AudioMonitorRouter";

    private readonly Dictionary<uint, ImageSource?> _iconCache = new();

    public ObservableCollection<MonitorMappingViewModel> Monitors { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();
    public ObservableCollection<SessionDisplayViewModel> ActiveSessions { get; } = new();

    [ObservableProperty]
    private bool _isRoutingEnabled;

    [ObservableProperty]
    private int _monitorCount;

    [ObservableProperty]
    private int _deviceCount;

    [ObservableProperty]
    private bool _autoStartWithWindows;

    [ObservableProperty]
    private bool _minimizeOnClose;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private int _selectedThemeIndex; // 0 = System, 1 = Light, 2 = Dark

    public string[] ThemeOptions { get; } = { "System", "Light", "Dark" };

    [ObservableProperty]
    private int _currentPage; // 0 = Home, 1 = Settings, 2 = About

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;

        _monitorService = new MonitorService();
        _audioDeviceService = new AudioDeviceService();
        _sessionService = new AudioSessionService();
        _routerService = new AudioRouterService();
        _settingsService = new SettingsService();
        _routingEngine = new RoutingEngine(_monitorService, _sessionService, _routerService);

        _routingEngine.SessionsUpdated += OnSessionsUpdated;

        // Debounced timer for device hot-plug refreshes. Constructed bound to the
        // UI dispatcher so the Tick handler runs on the UI thread and can safely
        // mutate the AudioDevices ObservableCollection.
        _deviceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = DeviceRefreshDebounce
        };
        _deviceRefreshTimer.Tick += OnDeviceRefreshTimerTick;

        _deviceNotifier = new AudioDeviceNotifier();
        _deviceNotifier.DevicesChanged += OnDeviceChangeDetected;
        _deviceNotifier.DefaultDeviceChanged += OnDeviceChangeDetected;
        _deviceNotifier.Start();

        LoadDevicesAndMonitors();
        LoadSettings();
    }

    private void LoadDevicesAndMonitors()
    {
        AudioDevices.Clear();
        Monitors.Clear();

        AudioDevices.Add(new AudioDeviceInfo { Id = "", FriendlyName = "(No mapping)" });

        foreach (var device in _audioDeviceService.GetOutputDevices())
        {
            AudioDevices.Add(device);
        }

        _monitorService.InvalidateCache();
        foreach (var monitor in _monitorService.GetMonitors(forceRefresh: true))
        {
            var vm = new MonitorMappingViewModel(monitor, AudioDevices);
            vm.MappingChanged += OnMappingChanged;
            vm.VolumeChanged += OnVolumeChanged;
            Monitors.Add(vm);
        }

        MonitorCount = Monitors.Count;
        DeviceCount = AudioDevices.Count - 1;
    }

    /// <summary>
    /// Hot-plug entry point. Runs on a COM RPC thread. All it does is restart
    /// the debounce timer on the UI thread — the actual refresh happens once
    /// the burst of events settles.
    /// </summary>
    private void OnDeviceChangeDetected()
    {
        if (_disposed) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            // Stop+Start resets the elapsed time, so rapid-fire events keep
            // pushing the refresh out until the stream quiets down.
            _deviceRefreshTimer.Stop();
            _deviceRefreshTimer.Start();
        });
    }

    private void OnDeviceRefreshTimerTick(object? sender, EventArgs e)
    {
        _deviceRefreshTimer.Stop();
        if (_disposed) return;
        RefreshAudioDevicesPreservingSelections();
    }

    /// <summary>
    /// Rebuilds <see cref="AudioDevices"/> from the current endpoint list while
    /// preserving per-monitor selections wherever the same device ID is still
    /// present. Devices that disappeared leave their monitor's selection at
    /// null — the dropdown reflects reality rather than showing a ghost. Must
    /// be called on the UI thread.
    /// </summary>
    private void RefreshAudioDevicesPreservingSelections()
    {
        // Snapshot selections by monitor name so we can restore after rebuilding
        // the device list. Using IDs (not references) because the AudioDeviceInfo
        // instances in AudioDevices are about to be replaced.
        var previousSelections = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.SelectedAudioDevice?.Id ?? string.Empty);

        // _isLoading suppresses SaveSettings while we mutate selections
        // programmatically — a hot-plug must not overwrite the user's saved
        // mappings just because a device went away.
        _isLoading = true;
        try
        {
            AudioDevices.Clear();
            AudioDevices.Add(new AudioDeviceInfo { Id = string.Empty, FriendlyName = "(No mapping)" });
            foreach (var device in _audioDeviceService.GetOutputDevices())
                AudioDevices.Add(device);

            DeviceCount = AudioDevices.Count - 1;

            foreach (var monitorVm in Monitors)
            {
                if (!previousSelections.TryGetValue(monitorVm.Monitor.DeviceName, out var previousId))
                    continue;

                // Match by Id (not reference) — AudioDeviceInfo is rebuilt each refresh.
                // If the device is gone, FirstOrDefault returns null and the binding clears.
                monitorVm.SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == previousId);
            }
        }
        finally
        {
            _isLoading = false;
        }

        // Push to engine even if mappings didn't semantically change — a newly
        // plugged device might make a previously-dangling mapping valid again,
        // and a newly removed one needs the engine to stop trying to route to it.
        PushMappingsToEngine();
    }

    private void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var settings = _settingsService.Load();

            foreach (var mapping in settings.Mappings)
            {
                var monitorVm = Monitors.FirstOrDefault(m => m.Monitor.DeviceName == mapping.MonitorDeviceName);
                if (monitorVm != null)
                {
                    var device = AudioDevices.FirstOrDefault(d => d.Id == mapping.AudioDeviceId);
                    if (device != null)
                        monitorVm.SelectedAudioDevice = device;
                }
            }

            AutoStartWithWindows = settings.AutoStartWithWindows;
            MinimizeOnClose = settings.MinimizeOnClose;
            StartMinimized = settings.StartMinimized;
            SelectedThemeIndex = settings.ThemeMode switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            // Read current volume from each assigned device
            foreach (var m in Monitors)
            {
                if (m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
                {
                    var vol = _audioDeviceService.GetDeviceVolume(m.SelectedAudioDevice.Id);
                    m.Volume = (int)Math.Round(vol * 100);
                }
            }

            PushMappingsToEngine();
            IsRoutingEnabled = settings.IsEnabled;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void OnMappingChanged()
    {
        PushMappingsToEngine();
        if (!_isLoading)
            SaveSettings();

        // Read the current volume from the newly selected device
        foreach (var m in Monitors)
        {
            if (m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
            {
                var vol = _audioDeviceService.GetDeviceVolume(m.SelectedAudioDevice.Id);
                m.Volume = (int)Math.Round(vol * 100);
            }
        }
    }

    private void OnVolumeChanged(MonitorMappingViewModel vm)
    {
        if (vm.SelectedAudioDevice == null || string.IsNullOrEmpty(vm.SelectedAudioDevice.Id))
            return;

        _audioDeviceService.SetDeviceVolume(vm.SelectedAudioDevice.Id, vm.Volume / 100f);
    }

    private void PushMappingsToEngine()
    {
        var mappings = Monitors
            .Where(m => m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
            .Select(m => new MonitorAudioMapping
            {
                MonitorDeviceName = m.Monitor.DeviceName,
                AudioDeviceId = m.SelectedAudioDevice!.Id
            })
            .ToList();

        _routingEngine.UpdateMappings(mappings);
    }

    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            IsEnabled = IsRoutingEnabled,
            AutoStartWithWindows = AutoStartWithWindows,
            MinimizeOnClose = MinimizeOnClose,
            StartMinimized = StartMinimized,
            ThemeMode = SelectedThemeIndex switch { 1 => "Light", 2 => "Dark", _ => "System" },
            Mappings = Monitors
                .Where(m => m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
                .Select(m => new MonitorAudioMapping
                {
                    MonitorDeviceName = m.Monitor.DeviceName,
                    AudioDeviceId = m.SelectedAudioDevice!.Id
                })
                .ToList()
        };

        _settingsService.Save(settings);
    }

    partial void OnIsRoutingEnabledChanged(bool value)
    {
        if (value)
        {
            PushMappingsToEngine();
            _routingEngine.Start();
        }
        else
        {
            _routingEngine.Stop();
            ActiveSessions.Clear();
        }

        if (!_isLoading)
            SaveSettings();
    }

    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        if (!_isLoading)
        {
            ApplyAutoStart(value);
            SaveSettings();
        }
    }

    partial void OnMinimizeOnCloseChanged(bool value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                    key.SetValue(AppRegistryName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppRegistryName, false);
            }
        }
        catch { }
    }

    [RelayCommand]
    private void Refresh()
    {
        var wasRunning = IsRoutingEnabled;
        if (wasRunning)
            _routingEngine.Stop();

        var currentMappings = Monitors
            .Where(m => m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
            .ToDictionary(m => m.Monitor.DeviceName, m => m.SelectedAudioDevice!.Id);

        LoadDevicesAndMonitors();

        _isLoading = true;
        try
        {
            foreach (var monitorVm in Monitors)
            {
                if (currentMappings.TryGetValue(monitorVm.Monitor.DeviceName, out var deviceId))
                {
                    var device = AudioDevices.FirstOrDefault(d => d.Id == deviceId);
                    if (device != null)
                        monitorVm.SelectedAudioDevice = device;
                }
            }
        }
        finally
        {
            _isLoading = false;
        }

        if (wasRunning)
        {
            PushMappingsToEngine();
            _routingEngine.Start();
            _isLoading = true;
            IsRoutingEnabled = true;
            _isLoading = false;
        }
    }

    private ImageSource? GetProcessIcon(uint processId)
    {
        if (_iconCache.TryGetValue(processId, out var cached))
            return cached;

        ImageSource? icon = null;
        try
        {
            var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName;
            if (path != null)
            {
                using var drawingIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (drawingIcon != null)
                {
                    icon = Imaging.CreateBitmapSourceFromHIcon(
                        drawingIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    icon.Freeze();
                }
            }
        }
        catch { }

        _iconCache[processId] = icon;
        return icon;
    }

    private void OnSessionsUpdated(List<AudioSessionInfo> sessions)
    {
        var sessionData = sessions.Select(s => new
        {
            Session = s,
            Icon = GetProcessIcon(s.ProcessId)
        }).ToList();

        _dispatcher.BeginInvoke(() =>
        {
            ActiveSessions.Clear();
            foreach (var item in sessionData)
            {
                ActiveSessions.Add(new SessionDisplayViewModel
                {
                    ProcessName = item.Session.ProcessName,
                    ProcessId = item.Session.ProcessId,
                    MonitorName = item.Session.MonitorFriendlyName,
                    AudioDeviceName = item.Session.AudioDeviceName,
                    Icon = item.Icon
                });
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe + dispose the notifier BEFORE stopping the timer so any
        // late COM-thread callback that was already queued observes _disposed
        // and bails instead of kicking the timer from a background thread.
        _deviceNotifier.DevicesChanged -= OnDeviceChangeDetected;
        _deviceNotifier.DefaultDeviceChanged -= OnDeviceChangeDetected;
        _deviceNotifier.Dispose();

        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Tick -= OnDeviceRefreshTimerTick;

        _routingEngine.SessionsUpdated -= OnSessionsUpdated;

        // Reset all audio routing back to system defaults
        _routingEngine.ResetAllRouting();

        _routingEngine.Dispose();
        _routerService.Dispose();
        _audioDeviceService.Dispose();
    }
}
