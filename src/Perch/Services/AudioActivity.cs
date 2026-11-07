using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Perch.Services;

// Is any of these processes making sound right now? Reads the Windows audio sessions of the default output device
// (the same data the volume mixer shows). Used for players that don't report "now playing" to Windows themselves.
//
// The COM interfaces below use the source-generated COM interop ([GeneratedComInterface]) instead of [ComImport],
// because the built-in COM marshalling is not available in Native AOT.
public static partial class AudioActivity
{
    private static readonly Guid MMDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid DeviceEnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid SessionManagerId = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    private static T Wrap<T>(nint pointer) where T : class
    {
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None); }
        finally { Marshal.Release(pointer); } // the wrapper holds its own reference
    }

    public static bool IsAudible(ICollection<int> processIds)
    {
        if (processIds.Count == 0) return false;
        try
        {
            if (CoCreateInstance(MMDeviceEnumeratorClass, 0, 23 /* CLSCTX_ALL */, DeviceEnumeratorId, out var enumeratorPointer) < 0) return false;
            var enumerator = Wrap<IMMDeviceEnumerator>(enumeratorPointer);
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) < 0) return false;
            if (device.Activate(SessionManagerId, 23, 0, out var managerPointer) < 0) return false;
            var manager = Wrap<IAudioSessionManager2>(managerPointer);
            if (manager.GetSessionEnumerator(out var sessions) < 0) return false;
            if (sessions.GetCount(out var count) < 0) return false;
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var sessionPointer) < 0) continue;
                var control = Wrap<IAudioSessionControl2>(sessionPointer);
                if (control.GetProcessId(out var pid) < 0 || !processIds.Contains((int)pid)) continue;
                if (control is IAudioMeterInformation meter && meter.GetPeakValue(out var peak) >= 0 && peak > 0.0005f) return true;
            }
        }
        catch (Exception)
        {
            // No audio device, or the audio service is restarting: treat as silent.
        }
        return false;
    }

    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, int context, nint parameters, out nint result);
    }

    [GeneratedComInterface, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    internal partial interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint groupingParam, int flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint groupingParam, int flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [GeneratedComInterface, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    internal partial interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out nint session);
    }

    // Only GetProcessId is used; the entries before it keep the method order of the real interface.
    [GeneratedComInterface, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    internal partial interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName(nint name, nint context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, nint context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(in Guid param, nint context);
        [PreserveSig] int RegisterAudioSessionNotification(nint client);
        [PreserveSig] int UnregisterAudioSessionNotification(nint client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    [GeneratedComInterface, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    internal partial interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
