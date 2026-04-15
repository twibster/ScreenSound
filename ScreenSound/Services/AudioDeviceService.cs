using ScreenSound.Models;
using NAudio.CoreAudioApi;

namespace ScreenSound.Services;

public class AudioDeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;

    public AudioDeviceService()
    {
        _enumerator = new MMDeviceEnumerator();
    }

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        var defaultDevice = GetDefaultDeviceId();

        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(new AudioDeviceInfo
            {
                Id = device.ID,
                FriendlyName = device.FriendlyName,
                IsDefault = device.ID == defaultDevice
            });
        }

        return devices;
    }

    private string? GetDefaultDeviceId()
    {
        try
        {
            var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return defaultDevice?.ID;
        }
        catch
        {
            return null;
        }
    }

    public float GetDeviceVolume(string deviceId)
    {
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (device.ID == deviceId)
                    return device.AudioEndpointVolume.MasterVolumeLevelScalar;
            }
        }
        catch { }
        return 1.0f;
    }

    public void SetDeviceVolume(string deviceId, float volume)
    {
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (device.ID == deviceId)
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
                    return;
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _enumerator.Dispose();
    }
}
