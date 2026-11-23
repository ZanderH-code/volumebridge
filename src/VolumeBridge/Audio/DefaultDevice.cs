using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace VolumeBridge.Audio;

/// <summary>
/// Default playback device helpers. Setting the default uses the undocumented but long-stable
/// IPolicyConfig interface (Windows 10/11), as many audio tools do; failures are reported, never fatal.
/// </summary>
public static class DefaultDevice
{
    public static bool IsDefault(string id)
    {
        using var e = new MMDeviceEnumerator();
        if (!e.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return false;
        using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return string.Equals(d.ID, id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Makes <paramref name="id"/> the default for media and games (console + multimedia roles).</summary>
    public static void SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, Role.Console));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, Role.Multimedia));
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is used; the preceding slots keep the vtable layout.
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility();
    }
}
