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
