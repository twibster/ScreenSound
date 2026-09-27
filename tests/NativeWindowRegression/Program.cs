using ScreenSound.Services;
using ScreenSound.Interop;
using Forms=System.Windows.Forms;

internal class ProbeForm : Forms.Form
{
    protected override bool ShowWithoutActivation => true;
}

internal static class Program
{
    [STAThread]
    static int Main()
    {
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var service = new MonitorService();
        var monitors = service.GetMonitors();
        int passed=0,failed=0;
        if(monitors.Count<2) { Console.WriteLine("NEEDS_TWO_MONITORS"); return 2; }
        using var window = new ProbeForm {
            Text="ScreenSound automated regression fixture",
            StartPosition=Forms.FormStartPosition.Manual,
            ShowInTaskbar=false,Opacity=0,
            Size=new System.Drawing.Size(600,400)
        };
        void Pump() { for(int i=0;i<5;i++) { Forms.Application.DoEvents(); Thread.Sleep(20); } }
        void Check(string name,string expected,MonitorService? other=null)
        {
            var actual=(other??service).GetMonitorForProcess((uint)Environment.ProcessId)?.DeviceName;
            if(actual==expected) {passed++;Console.WriteLine($"PASS {name}: {actual}");}
            else {failed++;Console.WriteLine($"FAIL {name}: expected={expected}, actual={actual??"NONE"}");}
        }
        foreach(var monitor in monitors)
        {
            window.WindowState=Forms.FormWindowState.Normal;
            window.Location=new System.Drawing.Point(monitor.Left+150,monitor.Top+150);
            window.Show();Pump();
            Check("visible player",monitor.DeviceName);
            window.WindowState=Forms.FormWindowState.Minimized;Pump();
            Check("minimized player",monitor.DeviceName);
            Check("startup while minimized",monitor.DeviceName,new MonitorService());
            window.Hide();Pump();
            Check("hidden player",monitor.DeviceName);
            var other=monitors.First(x=>x.DeviceName!=monitor.DeviceName);
            using(var lyrics=new ProbeForm {
                Text="Regression lyrics fixture",StartPosition=Forms.FormStartPosition.Manual,
                Location=new System.Drawing.Point(other.Left+100,other.Top+100),
                Size=new System.Drawing.Size(400,100),ShowInTaskbar=false,Opacity=0 })
            {
                lyrics.Show();Pump();
                Check("hidden player with lyrics on other display",monitor.DeviceName);
                lyrics.Hide();
            }
            window.WindowState=Forms.FormWindowState.Normal;
            window.Show();Pump();
            Check("restored player",monitor.DeviceName);
        }
        window.Hide();
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed==0?0:1;
    }
}
