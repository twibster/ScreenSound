using ScreenSound.Interop;

namespace ScreenSound.Services;

public class AudioRouterService : IDisposable
{
    private AudioPolicyConfigClient? _client;
    private readonly object _lock = new();

    private AudioPolicyConfigClient GetClient()
    {
        if (_client == null)
        {
            lock (_lock)
            {
                _client ??= new AudioPolicyConfigClient();
            }
        }
        return _client;
    }

    public bool TryRouteProcessToDevice(uint processId, string deviceId)
    {
        try
        {
            GetClient().RouteProcessToDevice(processId, deviceId);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to route PID {processId} to device {deviceId}: {ex.Message}");
            return false;
        }
    }

    public bool TryClearProcessRouting(uint processId)
    {
        try
        {
            GetClient().ClearProcessRouting(processId);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to clear routing for PID {processId}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
