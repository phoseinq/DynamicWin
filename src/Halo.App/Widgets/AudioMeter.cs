using System;
using System.Runtime.InteropServices;

namespace Halo.Widgets;

internal sealed class AudioMeter
{
    private IAudioMeterInformation? _meterI;
    private IAudioEndpointVolume? _vol;
    private static Guid _ctx = Guid.Empty;
    private string? _boundId;
    private long _nextDeviceCheck;

    public AudioMeter() => TryAcquire();

    public float Peak()
    {
        DropIfDeviceChanged();
        if (_meterI == null) { TryAcquire(); if (_meterI == null) return 0f; }
        try { _meterI!.GetPeakValue(out float p); return p; }
        catch { _meterI = null; return 0f; }
    }

    public float Volume()
    {
        DropIfDeviceChanged();
        if (_vol == null) { TryAcquire(); if (_vol == null) return 0f; }
        try { _vol!.GetMasterVolumeLevelScalar(out float v); return v; }
        catch { _vol = null; return 0f; }
    }

    public bool Muted()
    {
        DropIfDeviceChanged();
        if (_vol == null) return false;
        try { _vol!.GetMute(out bool m); return m; }
        catch { _vol = null; return false; }
    }

    public void SetVolume(float v)
    {
        DropIfDeviceChanged();
        if (_vol == null) { TryAcquire(); if (_vol == null) return; }
        try { _vol!.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), ref _ctx); }
        catch { _vol = null; }
    }

    public void ToggleMute()
    {
        DropIfDeviceChanged();
        if (_vol == null) { TryAcquire(); if (_vol == null) return; }
        try { _vol!.GetMute(out bool m); _vol.SetMute(!m, ref _ctx); }
        catch { _vol = null; }
    }

        private void DropIfDeviceChanged()
    {
        try
        {
            if (Environment.TickCount64 < _nextDeviceCheck) return;
            _nextDeviceCheck = Environment.TickCount64 + 1000;
            if (_boundId is null) return;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return;
            if (dev.GetId(out var id) != 0 || id == _boundId) return;
            _meterI = null;
            _vol = null;
            _boundId = null;
        }
        catch { }
    }

    private void TryAcquire()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return;
            if (dev.GetId(out var id) == 0) _boundId = id;
            var mid = typeof(IAudioMeterInformation).GUID;
            if (dev.Activate(ref mid, 23, IntPtr.Zero, out var mo) == 0) _meterI = mo as IAudioMeterInformation;
            var vid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref vid, 23, IntPtr.Zero, out var vo) == 0) _vol = vo as IAudioEndpointVolume;
        }
        catch { _meterI = null; _vol = null; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
