using System.Runtime.InteropServices;

namespace AudioMonitorRouter.Interop;

public enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

public enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

// Vtable layout verified against EarTrumpet (MIT) production source code.
// Both 21H2+ and downlevel variants share the same vtable layout, only GUID differs.
// With InterfaceIsIUnknown, .NET handles IUnknown slots 0-2 automatically.
// First declared method = vtable slot 3.

// Windows 21H2+ (build 22000+)
[ComImport]
[Guid("ab3d4648-e242-459f-b02f-541c70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigFactory21H2
{
    // IInspectable (slots 3-5)
    void _Pad_GetIids();
    void _Pad_GetRuntimeClassName();
    void _Pad_GetTrustLevel();

    // IAudioPolicyConfig methods we don't use (slots 6-24)
    void _Pad_add_CtxVolumeChange();
    void _Pad_remove_CtxVolumeChanged();
    void _Pad_add_RingerVibrateStateChanged();
    void _Pad_remove_RingerVibrateStateChanged();
    void _Pad_SetVolumeGroupGainForId();
    void _Pad_GetVolumeGroupGainForId();
    void _Pad_GetActiveVolumeGroupForEndpointId();
    void _Pad_GetVolumeGroupsForEndpoint();
    void _Pad_GetCurrentVolumeContext();
    void _Pad_SetVolumeGroupMuteForId();
    void _Pad_GetVolumeGroupMuteForId();
    void _Pad_SetRingerVibrateState();
    void _Pad_GetRingerVibrateState();
    void _Pad_SetPreferredChatApplication();
    void _Pad_ResetPreferredChatApplication();
    void _Pad_GetPreferredChatApplication();
    void _Pad_GetCurrentChatApplications();
    void _Pad_add_ChatContextChanged();
    void _Pad_remove_ChatContextChanged();

    // The methods we actually use (slots 25-27)
    // NOTE: Set comes BEFORE Get in the vtable!
    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role,
        [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

    void _Pad_ClearAllPersistedApplicationDefaultEndpoints();
}

// Pre-21H2 (same vtable, different GUID)
[ComImport]
[Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigFactoryDownlevel
{
    // IInspectable (slots 3-5)
    void _Pad_GetIids();
    void _Pad_GetRuntimeClassName();
    void _Pad_GetTrustLevel();

    // IAudioPolicyConfig methods we don't use (slots 6-24)
    void _Pad_add_CtxVolumeChange();
    void _Pad_remove_CtxVolumeChanged();
    void _Pad_add_RingerVibrateStateChanged();
    void _Pad_remove_RingerVibrateStateChanged();
    void _Pad_SetVolumeGroupGainForId();
    void _Pad_GetVolumeGroupGainForId();
    void _Pad_GetActiveVolumeGroupForEndpointId();
    void _Pad_GetVolumeGroupsForEndpoint();
    void _Pad_GetCurrentVolumeContext();
    void _Pad_SetVolumeGroupMuteForId();
    void _Pad_GetVolumeGroupMuteForId();
    void _Pad_SetRingerVibrateState();
    void _Pad_GetRingerVibrateState();
    void _Pad_SetPreferredChatApplication();
    void _Pad_ResetPreferredChatApplication();
    void _Pad_GetPreferredChatApplication();
    void _Pad_GetCurrentChatApplications();
    void _Pad_add_ChatContextChanged();
    void _Pad_remove_ChatContextChanged();

    // The methods we actually use (slots 25-27)
    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role,
        [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

    void _Pad_ClearAllPersistedApplicationDefaultEndpoints();
}

public class AudioPolicyConfigClient : IDisposable
{
    private readonly object? _factory;
    private readonly bool _is21H2;

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall, PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public AudioPolicyConfigClient()
    {
        _is21H2 = Environment.OSVersion.Version.Build >= 22000;

        var classNamePtr = IntPtr.Zero;
        try
        {
            const string className = "Windows.Media.Internal.AudioPolicyConfig";
            WindowsCreateString(className, className.Length, out classNamePtr);

            Guid iid = _is21H2
                ? typeof(IAudioPolicyConfigFactory21H2).GUID
                : typeof(IAudioPolicyConfigFactoryDownlevel).GUID;

            RoGetActivationFactory(classNamePtr, ref iid, out _factory);
        }
        finally
        {
            if (classNamePtr != IntPtr.Zero)
                WindowsDeleteString(classNamePtr);
        }
    }

    public void SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, string? deviceId)
    {
        var hstring = IntPtr.Zero;
        try
        {
            if (deviceId != null)
            {
                var formattedId = FormatDeviceId(deviceId);
                WindowsCreateString(formattedId, formattedId.Length, out hstring);
            }

            int hr;
            if (_is21H2)
                hr = ((IAudioPolicyConfigFactory21H2)_factory!).SetPersistedDefaultAudioEndpoint(processId, flow, role, hstring);
            else
                hr = ((IAudioPolicyConfigFactoryDownlevel)_factory!).SetPersistedDefaultAudioEndpoint(processId, flow, role, hstring);

            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            if (hstring != IntPtr.Zero)
                WindowsDeleteString(hstring);
        }
    }

    public void RouteProcessToDevice(uint processId, string deviceId)
    {
        SetPersistedDefaultAudioEndpoint(processId, EDataFlow.eRender, ERole.eConsole, deviceId);
        SetPersistedDefaultAudioEndpoint(processId, EDataFlow.eRender, ERole.eMultimedia, deviceId);
    }

    public void ClearProcessRouting(uint processId)
    {
        SetPersistedDefaultAudioEndpoint(processId, EDataFlow.eRender, ERole.eConsole, null);
        SetPersistedDefaultAudioEndpoint(processId, EDataFlow.eRender, ERole.eMultimedia, null);
    }

    private static string FormatDeviceId(string deviceId)
    {
        // deviceId from MMDevice.ID looks like: {0.0.0.00000000}.{guid}
        // Full device interface path format:
        // \\?\SWD#MMDEVAPI#{deviceId}#{DEVINTERFACE_AUDIO_RENDER}
        const string renderInterfaceGuid = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        return $@"\\?\SWD#MMDEVAPI#{deviceId}#{renderInterfaceGuid}";
    }

    public void Dispose()
    {
        if (_factory != null)
            Marshal.ReleaseComObject(_factory);
    }
}
