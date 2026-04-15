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

    /// <summary>
    /// True when this session's audio device was chosen by an explicit per-app pin
    /// (see <c>AppSettings.AppOverrides</c>) rather than the monitor → device mapping.
    /// Used by the UI to render a pin indicator.
    /// </summary>
    public bool IsOverridden { get; set; }
}
