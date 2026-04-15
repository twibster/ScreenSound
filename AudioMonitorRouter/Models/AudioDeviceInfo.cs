namespace AudioMonitorRouter.Models;

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}
