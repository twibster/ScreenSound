namespace AudioMonitorRouter.Models;

public class AppSettings
{
    public List<MonitorAudioMapping> Mappings { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
    public bool AutoStartWithWindows { get; set; }
    public bool MinimizeOnClose { get; set; }
    public bool StartMinimized { get; set; }
    public string ThemeMode { get; set; } = "System"; // "System", "Light", "Dark"
}

public class MonitorAudioMapping
{
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty;
}
