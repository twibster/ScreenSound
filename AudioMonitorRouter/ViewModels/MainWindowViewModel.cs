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

    /// <summary>
    /// The user's persistent intent — the audio device ID this monitor should
    /// map to. Distinct from <see cref="SelectedAudioDevice"/>, which mirrors
    /// what's actually present in the dropdown right now.
    /// <para>
    /// When a device is unplugged, <see cref="SelectedAudioDevice"/> goes null
    /// (FirstOrDefault returns nothing for a missing ID), but DesiredAudioDeviceId
    /// stays — so re-plugging the same endpoint, or restarting the app while it
    /// happens to be present again, restores the mapping. Empty string means
    /// "(No mapping)" / explicitly cleared.
    /// </para>
    /// </summary>
    public string DesiredAudioDeviceId { get; set; } = string.Empty;

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
        // Capture the user's intent — but only when the change comes with a
        // real value. The refresh path assigns null when the device the user
        // wanted is currently unavailable; that must NOT erase the desired ID,
        // otherwise re-plugging the device can't restore the mapping. User
        // picks always carry a non-null value (the "(No mapping)" sentinel
        // included — its Id is "").
        if (value != null)
            DesiredAudioDeviceId = value.Id;
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

    /// <summary>
    /// True when this session is being routed by an explicit per-app pin rather
    /// than the monitor → device mapping. Drives the pin glyph in the row UI and
    /// the "Remove pin" entry in the right-click menu.
    /// </summary>
    [ObservableProperty] private bool _isOverridden;
}

/// <summary>
/// One entry in the Pinned Apps list. Wraps a process name + the device the
/// user has pinned it to. <see cref="AudioDeviceName"/> is resolved against the
/// current device list each time the overrides UI refreshes — if the pinned
/// device is currently unplugged, name resolution falls back to a placeholder
/// while the underlying ID stays intact, mirroring the monitor-mapping behavior.
/// </summary>
public partial class AppOverrideViewModel : ObservableObject
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private string _audioDeviceId = string.Empty;
    [ObservableProperty] private string _audioDeviceName = string.Empty;

    /// <summary>
    /// App icon shown in the Pinned page. Captured from the originating session
    /// at pin-creation time, or back-filled by <c>MainWindowViewModel</c> when a
    /// pin loaded from disk matches a live session on the next session update.
    /// Null while the pinned app isn't running — the UI falls back to a pin glyph.
    /// </summary>
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
    private readonly UpdateService _updateService;
    private readonly AudioDeviceNotifier _deviceNotifier;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly Dispatcher _dispatcher;
    private bool _isLoading;
    // Volatile: read on the COM RPC thread in OnDeviceChangeDetected, written
    // on the UI thread in Dispose(). Without the barrier a late callback can
    // miss the write and queue dispatcher work during shutdown.
    private volatile bool _disposed;

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

    /// <summary>
    /// Per-app device pins shown in the "Pinned Apps" page and the source of
    /// truth for what the routing engine sees. Process name is the lookup key
    /// (case-insensitive). Source of additions is the right-click menu on an
    /// active session; removals come from the pin button on this page or the
    /// "Remove pin" entry on the session's context menu.
    /// </summary>
    public ObservableCollection<AppOverrideViewModel> AppOverrides { get; } = new();

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
    private int _currentPage; // 0 = Home, 1 = Pinned, 2 = Settings, 3 = About

    // --- About page bindings ---------------------------------------------
    //
    // AppVersionDisplay is read once at construction from the assembly's
    // informational version so the About page always shows the build that's
    // actually running — no more hardcoded "Version 1.0.0" drifting behind
    // the actual release tag.
    //
    // UpdateStatus is a single line of text rendered under the "Check for
    // updates" button. States cycle through:
    //   ""                    — idle, button hidden-label state
    //   "Checking…"           — request in flight
    //   "You're up to date"   — latest == current
    //   "vX.Y.Z available — click to open"   — newer release found
    //   "Couldn't reach GitHub: …"           — network/rate-limit failure
    //
    // LatestReleaseUrl is non-empty only when an update is available; the XAML
    // binds its visibility so the "Download" hyperlink appears only then.

    public string AppVersionDisplay => $"Version {_updateService.CurrentVersion}";

    /// <summary>
    /// Copyright footer rendered at the bottom of the About page. Takes the
    /// standard "Copyright (c) YYYY …" string emitted by MSBuild and swaps the
    /// "(c)" for a proper © glyph — same info, cleaner typography. We do the
    /// swap here rather than in the csproj because the Win32 file-properties
    /// dialog (and some installer pipelines) prefer the ASCII form.
    /// </summary>
    public string CopyrightDisplay =>
        _updateService.Copyright.Replace("Copyright (c)", "©", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private string _latestReleaseUrl = string.Empty;

    [ObservableProperty]
    private bool _isCheckingForUpdate;

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;

        _monitorService = new MonitorService();
        _audioDeviceService = new AudioDeviceService();
        _sessionService = new AudioSessionService();
        _routerService = new AudioRouterService();
        _settingsService = new SettingsService();
        _updateService = new UpdateService();
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
    /// null — the dropdown reflects reality rather than showing a ghost — but
    /// the desired ID stays on each monitor VM, so re-plugging the device
    /// restores the mapping on the next refresh. Must be called on the UI thread.
    /// </summary>
    private void RefreshAudioDevicesPreservingSelections()
    {
        // Snapshot the user's intent (DesiredAudioDeviceId), not the live
        // SelectedAudioDevice — otherwise an unplug-then-replug cycle erases
        // the mapping: the unplug refresh nulls SelectedAudioDevice, and the
        // replug refresh would then snapshot string.Empty and have nothing to
        // restore. Snapshotting intent makes the round-trip lossless.
        var desiredByMonitor = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.DesiredAudioDeviceId);

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
                if (!desiredByMonitor.TryGetValue(monitorVm.Monitor.DeviceName, out var desiredId))
                    continue;

                // Match by Id (not reference) — AudioDeviceInfo is rebuilt each
                // refresh. If the device is gone, FirstOrDefault returns null;
                // the OnSelectedAudioDeviceChanged guard keeps DesiredAudioDeviceId
                // intact in that case so a future refresh can still resolve it.
                monitorVm.SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == desiredId);
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

        // Re-resolve names on existing pins (their device IDs are stable, but
        // the friendly-name string for an unplugged device flips between
        // "(Device not connected)" and the real name on hot-plug).
        RefreshAppOverrideDeviceNames();

        // Re-sync sliders in case a re-plugged device has a different volume
        // than the one we last remembered.
        RefreshAssignedVolumes();
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
                    // Set DesiredAudioDeviceId unconditionally so a hot-plug
                    // later in the session can resolve a device that was missing
                    // at app start. SelectedAudioDevice only gets set when the
                    // device is actually present right now.
                    monitorVm.DesiredAudioDeviceId = mapping.AudioDeviceId;
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

            // Rebuild the per-app pin list from disk. Resolve device names against
            // the current device list — pins whose device is currently unplugged
            // get a placeholder name but keep their ID, so they reactivate cleanly
            // when the device returns.
            //
            // Collapse duplicate keys case-insensitively on the way in. System.Text.Json
            // deserializes into a Dictionary with the default (ordinal) comparer, so a
            // hand-edited settings.json containing both "chrome" and "Chrome" would load
            // without complaint — but PushOverridesToEngine() / SaveSettings() build an
            // OrdinalIgnoreCase dict downstream and would throw on the collision,
            // aborting startup. Normalize here; last key wins, consistent with a
            // re-save of what we end up with.
            AppOverrides.Clear();
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in settings.AppOverrides)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;
                normalized[kvp.Key.Trim()] = kvp.Value;
            }
            foreach (var (processName, deviceId) in normalized)
            {
                var deviceName = AudioDevices.FirstOrDefault(d => d.Id == deviceId)?.FriendlyName
                                 ?? "(Device not connected)";
                AppOverrides.Add(new AppOverrideViewModel
                {
                    ProcessName = processName,
                    AudioDeviceId = deviceId,
                    AudioDeviceName = deviceName
                });
            }

            RefreshAssignedVolumes();

            PushMappingsToEngine();
            PushOverridesToEngine();
            IsRoutingEnabled = settings.IsEnabled;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void OnMappingChanged()
    {
        // During programmatic refreshes (LoadSettings, hot-plug refresh, Refresh
        // command) selections are assigned in a loop. Running the full push-mappings
        // + volume-read fanout for each one would do O(N²) NAudio COM calls on the
        // UI thread and fire N half-built routing pushes. Each programmatic caller
        // is responsible for invoking PushMappingsToEngine + RefreshAssignedVolumes
        // itself, once, after its loop finishes.
        if (_isLoading) return;

        PushMappingsToEngine();
        SaveSettings();
        RefreshAssignedVolumes();
    }

    /// <summary>
    /// Reads the current master volume from each monitor's assigned audio device
    /// and syncs it to the monitor's <c>Volume</c> slider. Runs on the UI thread;
    /// each call makes one NAudio COM round-trip per assigned device, so callers
    /// should run it once after a batch of selection changes rather than per-change.
    /// </summary>
    private void RefreshAssignedVolumes()
    {
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

    private void PushOverridesToEngine()
    {
        var dict = AppOverrides.ToDictionary(
            o => o.ProcessName,
            o => o.AudioDeviceId,
            StringComparer.OrdinalIgnoreCase);
        _routingEngine.UpdateAppOverrides(dict);
    }

    /// <summary>
    /// Pin a process to a specific audio device. Replaces any existing pin for
    /// the same process. Persists immediately and pushes to the engine so the
    /// new pin takes effect on the next routing pass (typically &lt;200ms).
    /// </summary>
    /// <param name="icon">
    /// Optional app icon to attach to the pin. Callers that already have the
    /// icon (the right-click flow pins from a live session that's been through
    /// <c>GetProcessIcon</c>) pass it through so the Pinned page shows it
    /// immediately rather than waiting for the next sessions update.
    /// </param>
    public void SetAppOverride(string processName, string deviceId, ImageSource? icon = null)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(deviceId))
            return;

        var deviceName = AudioDevices.FirstOrDefault(d => d.Id == deviceId)?.FriendlyName ?? "(Unknown device)";

        var existing = AppOverrides.FirstOrDefault(
            o => string.Equals(o.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.AudioDeviceId = deviceId;
            existing.AudioDeviceName = deviceName;
            // Only overwrite with a non-null incoming icon — re-pinning from a
            // session preserves the icon we already had if the caller passed null.
            if (icon != null) existing.Icon = icon;
        }
        else
        {
            AppOverrides.Add(new AppOverrideViewModel
            {
                ProcessName = processName,
                AudioDeviceId = deviceId,
                AudioDeviceName = deviceName,
                Icon = icon
            });
        }

        PushOverridesToEngine();
        SaveSettings();
    }

    /// <summary>
    /// Remove the pin for <paramref name="processName"/>. No-op if no pin exists.
    /// The session falls back to the monitor mapping on the next routing pass.
    /// </summary>
    public void RemoveAppOverride(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var existing = AppOverrides.FirstOrDefault(
            o => string.Equals(o.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        AppOverrides.Remove(existing);
        PushOverridesToEngine();
        SaveSettings();
    }

    /// <summary>
    /// Re-resolve the friendly name of every pin against the current device list.
    /// Called after a hot-plug refresh so a previously-unplugged pinned device
    /// starts showing its real name (and vice versa).
    /// </summary>
    private void RefreshAppOverrideDeviceNames()
    {
        foreach (var ovr in AppOverrides)
        {
            var name = AudioDevices.FirstOrDefault(d => d.Id == ovr.AudioDeviceId)?.FriendlyName;
            ovr.AudioDeviceName = name ?? "(Device not connected)";
        }
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
            // Persist user intent (DesiredAudioDeviceId), not the live SelectedAudioDevice.
            // Otherwise a Save triggered while a device is temporarily unplugged
            // (e.g. the user toggles AutoStartWithWindows during a hot-unplug)
            // would write that mapping out of the file permanently.
            Mappings = Monitors
                .Where(m => !string.IsNullOrEmpty(m.DesiredAudioDeviceId))
                .Select(m => new MonitorAudioMapping
                {
                    MonitorDeviceName = m.Monitor.DeviceName,
                    AudioDeviceId = m.DesiredAudioDeviceId
                })
                .ToList(),
            // Pins survive device unplug for the same reason mappings do — we
            // store the device ID, not its present-or-not state.
            AppOverrides = AppOverrides.ToDictionary(
                o => o.ProcessName,
                o => o.AudioDeviceId,
                StringComparer.OrdinalIgnoreCase)
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

    // ── About page: check for updates ────────────────────────────────────
    //
    // User clicks the button → we hit the GitHub Releases API, compare tags,
    // and write a one-line result into UpdateStatus. LatestReleaseUrl is set
    // only when an update is available; the XAML uses it to decide whether to
    // render the "Download" hyperlink. IsCheckingForUpdate gates the button so
    // a double-click can't fire two concurrent probes.

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdate) return;

        IsCheckingForUpdate = true;
        UpdateStatus = "Checking…";
        LatestReleaseUrl = string.Empty;

        try
        {
            var result = await _updateService.CheckForUpdateAsync();
            switch (result)
            {
                case UpdateCheckResult.UpToDate upToDate:
                    UpdateStatus = $"You're on the latest version (v{upToDate.CurrentVersion}).";
                    break;
                case UpdateCheckResult.UpdateAvailable update:
                    UpdateStatus = $"Version {update.LatestVersion} is available.";
                    LatestReleaseUrl = update.ReleaseUrl;
                    break;
                case UpdateCheckResult.NetworkError net:
                    UpdateStatus = $"Couldn't reach GitHub: {net.Message}";
                    break;
                case UpdateCheckResult.Failed fail:
                    UpdateStatus = $"Update check failed: {fail.Message}";
                    break;
            }
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        var wasRunning = IsRoutingEnabled;
        if (wasRunning)
            _routingEngine.Stop();

        // Snapshot intent (DesiredAudioDeviceId) rather than current resolved
        // selection — a missing device's mapping should survive a manual
        // refresh the same way it survives a hot-plug refresh.
        var desiredByMonitor = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.DesiredAudioDeviceId);

        LoadDevicesAndMonitors();

        _isLoading = true;
        try
        {
            foreach (var monitorVm in Monitors)
            {
                if (!desiredByMonitor.TryGetValue(monitorVm.Monitor.DeviceName, out var desiredId))
                    continue;

                // Re-seed the desired ID first — LoadDevicesAndMonitors built
                // brand-new MonitorMappingViewModel instances with empty defaults.
                monitorVm.DesiredAudioDeviceId = desiredId;
                monitorVm.SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == desiredId);
            }

            // OnMappingChanged is suppressed under _isLoading, so read volumes
            // explicitly — sliders would otherwise show their default of 100.
            RefreshAssignedVolumes();
        }
        finally
        {
            _isLoading = false;
        }

        // Pin device names are derived from AudioDevices — refresh them after
        // the device list rebuild so the Pinned page reflects the new state.
        RefreshAppOverrideDeviceNames();

        if (wasRunning)
        {
            PushMappingsToEngine();
            PushOverridesToEngine();
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
                    Icon = item.Icon,
                    IsOverridden = item.Session.IsOverridden
                });
            }

            // Back-fill icons on pins loaded from disk: when a pinned app first
            // starts playing audio we now have a PID to extract its icon from,
            // so the Pinned page switches from the pin-glyph fallback to the real
            // app icon. Matches are case-insensitive; first live session wins.
            foreach (var ovr in AppOverrides)
            {
                if (ovr.Icon != null) continue;
                var match = sessionData.FirstOrDefault(s =>
                    string.Equals(s.Session.ProcessName, ovr.ProcessName,
                                  StringComparison.OrdinalIgnoreCase)
                    && s.Icon != null);
                if (match != null)
                    ovr.Icon = match.Icon;
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
