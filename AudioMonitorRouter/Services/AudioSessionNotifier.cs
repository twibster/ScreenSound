using NAudio.CoreAudioApi;

namespace AudioMonitorRouter.Services;

/// <summary>
/// Subscribes to <see cref="AudioSessionManager.OnSessionCreated"/> across all
/// currently-active render endpoints so the routing engine can react the moment
/// a new app starts playing audio, instead of discovering it on the next poll.
///
/// MMDevice references are held for the lifetime of the subscription — disposing
/// the device cancels the underlying <c>IAudioSessionNotification</c> registration,
/// so we keep them alive until <see cref="Stop"/> or <see cref="Dispose"/>.
///
/// NOTE: this snapshot is captured once per <see cref="Start"/> call. Devices
/// plugged in after start won't fire events — that's handled by the routing
/// engine's heartbeat and (eventually) a device-change subscription.
/// </summary>
public sealed class AudioSessionNotifier : IDisposable
{
    private readonly Action _onNewSession;
    private readonly List<Subscription> _subscriptions = new();
    private MMDeviceEnumerator? _enumerator;

    public AudioSessionNotifier(Action onNewSession)
    {
        _onNewSession = onNewSession ?? throw new ArgumentNullException(nameof(onNewSession));
    }

    public void Start()
    {
        Stop();

        _enumerator = new MMDeviceEnumerator();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var manager = device.AudioSessionManager;
                AudioSessionManager.SessionCreatedDelegate handler = (_, _) =>
                {
                    try { _onNewSession(); }
                    catch { /* swallow — COM callback */ }
                };
                manager.OnSessionCreated += handler;
                _subscriptions.Add(new Subscription(device, manager, handler));
            }
            catch
            {
                // Device might have become unavailable between enumeration and subscription.
                try { device.Dispose(); } catch { }
            }
        }
    }

    public void Stop()
    {
        foreach (var sub in _subscriptions)
        {
            try { sub.Manager.OnSessionCreated -= sub.Handler; } catch { }
            try { sub.Device.Dispose(); } catch { }
        }
        _subscriptions.Clear();

        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }

    public void Dispose() => Stop();

    private sealed record Subscription(
        MMDevice Device,
        AudioSessionManager Manager,
        AudioSessionManager.SessionCreatedDelegate Handler);
}
