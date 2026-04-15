using ScreenSound.Interop;
using ScreenSound.Models;
using Microsoft.Win32;
using System.Collections.Concurrent;

namespace ScreenSound.Services;

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

    // Per-app pins: process name (case-insensitive, no .exe) → audio device ID.
    // Checked before the monitor mapping so a pinned app stays on its chosen device
    // regardless of which monitor its window is on. volatile + replace-whole-dict
    // means the work loop sees a coherent snapshot without locks.
    private volatile Dictionary<string, string> _appOverrides =
        new(StringComparer.OrdinalIgnoreCase);

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
    /// Replaces the per-app override pins. Triggers an immediate routing pass so
    /// a newly-added pin takes effect without the user having to interact with the
    /// app, and a removed pin allows the monitor mapping to take over right away.
    /// </summary>
    public void UpdateAppOverrides(IDictionary<string, string> overrides)
    {
        _appOverrides = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        Trigger();
    }

    /// <summary>
    /// Starts the engine. Must be called from a thread with a message pump
    /// (the WPF UI thread) because <see cref="WinEventHook"/> needs one to
    /// receive out-of-context callbacks.
    /// </summary>
    /// <remarks>
    /// Construction builds everything into locals first so a failure partway
    /// through (e.g. <c>SetWinEventHook</c> returning NULL, or the audio
    /// service being unavailable) can unwind cleanly without leaving hooks
    /// registered in the OS or half-initialized fields hanging around for the
    /// next <c>Start()</c> call to overwrite.
    /// </remarks>
    public void Start()
    {
        if (IsRunning) return;

        var cts = new CancellationTokenSource();
        var signal = new SemaphoreSlim(initialCount: 0);

        // All signal sources release the same semaphore. Captured as a local
        // closure so the callbacks don't depend on the eventual field value
        // (which Stop() nulls out — we don't want callbacks firing on a
        // replaced or nulled field in the middle of a restart).
        Action release = () => { try { signal.Release(); } catch { /* disposed */ } };

        WinEventHook? moveResizeHook = null;
        WinEventHook? foregroundHook = null;
        AudioSessionNotifier? sessionNotifier = null;

        try
        {
            // Window-move / snap / maximize-restore end — THE "user dragged this to another monitor" signal.
            moveResizeHook = new WinEventHook(
                WinEventHook.EVENT_SYSTEM_MOVESIZEEND,
                WinEventHook.EVENT_SYSTEM_MOVESIZEEND,
                release);

            // Foreground change — covers alt-tab and apps that move their own windows programmatically.
            // Debouncing in the work loop keeps this cheap even though FOREGROUND can fire frequently.
            foregroundHook = new WinEventHook(
                WinEventHook.EVENT_SYSTEM_FOREGROUND,
                WinEventHook.EVENT_SYSTEM_FOREGROUND,
                release);

            sessionNotifier = new AudioSessionNotifier(release);
            sessionNotifier.Start();
        }
        catch
        {
            // Roll back anything that was created before the failure.
            sessionNotifier?.Dispose();
            foregroundHook?.Dispose();
            moveResizeHook?.Dispose();
            signal.Dispose();
            cts.Dispose();
            throw;
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Pass signal + token by value so the loop doesn't read instance fields
        // that Stop() might mutate concurrently.
        var workTask = Task.Run(() => WorkLoop(signal, cts.Token));

        // Commit to instance state only after everything succeeded.
        _cts = cts;
        _signal = signal;
        _moveResizeHook = moveResizeHook;
        _foregroundHook = foregroundHook;
        _sessionNotifier = sessionNotifier;
        _workTask = workTask;

        // Kick off one immediate pass so the UI populates without waiting for an event.
        release();
    }

    /// <summary>
    /// Stops the engine and waits briefly for the background loop to exit
    /// before disposing shared state. Safe to call repeatedly; safe to call
    /// concurrently with a subsequent <see cref="Start"/> (the old loop observes
    /// cancellation and exits against its own captured semaphore, not the new one).
    /// </summary>
    public void Stop()
    {
        // Snapshot current state then null the fields FIRST, so any concurrent
        // Trigger()/Start() sees "no engine running" and operates on its own
        // fresh resources rather than racing on the ones we're about to dispose.
        var cts = _cts;
        var signal = _signal;
        var workTask = _workTask;
        var moveResizeHook = _moveResizeHook;
        var foregroundHook = _foregroundHook;
        var sessionNotifier = _sessionNotifier;

        _cts = null;
        _signal = null;
        _workTask = null;
        _moveResizeHook = null;
        _foregroundHook = null;
        _sessionNotifier = null;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // Unhook OS / NAudio callbacks before we tear down the semaphore they
        // write to — prevents a late callback hitting a disposed semaphore.
        foregroundHook?.Dispose();
        moveResizeHook?.Dispose();
        sessionNotifier?.Dispose();

        try { cts?.Cancel(); } catch { /* disposed */ }
        // Wake the loop so it observes cancellation immediately instead of
        // sitting in WaitAsync for the full heartbeat interval.
        try { signal?.Release(); } catch { /* disposed */ }

        // Wait for the loop to actually exit before disposing what it's reading.
        // Bounded: if something wedges, we log and move on rather than hang forever.
        if (workTask != null)
        {
            try
            {
                if (!workTask.Wait(TimeSpan.FromSeconds(3)))
                    System.Diagnostics.Debug.WriteLine("RoutingEngine: work loop did not exit within 3s");
            }
            catch { /* task faulted — we own cleanup anyway */ }
        }

        signal?.Dispose();
        cts?.Dispose();

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

    // Takes signal + token as parameters (rather than reading the fields) so that
    // Stop() can safely null the fields without the loop dereferencing them
    // concurrently. The loop always operates on the exact semaphore/CTS it was
    // launched with — any overlap with a subsequent Start() is harmless.
    private async Task WorkLoop(SemaphoreSlim signal, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for a signal OR the heartbeat timeout, whichever comes first.
                bool signaled = await signal.WaitAsync(HeartbeatInterval, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                if (signaled)
                {
                    // Debounce: absorb any follow-up signals that arrive in the next DebounceMs
                    // so a burst of events (drag end + foreground change + session created) runs
                    // the cycle exactly once.
                    await Task.Delay(DebounceMs, ct).ConfigureAwait(false);
                    while (await signal.WaitAsync(0, ct).ConfigureAwait(false)) { }
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
        var overrides = _appOverrides;

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
                // Default to "not overridden" — flipped on below if a pin matches.
                // Reset every cycle so removing a pin clears the indicator on the
                // very next pass even if the session is still alive.
                session.IsOverridden = false;

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

                // Resolve the target device. Per-app pins win over monitor mappings —
                // a pinned app stays on its chosen device regardless of monitor.
                string? targetDeviceId = null;
                if (!string.IsNullOrEmpty(session.ProcessName) &&
                    overrides.TryGetValue(session.ProcessName, out var pinnedDeviceId) &&
                    !string.IsNullOrEmpty(pinnedDeviceId))
                {
                    targetDeviceId = pinnedDeviceId;
                    session.IsOverridden = true;
                }
                else if (monitor != null && mappings.Count > 0 &&
                         mappings.TryGetValue(monitor.DeviceName, out var monitorTarget) &&
                         !string.IsNullOrEmpty(monitorTarget))
                {
                    targetDeviceId = monitorTarget;
                }

                if (string.IsNullOrEmpty(targetDeviceId))
                {
                    // No rule applies right now. If we previously routed this PID
                    // (pin removed, monitor mapping cleared, window moved to a
                    // monitor without a mapping, …), undo our prior per-app
                    // policy so Windows falls back to the system default. Without
                    // this, the stale override set by the pin would persist after
                    // unpinning and audio would stay stuck on the old device.
                    // TryRemove-first means we only clear once per transition —
                    // subsequent heartbeat cycles find no cache entry and skip.
                    if (_lastRoutedDevice.TryRemove(session.ProcessId, out _))
                    {
                        try { _routerService.TryClearProcessRouting(session.ProcessId); }
                        catch { /* process may have exited */ }
                    }
                    continue;
                }

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
        // Stop() disposes CTS + signal itself now, so this is just a one-liner.
        Stop();
    }
}
