namespace AudioMonitorRouter.Models;

public class AudioSessionInfo
{
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string MonitorFriendlyName { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty;
    public string AudioDeviceName { get; set; } = string.Empty;
}
