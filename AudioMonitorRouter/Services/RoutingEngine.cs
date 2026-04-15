using AudioMonitorRouter.Models;
using System.Collections.Concurrent;

namespace AudioMonitorRouter.Services;

public class RoutingEngine : IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly AudioSessionService _sessionService;
    private readonly AudioRouterService _routerService;

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    // Track last-routed device per PID to avoid redundant COM calls
    private readonly ConcurrentDictionary<uint, string> _lastRoutedDevice = new();

    // In-memory mappings pushed from the ViewModel — no disk reads in the hot loop
    private volatile Dictionary<string, string> _mappings = new();

    // Device name lookup cached per cycle
    private Dictionary<string, string> _deviceNameCache = new();

    public int PollingIntervalMs { get; set; } = 1000;

    public bool IsRunning => _pollingTask != null && !_pollingTask.IsCompleted;

    public event Action<List<AudioSessionInfo>>? SessionsUpdated;

    public RoutingEngine(
        MonitorService monitorService,
        AudioSessionService sessionService,
        AudioRouterService routerService)
    {
        _monitorService = monitorService;
        _sessionService = sessionService;
        _routerService = routerService;
    }

    public void UpdateMappings(List<MonitorAudioMapping> mappings)
    {
        _mappings = mappings.ToDictionary(m => m.MonitorDeviceName, m => m.AudioDeviceId);
    }

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _pollingTask = Task.Run(() => PollingLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _pollingTask = null;
        _lastRoutedDevice.Clear();
    }

    private async Task PollingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                PerformRoutingCycle();
                await Task.Delay(PollingIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Routing cycle error: {ex.Message}");
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }
    }

    private void PerformRoutingCycle()
    {
        var mappings = _mappings;

        var sessions = _sessionService.GetActiveSessions();

        // Refresh device name cache
        try
        {
            using var deviceService = new AudioDeviceService();
            _deviceNameCache = deviceService.GetOutputDevices().ToDictionary(d => d.Id, d => d.FriendlyName);
        }
        catch { /* keep previous cache */ }

        // Clean up stale PIDs
        var activePids = new HashSet<uint>(sessions.Select(s => s.ProcessId));
        foreach (var pid in _lastRoutedDevice.Keys)
        {
            if (!activePids.Contains(pid))
                _lastRoutedDevice.TryRemove(pid, out _);
        }

        foreach (var session in sessions)
        {
            try
            {
                // Always detect which monitor the process is on
                var monitor = _monitorService.GetMonitorForProcess(session.ProcessId);
                if (monitor != null)
                {
                    session.MonitorDeviceName = monitor.DeviceName;
                    session.MonitorFriendlyName = monitor.FriendlyName;
                }

                // Show current audio device name
                if (_deviceNameCache.TryGetValue(session.AudioDeviceId, out var currentDeviceName))
                {
                    session.AudioDeviceName = currentDeviceName;
                }

                // Only route if we have a mapping for this monitor
                if (monitor == null || mappings.Count == 0)
                    continue;

                if (!mappings.TryGetValue(monitor.DeviceName, out var targetDeviceId))
                    continue;

                if (string.IsNullOrEmpty(targetDeviceId))
                    continue;

                // Update display to show the target device
                if (_deviceNameCache.TryGetValue(targetDeviceId, out var targetDeviceName))
                {
                    session.AudioDeviceId = targetDeviceId;
                    session.AudioDeviceName = targetDeviceName;
                }

                // Skip if already routed to this device
                if (_lastRoutedDevice.TryGetValue(session.ProcessId, out var lastDevice) && lastDevice == targetDeviceId)
                    continue;

                if (_routerService.TryRouteProcessToDevice(session.ProcessId, targetDeviceId))
                {
                    _lastRoutedDevice[session.ProcessId] = targetDeviceId;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error routing PID {session.ProcessId}: {ex.Message}");
            }
        }

        SessionsUpdated?.Invoke(sessions);
    }

    /// <summary>
    /// Resets all routed processes back to their system default audio device.
    /// Call this when the app is closing so audio doesn't stay stuck on a non-default device.
    /// </summary>
    public void ResetAllRouting()
    {
        foreach (var pid in _lastRoutedDevice.Keys.ToList())
        {
            try
            {
                _routerService.TryClearProcessRouting(pid);
            }
            catch
            {
                // Process may have already exited
            }
        }
        _lastRoutedDevice.Clear();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
