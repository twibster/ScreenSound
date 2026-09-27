using System.Runtime.InteropServices;
namespace ScreenSound.Interop;

// Replace only the OS boundary; tests execute the production MonitorService.
public static class NativeMethods
{
    public const uint MONITOR_DEFAULTTONEAREST=2, EDD_GET_DEVICE_INTERFACE_NAME=1;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS=9;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length,flags,showCmd; public POINT ptMinPosition,ptMaxPosition; public RECT rcNormalPosition; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct MONITORINFOEX { public int cbSize; public RECT rcMonitor,rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string DeviceString; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESSENTRY32 { public uint dwSize,th32ProcessID,th32ParentProcessID; }
    public delegate bool MonitorEnumDelegate(nint monitor,nint dc,ref RECT rect,nint data);
    public delegate bool EnumWindowsDelegate(nint window,nint data);
    public class Window { public uint Pid; public bool Visible=true,Minimized; public RECT Normal; public string Monitor="MAIN"; }
    public static Dictionary<nint,Window> Windows = new();
    public static bool SecondaryConnected=true;
    public static void Reset() { Windows.Clear(); SecondaryConnected=true; }
    public static RECT Rect(int x,int y,int w,int h) => new(){Left=x,Top=y,Right=x+w,Bottom=y+h};
    public static Window AddWindow(int handle,uint pid,int x,int y,int w,int h,string monitor)
    { var result=new Window{Pid=pid,Normal=Rect(x,y,w,h),Monitor=monitor}; Windows[(nint)handle]=result; return result; }
    public static bool EnumDisplayMonitors(nint dc,nint clip,MonitorEnumDelegate action,nint data)
    { var a=Rect(0,0,2048,1152); action(1,0,ref a,data); if(SecondaryConnected){var b=Rect(-1920,0,1920,1080);action(2,0,ref b,data);} return true; }
    public static bool GetMonitorInfo(nint monitor,ref MONITORINFOEX info)
    { info.szDevice=monitor==1?"MAIN":"SECONDARY";info.dwFlags=monitor==1?1u:0u;info.rcMonitor=monitor==1?Rect(0,0,2048,1152):Rect(-1920,0,1920,1080);return true; }
    public static bool EnumDisplayDevices(string? name,uint device,ref DISPLAY_DEVICE info,uint flags) { info.DeviceString=name??"";return true; }
    public static nint MonitorFromWindow(nint hwnd,uint flags) => Windows[hwnd].Monitor=="SECONDARY" && SecondaryConnected?2:1;
    public static bool EnumWindows(EnumWindowsDelegate action,nint data) { foreach(var hwnd in Windows.Keys.ToArray()) if(!action(hwnd,data))break;return true; }
    public static uint GetWindowThreadProcessId(nint hwnd,out uint pid) { pid=Windows.TryGetValue(hwnd,out var w)?w.Pid:0;return 1; }
    public static bool IsWindow(nint hwnd) => Windows.ContainsKey(hwnd);
    public static bool IsWindowVisible(nint hwnd) => Windows.TryGetValue(hwnd,out var w) && w.Visible;
    public static bool IsIconic(nint hwnd) => Windows.TryGetValue(hwnd,out var w) && w.Minimized;
    public static bool GetWindowRect(nint hwnd,out RECT rect) { var w=Windows[hwnd];rect=w.Minimized?Rect(-32000,-32000,160,28):w.Normal;return true; }
    public static bool GetWindowPlacement(nint hwnd,ref WINDOWPLACEMENT placement) { placement.rcNormalPosition=Windows[hwnd].Normal;return true; }
    public static int GetWindowTextLength(nint hwnd) => 10;
    public static int DwmGetWindowAttribute(nint hwnd,int attr,out RECT rect,int length) { GetWindowRect(hwnd,out rect);return 0; }
    public static nint CreateToolhelp32Snapshot(uint flags,uint pid) => 0;
    public static bool Process32First(nint handle,ref PROCESSENTRY32 entry) => false;
    public static bool Process32Next(nint handle,ref PROCESSENTRY32 entry) => false;
    public static bool CloseHandle(nint handle) => true;
}
