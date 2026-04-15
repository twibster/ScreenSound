using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ScreenSound.Services;

/// <summary>
/// Subscribes to Core Audio endpoint notifications so the UI and routing engine
/// find out the instant the user plugs in a USB DAC, removes a headset, or flips
/// the system default — instead of discovering it on the next heartbeat (or not
/// at all, while the user stares at a dropdown full of ghost devices).
///
/// <para>
/// Threading: <see cref="IMMNotificationClient"/> callbacks fire on an arbitrary
/// MTA thread owned by the Core Audio RPC subsystem. This class does NOT marshal
/// to any particular thread — consumers own that responsibility (WPF
/// <c>ObservableCollection</c>s must be mutated on the dispatcher, etc.).
/// </para>
///
/// <para>
/// A single physical action (plugging a USB DAC) often produces a burst of events
/// — OnDeviceAdded + OnDeviceStateChanged + OnDefaultDeviceChanged in rapid
/// succession. Consumers should debounce before running expensive work.
/// </para>
/// </summary>
public sealed class AudioDeviceNotifier : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private bool _registered;
    // Volatile because it's written on the thread calling Dispose() and read
    // on arbitrary COM RPC threads servicing in-flight callbacks. Without the
    // barrier a late callback could miss the write and invoke a subscriber
    // after IDisposable says we're done.
    private volatile bool _disposed;

    /// <summary>
    /// Raised when the set of available render endpoints changes
    /// (add / remove / state change). Fires on a COM RPC thread.
    /// </summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// Raised when the system default render device changes. Fires on a COM RPC
    /// thread. Filtered to render-flow only; capture-flow changes are ignored.
    /// </summary>
    public event Action? DefaultDeviceChanged;

    public AudioDeviceNotifier()
    {
        _enumerator = new MMDeviceEnumerator();
    }

    public void Start()
    {
        if (_registered) return;
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    public void Stop()
    {
        if (!_registered) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { /* already gone */ }
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        // Drop all subscribers so even if a COM callback is still in flight and
        // misses the _disposed check, there's nothing left to invoke. Field-
        // like events are rewritable inside the declaring class.
        DevicesChanged = null;
        DefaultDeviceChanged = null;

        try { _enumerator.Dispose(); } catch { }
    }

    // --- IMMNotificationClient ---
    // These run on a COM RPC thread. They must not throw — exceptions propagating
    // back into the OS callback pipeline are undefined behavior. We swallow.
    // Each entry checks _disposed because UnregisterEndpointNotificationCallback
    // does NOT block in-flight callbacks; one can land between Dispose() starting
    // and Stop() completing.

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (_disposed) return;
        // Covers disable/enable from Sound Settings and state flips when a device
        // becomes unavailable without being "removed" from the endpoint registry.
        if (!IsRenderEndpoint(deviceId)) return;
        SafeRaise(DevicesChanged);
    }

    public void OnDeviceAdded(string deviceId)
    {
        if (_disposed) return;
        if (!IsRenderEndpoint(deviceId)) return;
        SafeRaise(DevicesChanged);
    }

    public void OnDeviceRemoved(string deviceId)
    {
        if (_disposed) return;
        // GetDevice typically fails here because the OS has already de-registered
        // the endpoint by the time this callback fires. IsRenderEndpoint
        // conservatively returns true on failure so we still refresh on real
        // render-device removals — the cost is also refreshing on capture-device
        // removals, which is acceptable (one cheap rebuild vs. a stale dropdown).
        if (!IsRenderEndpoint(deviceId)) return;
        SafeRaise(DevicesChanged);
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (_disposed) return;
        // Only care about render (output) changes; ignore capture-device default flips.
        if (flow != DataFlow.Render) return;
        SafeRaise(DefaultDeviceChanged);
    }

    /// <summary>
    /// Fires constantly (volume changes, format changes, every property blob
    /// mutation) — far too chatty to hook to a refresh. Intentionally a no-op.
    /// </summary>
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    /// <summary>
    /// Resolves <paramref name="deviceId"/> and returns true if it's a render
    /// endpoint. Returns true on lookup failure (device already gone, RPC error)
    /// — the caller treats uncertainty as "render" so we don't silently drop a
    /// real render-device change. The unavoidable trade-off is that we also
    /// refresh on capture-device removals, since the endpoint is no longer
    /// queryable when OnDeviceRemoved fires.
    /// </summary>
    private bool IsRenderEndpoint(string deviceId)
    {
        try
        {
            using var device = _enumerator.GetDevice(deviceId);
            return device.DataFlow == DataFlow.Render;
        }
        catch
        {
            return true;
        }
    }

    private static void SafeRaise(Action? handler)
    {
        try { handler?.Invoke(); } catch { /* consumer bug must not crash the OS hook */ }
    }
}
