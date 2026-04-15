using AudioMonitorRouter.Interop;
using AudioMonitorRouter.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioMonitorRouter.Services;

public class MonitorService
{
    private List<MonitorInfo>? _cachedMonitors;
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(5);

    public List<MonitorInfo> GetMonitors(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedMonitors != null && DateTime.UtcNow - _lastRefresh < CacheExpiry)
            return _cachedMonitors;

        var monitors = new List<MonitorInfo>();
        int index = 1;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(info);

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var deviceName = info.szDevice?.TrimEnd('\0') ?? $"DISPLAY{index}";
                var friendlyName = GetMonitorFriendlyName(deviceName) ?? $"Monitor {index}";
                monitors.Add(new MonitorInfo
                {
                    Handle = hMonitor,
                    DeviceName = deviceName,
                    FriendlyName = friendlyName,
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary = (info.dwFlags & 1) != 0
                });
                index++;
            }
            return true;
        }, IntPtr.Zero);

        _cachedMonitors = monitors;
        _lastRefresh = DateTime.UtcNow;
        return monitors;
    }

    private static string? GetMonitorFriendlyName(string deviceName)
    {
        try
        {
            var dd = new NativeMethods.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);

            // Query the monitor attached to this display adapter
            if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
            {
                var name = dd.DeviceString?.Trim();
                if (!string.IsNullOrEmpty(name) && name != "Generic PnP Monitor")
                    return name;
            }
        }
        catch { }

        return null;
    }

    public void InvalidateCache()
    {
        _cachedMonitors = null;
    }

    public MonitorInfo? GetMonitorForProcess(uint processId)
    {
        // Try the exact PID first, then walk up the parent process tree.
        // Handles Electron apps (Discord, Slack, VS Code) where the audio
        // session runs in a child renderer process without a visible window.
        var pidsToTry = GetProcessAndAncestors(processId);

        IntPtr hwnd = IntPtr.Zero;
        foreach (var pid in pidsToTry)
        {
            hwnd = FindMainWindowForProcess(pid);
            if (hwnd != IntPtr.Zero)
                break;
        }

        if (hwnd == IntPtr.Zero) return null;

        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;

        // PRIMARY METHOD: Use the window's actual position to determine which
        // monitor it's on. This works correctly regardless of DPI scaling.
        var windowMonitor = FindMonitorByWindowPosition(hwnd, monitors);
        if (windowMonitor != null) return windowMonitor;

        // FALLBACK: Use MonitorFromWindow API + device name matching
        var hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero)
        {
            // Try handle match
            var match = monitors.FirstOrDefault(m => m.Handle == hMonitor);
            if (match != null) return match;

            // Try device name match
            var info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(info);
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var deviceName = info.szDevice?.TrimEnd('\0');
                match = monitors.FirstOrDefault(m => m.DeviceName == deviceName);
                if (match != null) return match;
            }
        }

        // Last resort: primary monitor
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
    }

    private MonitorInfo? FindMonitorByWindowPosition(IntPtr hwnd, List<MonitorInfo> monitors)
    {
        // Try DWM extended frame bounds first (gives physical/unscaled coordinates)
        int centerX, centerY;
        int hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT dwmRect, Marshal.SizeOf<NativeMethods.RECT>());

        if (hr == 0)
        {
            centerX = (dwmRect.Left + dwmRect.Right) / 2;
            centerY = (dwmRect.Top + dwmRect.Bottom) / 2;
        }
        else
        {
            // Fall back to GetWindowRect
            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
                return null;
            centerX = (windowRect.Left + windowRect.Right) / 2;
            centerY = (windowRect.Top + windowRect.Bottom) / 2;
        }

        // Find which monitor contains the window center point
        foreach (var monitor in monitors)
        {
            if (centerX >= monitor.Left && centerX < monitor.Left + monitor.Width &&
                centerY >= monitor.Top && centerY < monitor.Top + monitor.Height)
            {
                return monitor;
            }
        }

        // If no exact match (window might be between monitors), find closest
        MonitorInfo? closest = null;
        double closestDist = double.MaxValue;
        foreach (var monitor in monitors)
        {
            double monCenterX = monitor.Left + monitor.Width / 2.0;
            double monCenterY = monitor.Top + monitor.Height / 2.0;
            double dist = Math.Pow(centerX - monCenterX, 2) + Math.Pow(centerY - monCenterY, 2);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = monitor;
            }
        }

        return closest;
    }

    private static List<uint> GetProcessAndAncestors(uint processId)
    {
        var pids = new List<uint> { processId };
        var seen = new HashSet<uint> { processId };
        var currentPid = processId;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                var parentPid = GetParentProcessId(currentPid);
                if (parentPid == 0 || !seen.Add(parentPid))
                    break;
                pids.Add(parentPid);
                currentPid = parentPid;
            }
            catch
            {
                break;
            }
        }

        return pids;
    }

    private static uint GetParentProcessId(uint processId)
    {
        var handle = NativeMethods.CreateToolhelp32Snapshot(0x00000002, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return 0;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32();
            entry.dwSize = (uint)Marshal.SizeOf(entry);

            if (!NativeMethods.Process32First(handle, ref entry))
                return 0;

            do
            {
                if (entry.th32ProcessID == processId)
                    return entry.th32ParentProcessID;
            } while (NativeMethods.Process32Next(handle, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }

        return 0;
    }

    private IntPtr FindMainWindowForProcess(uint targetPid)
    {
        IntPtr bestWindow = IntPtr.Zero;
        int bestArea = 0;

        NativeMethods.EnumWindows((IntPtr hWnd, IntPtr lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != targetPid)
                return true;

            if (NativeMethods.GetWindowTextLength(hWnd) == 0)
                return true;

            NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;

            if (w < 50 || h < 50)
                return true;

            int area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                bestWindow = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }
}
