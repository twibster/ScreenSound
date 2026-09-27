using ScreenSound.Services;
using ScreenSound.Interop;

int failed = 0, passed = 0;
void Check(string name, MonitorService service, string expected)
{
    var actual = service.GetMonitorForProcess(42)?.DeviceName ?? "NONE";
    if (actual != expected) { Console.WriteLine($"FAIL {name}: expected {expected}, got {actual}"); failed++; }
    else { Console.WriteLine($"PASS {name}"); passed++; }
}

NativeMethods.Reset();
var svc = new MonitorService();
var main = NativeMethods.AddWindow(10, 42, 200, 100, 1200, 800, "MAIN");
Check("normal main screen", svc, "MAIN");
main.Minimized = true;
Check("minimize preserves main screen", svc, "MAIN");
main.Minimized = false;
main.Normal = NativeMethods.Rect(-1600, 100, 1200, 800);
main.Monitor = "SECONDARY";
Check("restore then drag to secondary", svc, "SECONDARY");
main.Minimized = true;
Check("minimize preserves secondary screen", svc, "SECONDARY");
main.Visible = false;
main.Minimized = false;
Check("hide to tray preserves last screen", svc, "SECONDARY");
NativeMethods.AddWindow(11, 42, 100, 100, 600, 150, "MAIN");
Check("lyrics window does not steal hidden player route", svc, "SECONDARY");
main.Visible = true;
main.Normal = NativeMethods.Rect(200, 100, 1200, 800);
main.Monitor = "MAIN";
Check("restored player still follows dragging", svc, "MAIN");
NativeMethods.Windows.Remove((nint)10);
NativeMethods.Windows.Remove((nint)11);
NativeMethods.AddWindow(12, 42, -1600, 100, 1200, 800, "SECONDARY");
Check("destroyed cached window does not retain stale route", svc, "SECONDARY");

NativeMethods.Reset();
var minimizedStartup = new MonitorService();
var startupWindow = NativeMethods.AddWindow(20, 42, 200, 100, 1200, 800, "MAIN");
startupWindow.Minimized = true;
Check("startup with minimized player uses restored rectangle", minimizedStartup, "MAIN");

NativeMethods.Reset();
var disconnected = new MonitorService();
var unplugWindow = NativeMethods.AddWindow(30, 42, -1600, 100, 1200, 800, "SECONDARY");
Check("secondary before unplug", disconnected, "SECONDARY");
unplugWindow.Minimized = true;
NativeMethods.SecondaryConnected = false;
disconnected.InvalidateCache();
Check("unplug does not return a nonexistent monitor", disconnected, "MAIN");

NativeMethods.Reset();
var pruned = new MonitorService();
var hidden = NativeMethods.AddWindow(40, 42, -1600, 100, 1200, 800, "SECONDARY");
Check("remember process before cleanup", pruned, "SECONDARY");
hidden.Visible = false;
pruned.RetainProcesses(new HashSet<uint> { 42 });
Check("active process retains hidden window", pruned, "SECONDARY");
pruned.RetainProcesses(new HashSet<uint>());
Check("ended audio session forgets cached window", pruned, "NONE");

NativeMethods.Reset();
var reused = new MonitorService();
NativeMethods.AddWindow(50, 42, -1600, 100, 1200, 800, "SECONDARY");
Check("remember before handle reuse", reused, "SECONDARY");
NativeMethods.Windows[(nint)50].Pid = 99;
NativeMethods.Windows[(nint)50].Visible = false;
NativeMethods.AddWindow(51, 42, 200, 100, 1200, 800, "MAIN");
Check("reused handle from another process is rejected", reused, "MAIN");

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
