// Lists render endpoints and their default roles, or sets the default render endpoint for all three roles.
//   AudioSwitch list
//   AudioSwitch set <device id>
using System;
using System.Runtime.InteropServices;

static class Program
{
    static int Main(string[] args)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        if (args.Length >= 2 && args[0] == "set")
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            for (int role = 0; role < 3; role++)
            {
                int hr = policy.SetDefaultEndpoint(args[1], role);
                if (hr < 0) { Console.WriteLine($"SetDefaultEndpoint role {role} failed 0x{hr:X8}"); return 1; }
            }
            Console.WriteLine($"default render endpoint set to {args[1]}");
            return 0;
        }

        if (args.Length >= 1 && args[0] == "volume")
        {
            Check(en.GetDefaultAudioEndpoint(0, 0, out IMMDevice dv));
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            Check(dv.Activate(ref iid, 0x17, IntPtr.Zero, out object o));
            var vol = (IAudioEndpointVolume)o;
            Check(vol.GetMasterVolumeLevelScalar(out float level));
            Check(vol.GetMute(out bool mute));
            uint hw = 0; vol.QueryHardwareSupport(out hw);
            Console.WriteLine($"{Name(dv)}: master volume {level * 100:0.0}%, mute {mute}, hardware support 0x{hw:X}");
            return 0;
        }

        if (args.Length >= 2 && (args[0] == "mute" || args[0] == "unmute"))
        {
            // mute or unmute every session of the named processes (comma separated) on every active render endpoint
            bool mute = args[0] == "mute";
            string[] names = args[1].Split(',');
            Check(en.EnumAudioEndpoints(0, 1, out IMMDeviceCollection all));
            all.GetCount(out uint devices);
            for (uint i = 0; i < devices; i++)
            {
                all.Item(i, out IMMDevice dv);
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (dv.Activate(ref iid, 0x17, IntPtr.Zero, out object o) < 0) continue;
                ((IAudioSessionManager2)o).GetSessionEnumerator(out IAudioSessionEnumerator se);
                se.GetCount(out int count);
                for (int k = 0; k < count; k++)
                {
                    se.GetSession(k, out IAudioSessionControl c);
                    ((IAudioSessionControl2)c).GetProcessId(out uint pid);
                    string proc;
                    try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch (Exception) { continue; }
                    if (Array.FindIndex(names, n => string.Equals(n, proc, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                    var vol = (ISimpleAudioVolume)c;
                    vol.GetMute(out bool was);
                    Guid ctx = Guid.Empty;
                    vol.SetMute(mute, ref ctx);
                    Console.WriteLine($"{Name(dv)}: {proc} pid {pid} mute {was} -> {mute}");
                }
            }
            return 0;
        }

        if (args.Length >= 1 && args[0] == "sessions")
        {
            Check(en.GetDefaultAudioEndpoint(0, 0, out IMMDevice ds));
            Guid iid = typeof(IAudioSessionManager2).GUID;
            Check(ds.Activate(ref iid, 0x17, IntPtr.Zero, out object o));
            var mgr = (IAudioSessionManager2)o;
            Check(mgr.GetSessionEnumerator(out IAudioSessionEnumerator se));
            se.GetCount(out int count);
            Console.WriteLine($"{Name(ds)}: {count} sessions");
            for (int i = 0; i < count; i++)
            {
                se.GetSession(i, out IAudioSessionControl c);
                var c2 = (IAudioSessionControl2)c;
                c2.GetState(out int state);
                c2.GetProcessId(out uint pid);
                string proc = "?";
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch (Exception) { }
                Console.WriteLine($"  pid {pid,6} {proc,-24} state {(state == 1 ? "ACTIVE" : state == 0 ? "inactive" : "expired")}");
            }
            return 0;
        }

        string[] defaults = new string[3];
        for (int role = 0; role < 3; role++)
            if (en.GetDefaultAudioEndpoint(0, role, out IMMDevice d) >= 0) { d.GetId(out defaults[role]); }
        Check(en.EnumAudioEndpoints(0, 1, out IMMDeviceCollection col));
        col.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            col.Item(i, out IMMDevice d);
            d.GetId(out string id);
            string roles = (id == defaults[0] ? " console" : "") + (id == defaults[1] ? " multimedia" : "") + (id == defaults[2] ? " communications" : "");
            Console.WriteLine($"{id}\t{Name(d)}\t{(roles.Length > 0 ? "default:" + roles : "")}");
        }
        return 0;
    }

    static string Name(IMMDevice d)
    {
        if (d.OpenPropertyStore(0, out IPropertyStore ps) < 0) return "?";
        var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        if (ps.GetValue(ref key, out PropVariant pv) < 0) return "?";
        string s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "?" : "?";
        PropVariantClear(ref pv);
        return s;
    }

    static void Check(int hr) { if (hr < 0) throw new COMException("call failed", hr); }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PropertyKey k);
        [PreserveSig] int GetValue(ref PropertyKey k, out PropVariant v);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float inc);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr c);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr v);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr n);
        [PreserveSig] int SetDisplayName(IntPtr n, IntPtr ctx);
        [PreserveSig] int GetIconPath(out IntPtr p);
        [PreserveSig] int SetIconPath(IntPtr p, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier(out IntPtr s);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr s);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // undocumented but long-stable (Vista through Windows 11); only SetDefaultEndpoint is used
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr f);
        [PreserveSig] int GetDeviceFormat(string id, int def, IntPtr f);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr a, IntPtr b);
        [PreserveSig] int GetProcessingPeriod(string id, int def, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr a);
        [PreserveSig] int GetShareMode(string id, IntPtr m);
        [PreserveSig] int SetShareMode(string id, IntPtr m);
        [PreserveSig] int GetPropertyValue(string id, int store, IntPtr k, IntPtr v);
        [PreserveSig] int SetPropertyValue(string id, int store, IntPtr k, IntPtr v);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(string id, int visible);
    }
}
