using AudioMonitorRouter.Interop;
using AudioMonitorRouter.Models;
using Microsoft.Win32;
using System.Collections.Concurrent;

namespace AudioMonitorRouter.Services;

/// <summary>
/// Drives audio routing in response to signals:
///   * window moved / resized / foreground changed  (<see cref="WinEventHook"/>)
///   * new audio session created                    (<see cref="AudioSessionNotifier"/>)
///   * display configuration changed                (<see cref="SystemEvents.DisplaySettingsChanged"/>)
///   * periodic heartbeat                           (safety net every <see cref="HeartbeatInterval"/>)
///
/// All signals call <see cref="Trigger"/>, which releases a semaphore. A single
/// background task consumes the semaphore, debounces rapid bursts, and runs one
/// <see cref="PerformRoutingCycle"/> per batch — so dragging a window to a new
/// monitor results in exactly one routing pass ~<see cref="DebounceMs"/>ms after
/// the drag ends, rather than up to a full polling interval of latency.
///
/// Compared to the old 1000ms polling loop this reduces idle CPU wake-ups ~30x
/// (one every 30s vs every 1s) and drops worst-case routing latency from ~1s to
/// effectively the debounce window.
/// </summary>
public class RoutingEngine : IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly AudioSessionService _sessionService;
    private readonly AudioRouterService _routerService;

    // Coalesces bursts of events into a single routing cycle.
    private const int DebounceMs = 150;

    // Safety-net interval: also re-evaluates even if no events fire. Catches edge
    // cases (programmatic window moves, devices that don't raise session-created
    // events, missed hooks) without burning CPU.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _workTask;
    private SemaphoreSlim? _signal;

    private WinEventHook? _foregroundHook;
    private WinEventHook? _moveResizeHook;
    private AudioSessionNotifier? _sessionNotifier;

    // Track last-routed device per PID to avoid redundant COM calls
    private readonly ConcurrentDictionary<uint, string> _lastRoutedDevice = new();

    // In-memory mappings pushed from the ViewModel — no disk reads in the hot loop
    private volatile Dictionary<string, string> _mappings = new();

    // Device name lookup cached per cycle
    private Dictionary<string, string> _deviceNameCache = new();

    public bool IsRunning => _workTask != null && !_workTask.IsCompleted;

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
        // Mappings changed — re-evaluate immediately so already-running sessions
        // get routed without waiting for the next user action.
        Trigger();
    }

    /// <summary>
    /// Starts the engine. Must be called from a thread with a message pump
    /// (the WPF UI thread) because <see cref="WinEventHook"/> needs one to
    /// receive out-of-context callbacks.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _signal = new SemaphoreSlim(initialCount: 0);

        // Window-move / snap / maximize-restore end — the main "user dragged this to another monitor" signal.
        _moveResizeHook = new WinEventHook(
            WinEventHook.EVENT_SYSTEM_MOVESIZEEND,
            WinEventHook.EVENT_SYSTEM_MOVESIZEEND,
            Trigger);

        // Foreground change — covers alt-tab and apps that move their own windows programmatically.
        // Debouncing in the work loop keeps this cheap even though FOREGROUND can fire frequently.
        _foregroundHook = new WinEventHook(
            WinEventHook.EVENT_SYSTEM_FOREGROUND,
            WinEventHook.EVENT_SYSTEM_FOREGROUND,
            Trigger);

        _sessionNotifier = new AudioSessionNotifier(Trigger);
        _sessionNotifier.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _workTask = Task.Run(() => WorkLoop(_cts.Token));

        // Kick off one immediate pass so the UI populates without waiting for an event.
        Trigger();
    }

    public void Stop()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _foregroundHook?.Dispose();
        _foregroundHook = null;
        _moveResizeHook?.Dispose();
        _moveResizeHook = null;

        _sessionNotifier?.Dispose();
        _sessionNotifier = null;

        _cts?.Cancel();
        // Wake the loop so it can observe cancellation immediately.
        try { _signal?.Release(); } catch { }

        _workTask = null;
        _signal?.Dispose();
        _signal = null;
        _lastRoutedDevice.Clear();
    }

    /// <summary>
    /// Wakes the work loop. Safe to call from any thread and from native hook callbacks.
    /// Non-blocking; never throws.
    /// </summary>
    public void Trigger()
    {
        try { _signal?.Release(); } catch { /* disposed */ }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Monitor layout changed — invalidate our cached monitor list and re-route.
        _monitorService.InvalidateCache();
        Trigger();
    }

    private async Task WorkLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for a signal OR the heartbeat timeout, whichever comes first.
                bool signaled = await _signal!.WaitAsync(HeartbeatInterval, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                if (signaled)
                {
                    // Debounce: absorb any follow-up signals that arrive in the next DebounceMs
                    // so a burst of events (drag end + foreground change + session created) runs
                    // the cycle exactly once.
                    await Task.Delay(DebounceMs, ct).ConfigureAwait(false);
                    while (await _signal!.WaitAsync(0, ct).ConfigureAwait(false)) { }
                }

                PerformRoutingCycle();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Routing cycle error: {ex.Message}");
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
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
