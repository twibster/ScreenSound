namespace ScreenSound.Models;

public class AppSettings
{
    public List<MonitorAudioMapping> Mappings { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
    public bool AutoStartWithWindows { get; set; }
    public bool MinimizeOnClose { get; set; }
    public bool StartMinimized { get; set; }
    public string ThemeMode { get; set; } = "System"; // "System", "Light", "Dark"

    /// <summary>
    /// Per-app override pins: process name (without .exe, case-insensitive) → audio device ID.
    /// When present, an entry takes precedence over the monitor → device mapping for that process,
    /// regardless of which monitor its window happens to be on. Survives device hot-plug and
    /// window moves; only cleared when the user explicitly removes the pin.
    /// </summary>
    public Dictionary<string, string> AppOverrides { get; set; } = new();
}

public class MonitorAudioMapping
{
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty;
}
