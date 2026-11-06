using System.Runtime.InteropServices;

namespace Perch.Services;

// Is any of these processes making sound right now? Reads the Windows audio sessions of the default output device
// (the same data the volume mixer shows). Used for players that don't report "now playing" to Windows themselves.
public static class AudioActivity
{
    public static bool IsAudible(ICollection<int> processIds)
    {
        if (processIds.Count == 0) return false;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var managerObject);
            var sessions = ((IAudioSessionManager2)managerObject).GetSessionEnumerator();
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var control);
                if (control is not IAudioSessionControl2 details) continue;
                details.GetProcessId(out var pid);
                if (!processIds.Contains((int)pid)) continue;
                if (control is IAudioMeterInformation meter)
                {
                    meter.GetPeakValue(out var peak);
                    if (peak > 0.0005f) return true;
                }
            }
        }
        catch (Exception)
        {
            // No audio device, or the audio service is restarting: treat as silent.
        }
        return false;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        void GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [return: MarshalAs(UnmanagedType.Interface)] IAudioSessionEnumerator GetSessionEnumerator();
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.Interface)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        void GetGroupingParam(out Guid param);
        void SetGroupingParam(ref Guid param, ref Guid context);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        void SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
