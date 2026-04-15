using AudioMonitorRouter.Models;
using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace AudioMonitorRouter.Services;

public class AudioSessionService
{
    public List<AudioSessionInfo> GetActiveSessions()
    {
        var sessions = new List<AudioSessionInfo>();
        var seen = new HashSet<uint>();

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessionManager = device.AudioSessionManager;
                var sessionEnumerator = sessionManager.Sessions;

                for (int i = 0; i < sessionEnumerator.Count; i++)
                {
                    var session = sessionEnumerator[i];
                    var pid = (uint)session.GetProcessID;

                    // Skip system sounds (PID 0) and duplicates
                    if (pid == 0 || !seen.Add(pid))
                        continue;

                    string processName;
                    try
                    {
                        var process = Process.GetProcessById((int)pid);
                        processName = process.ProcessName;
                    }
                    catch
                    {
                        processName = "Unknown";
                    }

                    sessions.Add(new AudioSessionInfo
                    {
                        ProcessId = pid,
                        ProcessName = processName,
                        DisplayName = string.IsNullOrEmpty(session.DisplayName) ? processName : session.DisplayName,
                        AudioDeviceId = device.ID,
                        AudioDeviceName = device.FriendlyName
                    });
                }
            }
            catch
            {
                // Device may become unavailable during enumeration
            }
        }

        return sessions;
    }
}
