namespace AudioMonitorRouter.Models;

public class MonitorInfo
{
    public IntPtr Handle { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }

    public string DisplayText => IsPrimary
        ? $"{FriendlyName} (Primary) - {Width}x{Height}"
        : $"{FriendlyName} - {Width}x{Height}";
}
