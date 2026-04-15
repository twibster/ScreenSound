using System.Runtime.InteropServices;

namespace AudioMonitorRouter.Interop;

/// <summary>
/// Thin wrapper around SetWinEventHook for observing global window events
/// (move/resize end, foreground change) without subclassing windows.
///
/// IMPORTANT: with <c>WINEVENT_OUTOFCONTEXT</c> the OS marshals callbacks
/// to the thread that called <see cref="SetWinEventHook"/> via that thread's
/// message queue, so this type MUST be constructed on a thread that pumps
/// messages — in our app that's the WPF UI thread.
///
/// The callback itself just enqueues a signal; all real work happens on
/// the routing engine's background task, so the UI thread is only touched
/// for a few nanoseconds per event.
/// </summary>
public sealed class WinEventHook : IDisposable
{
    // Event constants from WinUser.h
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly WinEventProc _callback; // Rooted to prevent GC collecting the delegate the OS is holding
    private readonly Action _onEvent;
    private IntPtr _hookHandle;

    /// <summary>
    /// Installs a hook for the given event range. <paramref name="onEvent"/> is invoked on the
    /// thread that constructs this object (the OS dispatches via that thread's message queue).
    /// Keep the callback cheap — throwing back into the OS hook pipeline is undefined behavior.
    /// </summary>
    public WinEventHook(uint eventMin, uint eventMax, Action onEvent)
    {
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _callback = HookCallback;

        _hookHandle = SetWinEventHook(
            eventMin,
            eventMax,
            IntPtr.Zero,
            _callback,
            idProcess: 0,
            idThread: 0,
            dwFlags: WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException("SetWinEventHook returned NULL; hook installation failed.");
    }

    private void HookCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // Only care about top-level window events; ignore caret, menu, focus-on-control events etc.
        // idObject == OBJID_WINDOW (0) is the window itself.
        if (idObject != 0 || idChild != 0)
            return;

        try
        {
            _onEvent();
        }
        catch
        {
            // Must not propagate — the OS is holding a pointer to this callback.
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc pfnWinEventProc,
        uint idProcess, uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
