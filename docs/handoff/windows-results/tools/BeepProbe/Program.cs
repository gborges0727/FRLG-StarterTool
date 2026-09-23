// BeepProbe - measures when a beep scheduled the way FlowTimer schedules it actually reaches the
// Windows audio engine. Uses a WASAPI loopback capture (QPC-stamped packets) on the same default
// render endpoint that SDL opens.
//
// Modes:
//   queue    : FlowTimer's path. SDL 2.0.10, SDL_OpenAudioDevice(NULL, S16 48k stereo,
//              samples = min engine period, allowed_changes = 0), then per "press":
//              t0 = QPC; build a PCM buffer of --target-s seconds with the beep at --offset-ms
//              (exactly what FlowTimer.UpdatePCM does); SDL_QueueAudio(buffer).
//   callback : SDL callback API with SDL_AUDIO_ALLOW_ANY_CHANGE, beep placed by audio frame index
//              (frames delivered so far, mapped to the press time through the callback timestamp).
//   wasapi   : direct IAudioClient3 shared stream at the minimum period, beep placed by frame index
//              using IAudioClock::GetPosition (device position + matching QPC).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BeepProbe {

    // ------------------------------------------------------------------ COM interop (WASAPI)

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // IAudioClient3 with the IAudioClient and IAudioClient2 slots flattened in vtable order.
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient3 {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
        [PreserveSig] int SetClientProperties(IntPtr props);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriod);
        [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, IntPtr format, IntPtr sessionGuid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClock {
        [PreserveSig] int GetFrequency(out ulong frequency);
        [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
    }

    static class Native {
        public const int eRender = 0, eCapture = 1;
        public const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
        public const uint CLSCTX_ALL = 0x17;
        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 1, AUDCLNT_BUFFERFLAGS_SILENT = 2, AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 4;
        public static Guid IID_IAudioClient3 = new Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
        public static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static Guid IID_IAudioClock = new Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7");
        public static Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");
        public static Guid KSDATAFORMAT_SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");
        public static PropertyKey PKEY_Device_FriendlyName = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);
        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant v);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] public static extern IntPtr AvSetMmThreadCharacteristicsW(string task, out uint index);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);

        public static void Check(int hr, string what) {
            if (hr < 0) throw new Exception(string.Format("{0} failed: 0x{1:X8}", what, hr));
        }
    }

    // Mix format as reported by GetMixFormat.
    class MixFormat {
        public IntPtr Ptr;
        public int Tag, Channels, Rate, Bits, BlockAlign;
        public bool IsFloat;
        public string Describe() { return string.Format("{0} {1} Hz {2} ch {3} bit (tag 0x{4:X})", IsFloat ? "float" : "pcm", Rate, Channels, Bits, Tag); }

        public static MixFormat Read(IntPtr p) {
            MixFormat f = new MixFormat { Ptr = p };
            f.Tag = Marshal.ReadInt16(p, 0) & 0xFFFF;
            f.Channels = Marshal.ReadInt16(p, 2);
            f.Rate = Marshal.ReadInt32(p, 4);
            f.BlockAlign = Marshal.ReadInt16(p, 12);
            f.Bits = Marshal.ReadInt16(p, 14);
            if (f.Tag == 0xFFFE) {
                byte[] g = new byte[16]; Marshal.Copy(p + 24, g, 0, 16);
                Guid sub = new Guid(g);
                f.IsFloat = sub == Native.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                if (!f.IsFloat && sub != Native.KSDATAFORMAT_SUBTYPE_PCM) throw new Exception("Unsupported mix subformat " + sub);
            } else f.IsFloat = f.Tag == 3;
            return f;
        }
    }

    // ------------------------------------------------------------------ SDL2 interop

    static unsafe class SDL {
        public const uint SDL_INIT_AUDIO = 0x10;
        public const ushort AUDIO_S16LSB = 0x8010, AUDIO_F32LSB = 0x8120;
        public const int SDL_AUDIO_ALLOW_ANY_CHANGE = 0xF;

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioSpec { public int freq; public ushort format; public byte channels; public byte silence; public ushort samples; public ushort padding; public uint size; public IntPtr callback; public IntPtr userdata; }
        [StructLayout(LayoutKind.Sequential)] public struct Version { public byte major, minor, patch; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void AudioCallback(IntPtr userdata, IntPtr stream, int len);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_Init(uint flags);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_Quit();
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GetError();
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_GetVersion(out Version v);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GetCurrentAudioDriver();
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern uint SDL_OpenAudioDevice(IntPtr device, int iscapture, ref AudioSpec desired, out AudioSpec obtained, int allowedChanges);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_PauseAudioDevice(uint dev, int pauseOn);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_QueueAudio(uint dev, byte* data, uint len);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_ClearQueuedAudio(uint dev);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern uint SDL_GetQueuedAudioSize(uint dev);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_CloseAudioDevice(uint dev);

        public static string Error() { return Marshal.PtrToStringUTF8(SDL_GetError()); }
    }

    // ------------------------------------------------------------------ loopback capture

    class Packet { public long Qpc; public long Arrival; public ulong DevPos; public uint Flags; public float[] Amp; }

    class Loopback {
        public IAudioClient3 Client; public IAudioCaptureClient Capture; public IAudioClock Clock;
        public MixFormat Fmt; public long StreamLatency; public uint BufferFrames;
        public readonly List<Packet> Packets = new List<Packet>();
        public readonly List<(long qpc, double sec)> ClockSamples = new List<(long, double)>();
        public readonly List<(long qpc, double sec)> ArrivalSamples = new List<(long, double)>();
        public int Discontinuities, TimestampErrors;
        Thread thread; volatile bool running = true; ulong clockFreq;

        public Loopback(IMMDevice dev) {
            object o; Native.Check(dev.Activate(ref Native.IID_IAudioClient3, Native.CLSCTX_ALL, IntPtr.Zero, out o), "Activate(IAudioClient3) loopback");
            Client = (IAudioClient3)o;
            IntPtr fp; Native.Check(Client.GetMixFormat(out fp), "GetMixFormat");
            Fmt = MixFormat.Read(fp);
            Native.Check(Client.Initialize(0, Native.AUDCLNT_STREAMFLAGS_LOOPBACK, 2_000_000, 0, fp, IntPtr.Zero), "Initialize loopback");
            Client.GetBufferSize(out BufferFrames);
            Client.GetStreamLatency(out StreamLatency);
            object svc; Native.Check(Client.GetService(ref Native.IID_IAudioCaptureClient, out svc), "GetService(IAudioCaptureClient)");
            Capture = (IAudioCaptureClient)svc;
            if (Client.GetService(ref Native.IID_IAudioClock, out svc) >= 0) { Clock = (IAudioClock)svc; Clock.GetFrequency(out clockFreq); }
            Native.Check(Client.Start(), "Start loopback");
            thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "loopback" };
            thread.Start();
        }

        public void Stop() { running = false; thread.Join(); Client.Stop(); }

        unsafe void Run() {
            int ch = Fmt.Channels, bps = Fmt.Bits / 8;
            long lastClock = 0;
            while (running) {
                uint next;
                while (Capture.GetNextPacketSize(out next) >= 0 && next > 0) {
                    IntPtr data; uint frames, flags; ulong devPos, qpc;
                    if (Capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc) < 0) break;
                    Packet p = new Packet { Qpc = (long)qpc, Arrival = Program.Now(), DevPos = devPos, Flags = flags, Amp = new float[frames] };
                    if ((flags & Native.AUDCLNT_BUFFERFLAGS_SILENT) == 0) {
                        byte* b = (byte*)data;
                        for (int f = 0; f < frames; f++) {
                            float m = 0;
                            for (int c = 0; c < ch; c++) {
                                float v;
                                if (Fmt.IsFloat) v = *(float*)(b + (f * ch + c) * 4);
                                else if (bps == 2) v = *(short*)(b + (f * ch + c) * 2) / 32768f;
                                else if (bps == 4) v = *(int*)(b + (f * ch + c) * 4) / 2147483648f;
                                else v = 0;
                                if (v < 0) v = -v; if (v > m) m = v;
                            }
                            p.Amp[f] = m;
                        }
                    }
                    Capture.ReleaseBuffer(frames);
                    if ((flags & Native.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) Discontinuities++;
                    if ((flags & Native.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0) TimestampErrors++;
                    else lock (ClockSamples) ClockSamples.Add(((long)qpc, devPos / (double)Fmt.Rate));   // device frame position vs QPC
                    lock (ArrivalSamples) ArrivalSamples.Add((p.Arrival, devPos / (double)Fmt.Rate));    // device frame position vs when the packet reached us
                    lock (Packets) Packets.Add(p);
                }
                Thread.SpinWait(2000);   // busy poll so packet arrival is timed to a few microseconds
            }
        }

        // Engine clock fitted from packet arrivals: qpc = a + b * devicePosition. The arrival of a packet is
        // pinned to the end of the engine pass that mixed it, so the fit has no per-period ambiguity and the
        // polling jitter averages out over thousands of packets. The constant a includes the (unknown, fixed)
        // engine-to-endpoint latency.
        public (double a, double b, double rmsMs, int n) ArrivalFit() {
            List<(long qpc, double sec)> s; lock (ArrivalSamples) s = ArrivalSamples.ToList();
            return FitOf(s);
        }

        // same fit restricted to packets within +-windowSec of a device position, so a gap or glitch
        // elsewhere in the run (engine idling, capture stall) cannot bend the line
        public (double a, double b, double rmsMs, int n) ArrivalFitAround(double posSec, double windowSec) {
            List<(long qpc, double sec)> s; lock (ArrivalSamples) s = ArrivalSamples.Where(x => Math.Abs(x.sec - posSec) <= windowSec).ToList();
            return FitOf(s);
        }

        (double a, double b, double rmsMs, int n) FitOf(List<(long qpc, double sec)> s) {
            if (s.Count < 10) return (0, 0, 0, 0);
            double x0 = s[0].sec, y0 = s[0].qpc, sx = 0, sy = 0, sxx = 0, sxy = 0; int n = s.Count;
            foreach (var (qpc, sec) in s) { double x = sec - x0, y = qpc - y0; sx += x; sy += y; sxx += x * x; sxy += x * y; }
            double b = (n * sxy - sx * sy) / (n * sxx - sx * sx), icpt = (sy - b * sx) / n;
            double sumSq = 0; foreach (var (qpc, sec) in s) { double r = (qpc - y0) - (b * (sec - x0) + icpt); sumSq += r * r; }
            double bFrames = b / Fmt.Rate;                       // 100 ns per frame
            double a = y0 + icpt - b * x0;                       // qpc at position 0 seconds
            return (a, bFrames, Math.Sqrt(sumSq / n) / 1e4, n);
        }

        public long FitTime(long devPos) { var f = ArrivalFit(); return (long)(f.a + f.b * devPos); }

        // max amplitude per 10 ms bin between two QPC times, without consuming packets
        public string Envelope(long fromQpc, long toQpc) {
            List<Packet> pk; lock (Packets) pk = Packets.Where(p => p.Qpc >= fromQpc - 200_000 && p.Qpc <= toQpc).ToList();
            int bins = (int)((toQpc - fromQpc) / 100_000); float[] env = new float[Math.Max(bins, 1)];
            foreach (Packet p in pk) for (int f = 0; f < p.Amp.Length; f++) { long tf = p.Qpc + f * 10_000_000L / Fmt.Rate; int b = (int)((tf - fromQpc) / 100_000); if (b >= 0 && b < env.Length && p.Amp[f] > env[b]) env[b] = p.Amp[f]; }
            StringBuilder sb = new StringBuilder();
            for (int b = 0; b < env.Length; b++) sb.Append(env[b] < 0.01f ? '.' : env[b] < 0.1f ? ':' : env[b] < 0.3f ? 'o' : '#');
            return sb.ToString();
        }

        public List<Packet> Take(long fromQpc, long toQpc) {
            lock (Packets) {
                List<Packet> r = Packets.Where(p => p.Qpc >= fromQpc && p.Qpc <= toQpc).ToList();
                Packets.RemoveAll(p => p.Qpc < fromQpc);
                return r;
            }
        }

        public string ClockDrift() { lock (ClockSamples) return Drift(ClockSamples); }

        // least-squares slope of audio-clock seconds against QPC seconds, as ppm off nominal
        public static string Drift(List<(long qpc, double sec)> s) {
            if (s.Count < 10) return "n/a";
            double x0 = s[0].qpc, y0 = s[0].sec, sx = 0, sy = 0, sxx = 0, sxy = 0; int n = s.Count;
            foreach (var (qpc, sec) in s) { double x = (qpc - x0) / 1e7, y = sec - y0; sx += x; sy += y; sxx += x * x; sxy += x * y; }
            double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
            double icpt = (sy - slope * sx) / n, maxRes = 0, sumSq = 0;
            foreach (var (qpc, sec) in s) { double r = (sec - y0) - (slope * (qpc - x0) / 1e7 + icpt); sumSq += r * r; if (Math.Abs(r) > maxRes) maxRes = Math.Abs(r); }
            return string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0} ppm over {1:0.0} s ({2} samples); residual of qpc-vs-position fit: rms {3:0.000} ms, max {4:0.000} ms",
                (slope - 1) * 1e6, (s[n - 1].qpc - x0) / 1e7, n, Math.Sqrt(sumSq / n) * 1000, maxRes * 1000);
        }
    }

    // ------------------------------------------------------------------ program

    static unsafe class Program {
        public static long Now() {   // QPC in 100 ns units, same time base as WASAPI's QPC positions
            long ts = Stopwatch.GetTimestamp();
            return Stopwatch.Frequency == 10_000_000 ? ts : (long)(ts * (1e7 / Stopwatch.Frequency));
        }
        static double Ms(long t) { return t / 1e4; }

        // beep source: 48 kHz S16 stereo like FlowTimer's bundled wavs
        static byte[] beepPcm; static int beepFrames; static int beepOnsetFrame; static float beepPeak;

        static string mode = "queue", sdlPath = null, beepPath = null, csvPath = null;
        static int trials = 50; static double offsetMs = 500; static double targetS = 40; static int samplesOverride = -1;
        static bool allowAny = false, presubmit = false; static int gapMaxMs = 97; static int role = Native.eConsole;

        static int Main(string[] args) {
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--mode": mode = args[++i]; break;
                    case "--sdl": sdlPath = args[++i]; break;
                    case "--beep": beepPath = args[++i]; break;
                    case "--csv": csvPath = args[++i]; break;
                    case "--trials": trials = int.Parse(args[++i]); break;
                    case "--offset-ms": offsetMs = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--target-s": targetS = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--samples": samplesOverride = int.Parse(args[++i]); break;
                    case "--allow-any": allowAny = true; break;
                    case "--presubmit": presubmit = true; break;
                    case "--gap-max-ms": gapMaxMs = int.Parse(args[++i]); break;
                    case "--multimedia-role": role = Native.eMultimedia; break;
                    case "--log": diagLog = args[++i]; break;
                    case "--envelope": envelope = true; break;
                    case "--onset-frac": onsetFraction = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--envelope-ms": envelopeMs = int.Parse(args[++i]); break;
                    case "--selftest-dir": selftestDir = args[++i]; break;
                    case "--max-s": maxSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--volume": volumeScale = int.Parse(args[++i]) / 100f; break;
                    case "--abs-thr": absThreshold = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--device-id": deviceId = args[++i]; break;
                    case "--drift": toolDrift = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    default: Console.WriteLine("unknown arg " + args[i]); return 2;
                }
            }
            Native.timeBeginPeriod(1);
            if (sdlPath == null) sdlPath = Path.Combine(AppContext.BaseDirectory, "SDL2.dll");
            NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, (name, asm, path) => name == "SDL2" ? NativeLibrary.Load(sdlPath) : IntPtr.Zero);

            LoadBeep();
            Console.WriteLine("BeepProbe mode={0} trials={1} offset={2} ms target={3} s presubmit={4} allowAny={5} process={6}",
                mode, trials, offsetMs, targetS, presubmit, allowAny, Environment.Is64BitProcess ? "x64" : "x86");
            Console.WriteLine("QPC frequency {0} Hz, beep {1} frames, onset at frame {2}, peak {3:0.000}", Stopwatch.Frequency, beepFrames, beepOnsetFrame, beepPeak);

            // --- default endpoints, engine periods (what AudioContext.GlobalInit reads, plus the Console-role one SDL uses)
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice devConsole, devMM;
            Native.Check(en.GetDefaultAudioEndpoint(Native.eRender, Native.eConsole, out devConsole), "GetDefaultAudioEndpoint(Console)");
            Native.Check(en.GetDefaultAudioEndpoint(Native.eRender, Native.eMultimedia, out devMM), "GetDefaultAudioEndpoint(Multimedia)");
            string idC = DevId(devConsole), idM = DevId(devMM);
            Console.WriteLine("default render (Console role):    {0}", DevName(devConsole));
            Console.WriteLine("default render (Multimedia role): {0}{1}", DevName(devMM), idC == idM ? "  [same endpoint]" : "  [DIFFERENT endpoint]");
            uint minPeriodMM = DescribeClient(devMM, "Multimedia");
            uint minPeriodC = DescribeClient(devConsole, "Console");
            int flowTimerSamples = (int)minPeriodMM;   // AudioContext.MinBufferSize
            if (samplesOverride > 0) flowTimerSamples = samplesOverride;

            IMMDevice captureDev = role == Native.eConsole ? devConsole : devMM;
            if (mode == "external" || mode == "selftest") StartSilentKeepalive(captureDev);   // keep the engine running before the app starts, so loopback never gaps
            if (deviceId != null) { Native.Check(en.GetDevice(deviceId, out captureDev), "GetDevice " + deviceId); Console.WriteLine("capturing the named endpoint: {0}", DevName(captureDev)); }
            Loopback lb = new Loopback(captureDev);
            Console.WriteLine("loopback capture: {0}, buffer {1} frames, GetStreamLatency {2:0.00} ms", lb.Fmt.Describe(), lb.BufferFrames, lb.StreamLatency / 1e4);
            Thread.Sleep(300);

            List<double> delays = new List<double>(), delaysFromQueue = new List<double>(), preps = new List<double>(), queueCalls = new List<double>();
            fitTrials.Clear();
            StreamWriter csv = csvPath != null ? new StreamWriter(csvPath) : null;
            if (csv != null) csv.WriteLine("trial,mode,t0_100ns,prep_ms,queuecall_ms,delay_from_t0_ms,delay_from_queue_ms,peak,notes");
            Random rng = new Random(12345);

            try {
                if (mode == "queue") RunQueue(lb, flowTimerSamples, rng, delays, delaysFromQueue, preps, queueCalls, csv);
                else if (mode == "callback") RunCallback(lb, flowTimerSamples, rng, delays, csv);
                else if (mode == "wasapi") RunWasapi(lb, captureDev, rng, delays, csv);
                else if (mode == "external") RunExternal(lb, delays, csv);
                else if (mode == "selftest") { RunSelftest(lb); return 0; }
                else if (mode == "clockprobe") { RunClockProbe(captureDev); return 0; }
                else if (mode == "record") { RunRecord(lb); return 0; }
                else { Console.WriteLine("bad mode"); return 2; }
            } finally {
                lb.Stop();
                if (csv != null) csv.Close();
            }

            Console.WriteLine();
            Console.WriteLine("device clock vs QPC: {0}", lb.ClockDrift());
            Console.WriteLine("loopback packets flagged: discontinuity {0}, timestamp-error {1}", lb.Discontinuities, lb.TimestampErrors);
            Console.WriteLine();
            var fit = lb.ArrivalFit();
            Console.WriteLine("engine clock fitted from {0} packet arrivals: {1:+0.0;-0.0} ppm vs nominal, fit residual rms {2:0.000} ms", fit.n, (1e7 / (fit.b * lb.Fmt.Rate) - 1) * 1e6, fit.rmsMs);
            List<double> fitDelays = new List<double>(), localRms = new List<double>();
            foreach (FitTrial tr in fitTrials) {
                var lf = lb.ArrivalFitAround(tr.onsetDevPos / (double)lb.Fmt.Rate, 3.0);   // +-3 s of packets around this beep
                if (lf.n < 10) continue;
                fitDelays.Add(Ms((long)(lf.a + lf.b * tr.onsetDevPos) - tr.expected)); localRms.Add(lf.rmsMs);
            }
            if (localRms.Count > 0) Console.WriteLine("per-trial local fits (+-3 s): {0} trials, residual rms median {1:0.000} ms, max {2:0.000} ms", localRms.Count, localRms.OrderBy(x => x).ElementAt(localRms.Count / 2), localRms.Max());
            Report("beep late by, ARRIVAL-FIT clock    ", fitDelays);
            if (csvPath != null) File.WriteAllLines(csvPath + ".fit.csv", new[] { "trial,delay_fit_ms" }.Concat(fitDelays.Select((d, i) => string.Format(CultureInfo.InvariantCulture, "{0},{1:0.000}", i, d))));
            Report("beep late by, packet-stamp clock   ", delays);
            if (delaysFromQueue.Count > 0) Report("  same, from SDL_QueueAudio call  ", delaysFromQueue);
            if (preps.Count > 0) Report("clock read -> QueueAudio (UpdatePCM)", preps);
            if (queueCalls.Count > 0) Report("SDL_QueueAudio call duration      ", queueCalls);
            Console.WriteLine("histogram of arrival-fit delays:");
            Histogram(fitDelays);
            return 0;
        }

        struct FitTrial { public long expected, onsetDevPos; }
        static readonly List<FitTrial> fitTrials = new List<FitTrial>();

        // ---------------- queue mode: FlowTimer's exact path

        static void RunQueue(Loopback lb, int samples, Random rng, List<double> delays, List<double> delaysFromQueue, List<double> preps, List<double> queueCalls, StreamWriter csv) {
            Native.Check(SDL.SDL_Init(SDL.SDL_INIT_AUDIO), "SDL_Init");
            SDL.Version v; SDL.SDL_GetVersion(out v);
            Console.WriteLine("SDL {0}.{1}.{2} driver {3} from {4}", v.major, v.minor, v.patch, Marshal.PtrToStringUTF8(SDL.SDL_GetCurrentAudioDriver()), sdlPath);
            SDL.AudioSpec desired = new SDL.AudioSpec { freq = 48000, format = SDL.AUDIO_S16LSB, channels = 2, samples = (ushort)samples };
            SDL.AudioSpec obtained;
            uint dev = SDL.SDL_OpenAudioDevice(IntPtr.Zero, 0, ref desired, out obtained, allowAny ? SDL.SDL_AUDIO_ALLOW_ANY_CHANGE : 0);
            if (dev == 0) throw new Exception("SDL_OpenAudioDevice: " + SDL.Error());
            Console.WriteLine("SDL desired: {0} Hz fmt 0x{1:X} ch {2} samples {3}", desired.freq, desired.format, desired.channels, desired.samples);
            Console.WriteLine("SDL obtained: {0} Hz fmt 0x{1:X} ch {2} samples {3} ({4:0.00} ms) size {5} bytes{6}", obtained.freq, obtained.format, obtained.channels, obtained.samples,
                obtained.samples * 1000.0 / obtained.freq, obtained.size, (obtained.format != desired.format || obtained.freq != desired.freq) ? "  [conversion stream inserted]" : "");
            SDL.SDL_PauseAudioDevice(dev, 0);
            Thread.Sleep(500);

            int sampleRate = obtained.freq, ch = obtained.channels; bool isFloat = obtained.format == SDL.AUDIO_F32LSB;   // app-side spec, as in UpdatePCM
            for (int t = -3; t < trials; t++) {
                RandomGap(rng);
                if (presubmit) {
                    // like the first Submit: a buffer whose beep is far away, then the +/- key path: ClearQueuedAudio, re-Submit
                    byte[] first = BuildPcm(30_000, sampleRate, ch, isFloat);
                    fixed (byte* p = first) SDL.SDL_QueueAudio(dev, p, (uint)first.Length);
                    Thread.Sleep(50 + rng.Next(200));
                    SDL.SDL_ClearQueuedAudio(dev);
                }
                long t0 = Now();                                        // VariableOffsetTimer.Submit: double now = Win32.GetTime();
                byte[] pcm = BuildPcm(offsetMs, sampleRate, ch, isFloat, targetS);   // FlowTimer.UpdatePCM
                long tq0 = Now();
                fixed (byte* p = pcm) SDL.SDL_QueueAudio(dev, p, (uint)pcm.Length);   // AudioContext.QueueAudio
                long tq1 = Now();
                uint queued = SDL.SDL_GetQueuedAudioSize(dev);

                WaitUntil(t0 + (long)((offsetMs + beepFrames * 1000.0 / 48000 + 250) * 1e4));
                SDL.SDL_ClearQueuedAudio(dev);
                long expected = t0 + (long)(offsetMs * 1e4) + (long)(beepOnsetFrame * 1e7 / 48000);
                string note; float peak; long onsetDevPos; long onset = FindOnset(lb, t0, expected, out peak, out note, out onsetDevPos);
                if (t < 0) continue;   // warm-up
                if (onset < 0) { Console.WriteLine("trial {0}: no beep found ({1})", t, note); continue; }
                fitTrials.Add(new FitTrial { expected = expected, onsetDevPos = onsetDevPos });
                double d = Ms(onset - expected), dq = Ms(onset - (expected + (tq0 - t0))), prep = Ms(tq0 - t0), qc = Ms(tq1 - tq0);
                delays.Add(d); delaysFromQueue.Add(dq); preps.Add(prep); queueCalls.Add(qc);
                Console.WriteLine("trial {0,3}: late {1,7:0.00} ms  (prep {2:0.00} ms, queue call {3:0.00} ms, queued {4} bytes, peak {5:0.00}{6})", t, d, prep, qc, queued, peak, note);
                if (csv != null) csv.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},queue,{1},{2:0.000},{3:0.000},{4:0.000},{5:0.000},{6:0.000},{7}", t, t0, prep, qc, d, dq, peak, note.Trim()));
            }
            SDL.SDL_CloseAudioDevice(dev);
            SDL.SDL_Quit();
        }

        // ---------------- callback mode: SDL callback API, beep placed by frame index

        static SDL.AudioCallback cbKeepAlive;
        static readonly object cbLock = new object();
        static long cbFramesDelivered, cbQpc, cbTotalFrames;
        static long cbSched = -1;
        static float[] cbBeepF; static int cbChannels; static bool cbFloat;

        static void RunCallback(Loopback lb, int samples, Random rng, List<double> delays, StreamWriter csv) {
            Native.Check(SDL.SDL_Init(SDL.SDL_INIT_AUDIO), "SDL_Init");
            SDL.Version v; SDL.SDL_GetVersion(out v);
            Console.WriteLine("SDL {0}.{1}.{2} driver {3} from {4}", v.major, v.minor, v.patch, Marshal.PtrToStringUTF8(SDL.SDL_GetCurrentAudioDriver()), sdlPath);
            cbKeepAlive = Callback;
            SDL.AudioSpec desired = new SDL.AudioSpec { freq = 48000, format = SDL.AUDIO_S16LSB, channels = 2, samples = (ushort)samples, callback = Marshal.GetFunctionPointerForDelegate(cbKeepAlive) };
            SDL.AudioSpec obtained;
            uint dev = SDL.SDL_OpenAudioDevice(IntPtr.Zero, 0, ref desired, out obtained, SDL.SDL_AUDIO_ALLOW_ANY_CHANGE);
            if (dev == 0) throw new Exception("SDL_OpenAudioDevice: " + SDL.Error());
            Console.WriteLine("SDL obtained: {0} Hz fmt 0x{1:X} ch {2} samples {3} ({4:0.00} ms)", obtained.freq, obtained.format, obtained.channels, obtained.samples, obtained.samples * 1000.0 / obtained.freq);
            if (obtained.format != SDL.AUDIO_F32LSB && obtained.format != SDL.AUDIO_S16LSB) throw new Exception("unsupported obtained format");
            cbFloat = obtained.format == SDL.AUDIO_F32LSB; cbChannels = obtained.channels;
            cbBeepF = BeepAsFloat(obtained.freq, cbChannels);
            SDL.SDL_PauseAudioDevice(dev, 0);
            Thread.Sleep(500);
            int rate = obtained.freq;

            for (int t = -3; t < trials; t++) {
                RandomGap(rng);
                long fd, fq; lock (cbLock) { fd = cbFramesDelivered; fq = cbQpc; }
                long tPress = Now();
                double frameAtPress = fd + (tPress - fq) * rate / 1e7;
                long sched = (long)Math.Round(frameAtPress + offsetMs * rate / 1000.0);
                Volatile.Write(ref cbSched, sched);
                WaitUntil(tPress + (long)((offsetMs + beepFrames * 1000.0 / 48000 + 250) * 1e4));
                Volatile.Write(ref cbSched, -1);
                long expected = tPress + (long)(offsetMs * 1e4) + (long)(beepOnsetFrame * 1e7 / 48000);
                string note; float peak; long onsetDevPos; long onset = FindOnset(lb, tPress, expected, out peak, out note, out onsetDevPos);
                if (t < 0) continue;
                if (onset < 0) { Console.WriteLine("trial {0}: no beep found ({1})", t, note); continue; }
                fitTrials.Add(new FitTrial { expected = expected, onsetDevPos = onsetDevPos });
                double d = Ms(onset - expected); delays.Add(d);
                Console.WriteLine("trial {0,3}: late {1,7:0.00} ms  (press {2:0.0} ms after last callback, peak {3:0.00}{4})", t, d, Ms(tPress - fq), peak, note);
                if (csv != null) csv.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},callback,{1},0,0,{2:0.000},{2:0.000},{3:0.000},{4}", t, tPress, d, peak, note.Trim()));
            }
            SDL.SDL_CloseAudioDevice(dev);
            SDL.SDL_Quit();
        }

        static void Callback(IntPtr userdata, IntPtr stream, int len) {
            long now = Now();
            int bps = cbFloat ? 4 : 2;
            int frames = len / (cbChannels * bps);
            long start = cbTotalFrames;
            lock (cbLock) { cbFramesDelivered = start; cbQpc = now; }
            new Span<byte>((void*)stream, len).Clear();
            long s = Volatile.Read(ref cbSched);
            if (s >= 0) FillBeep(stream, start, frames, s, cbBeepF, cbChannels, cbFloat);
            cbTotalFrames = start + frames;
        }

        // copy the overlap of [start, start+frames) with [sched, sched+beepFrames) into the buffer
        static void FillBeep(IntPtr buf, long start, int frames, long sched, float[] beepF, int channels, bool isFloat) {
            int bf = beepF.Length / channels;
            long from = Math.Max(start, sched), to = Math.Min(start + frames, sched + bf);
            for (long f = from; f < to; f++) {
                int bi = (int)(f - sched), oi = (int)(f - start);
                for (int c = 0; c < channels; c++) {
                    float v = beepF[bi * channels + c];
                    if (isFloat) *(float*)((byte*)buf + (oi * channels + c) * 4) = v;
                    else *(short*)((byte*)buf + (oi * channels + c) * 2) = (short)(v * 32767);
                }
            }
        }

        // ---------------- wasapi mode: IAudioClient3 at min period, beep placed by IAudioClock position

        static long waSched = -1, waWritten; static volatile bool waRunning = true; static long waLateCount;

        static void RunWasapi(Loopback lb, IMMDevice dev, Random rng, List<double> delays, StreamWriter csv) {
            object o; Native.Check(dev.Activate(ref Native.IID_IAudioClient3, Native.CLSCTX_ALL, IntPtr.Zero, out o), "Activate(IAudioClient3) render");
            IAudioClient3 client = (IAudioClient3)o;
            IntPtr fp; Native.Check(client.GetMixFormat(out fp), "GetMixFormat");
            MixFormat fmt = MixFormat.Read(fp);
            uint dp, fnd, minp, maxp; Native.Check(client.GetSharedModeEnginePeriod(fp, out dp, out fnd, out minp, out maxp), "GetSharedModeEnginePeriod");
            int hr = client.InitializeSharedAudioStream(Native.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, minp, fp, IntPtr.Zero);
            string how = "InitializeSharedAudioStream period " + minp;
            if (hr < 0) { how = string.Format("Initialize (InitializeSharedAudioStream failed 0x{0:X8})", hr); Native.Check(client.Initialize(0, Native.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, fp, IntPtr.Zero), "Initialize render"); }
            uint bufFrames; client.GetBufferSize(out bufFrames);
            long lat; client.GetStreamLatency(out lat);
            uint curPeriod; IntPtr curFmt; client.GetCurrentSharedModeEnginePeriod(out curFmt, out curPeriod); if (curFmt != IntPtr.Zero) Native.CoTaskMemFree(curFmt);
            Console.WriteLine("wasapi render: {0}, {1}, buffer {2} frames ({3:0.00} ms), GetStreamLatency {4:0.00} ms, current engine period {5} frames", fmt.Describe(), how, bufFrames, bufFrames * 1000.0 / fmt.Rate, lat / 1e4, curPeriod);
            AutoResetEvent evt = new AutoResetEvent(false);
            Native.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            object svc; Native.Check(client.GetService(ref Native.IID_IAudioRenderClient, out svc), "GetService(IAudioRenderClient)");
            IAudioRenderClient render = (IAudioRenderClient)svc;
            Native.Check(client.GetService(ref Native.IID_IAudioClock, out svc), "GetService(IAudioClock)");
            IAudioClock clock = (IAudioClock)svc;
            ulong clockFreq; clock.GetFrequency(out clockFreq);
            float[] beepF = BeepAsFloat(fmt.Rate, fmt.Channels);
            int rate = fmt.Rate, channels = fmt.Channels; bool isFloat = fmt.IsFloat;
            if (!isFloat && fmt.Bits != 16) throw new Exception("unsupported render format");

            // pre-fill with silence, then start
            IntPtr data; Native.Check(render.GetBuffer(bufFrames, out data), "GetBuffer prefill");
            new Span<byte>((void*)data, (int)(bufFrames * fmt.BlockAlign)).Clear();
            render.ReleaseBuffer(bufFrames, 0); waWritten = bufFrames;
            List<(long qpc, double sec)> renderClock = new List<(long, double)>();
            Thread rt = new Thread(() => {
                uint idx; Native.AvSetMmThreadCharacteristicsW("Pro Audio", out idx);
                long lastClock = 0;
                while (waRunning) {
                    if (!evt.WaitOne(200)) continue;
                    long now = Now();
                    if (now - lastClock > 2_000_000) { ulong cp, cq; if (clock.GetPosition(out cp, out cq) >= 0) lock (renderClock) renderClock.Add(((long)cq, cp / (double)clockFreq)); lastClock = now; }
                    uint pad; if (client.GetCurrentPadding(out pad) < 0) continue;
                    uint n = bufFrames - pad; if (n == 0) continue;
                    IntPtr d; if (render.GetBuffer(n, out d) < 0) continue;
                    new Span<byte>((void*)d, (int)(n * fmt.BlockAlign)).Clear();
                    long s = Volatile.Read(ref waSched);
                    if (s >= 0) { if (s < waWritten && waWritten == Volatile.Read(ref waWrittenAtSched)) Interlocked.Increment(ref waLateCount); FillBeep(d, waWritten, (int)n, s, beepF, channels, isFloat); }
                    render.ReleaseBuffer(n, 0);
                    Volatile.Write(ref waWritten, waWritten + n);
                }
            }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "wasapi-render" };
            rt.Start();
            Native.Check(client.Start(), "Start render");
            Thread.Sleep(500);

            // how IAudioClock behaves between engine passes: position steps and qpc-vs-now
            Console.WriteLine("IAudioClock probe (GetFrequency {0}): ms since last position change / (qpc - now) us", clockFreq);
            { StringBuilder sb = new StringBuilder(); ulong lastPos = ulong.MaxValue; long lastChange = 0;
              for (int i = 0; i < 40; i++) { ulong pp, qq; long n0 = Now(); clock.GetPosition(out pp, out qq); if (pp != lastPos) { lastPos = pp; lastChange = n0; }
                  sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.0}/{1} ", Ms(n0 - lastChange), ((long)qq - n0) / 10); WaitUntil(n0 + 10_000); }
              Console.WriteLine("  " + sb); }

            double engineErr0 = double.NaN;
            for (int t = -3; t < trials; t++) {
                RandomGap(rng);
                ulong pos, q; Native.Check(clock.GetPosition(out pos, out q), "IAudioClock.GetPosition");
                long tPress = Now();
                double frameAtQpc = pos / (double)clockFreq * rate;
                double frameAtPress = frameAtQpc + (tPress - (long)q) * rate / 1e7;
                long sched = (long)Math.Round(frameAtPress + offsetMs * rate / 1000.0);
                long writtenNow = Volatile.Read(ref waWritten);
                Volatile.Write(ref waWrittenAtSched, writtenNow);
                Volatile.Write(ref waSched, sched);
                WaitUntil(tPress + (long)((offsetMs + beepFrames * 1000.0 / 48000 + 250) * 1e4));
                Volatile.Write(ref waSched, -1);
                long expected = tPress + (long)(offsetMs * 1e4) + (long)(beepOnsetFrame * 1e7 / 48000);
                string note; float peak; long onsetDevPos; long onset = FindOnset(lb, tPress, expected, out peak, out note, out onsetDevPos);
                if (t < 0) continue;
                if (onset < 0) { Console.WriteLine("trial {0}: no beep found ({1})", t, note); continue; }
                fitTrials.Add(new FitTrial { expected = expected, onsetDevPos = onsetDevPos });
                double d = Ms(onset - expected); delays.Add(d);
                // placement error measured in the engine's own timeline (loopback device position vs scheduled render frame), relative to trial 0
                double engineErr = onsetDevPos * 1000.0 / lb.Fmt.Rate - (sched + (long)beepOnsetFrame * rate / 48000) * 1000.0 / rate;
                if (double.IsNaN(engineErr0)) engineErr0 = engineErr;
                Console.WriteLine("trial {0,3}: late {1,7:0.00} ms  (engine-domain {2,6:+0.00;-0.00} ms vs trial 0; clock read {3:0.00} ms before press, pos-qpc age {4:0.00} ms, write cursor {5:0.0} ms ahead of play, peak {6:0.00}{7})",
                    t, d, engineErr - engineErr0, Ms(tPress - (long)q), Ms(tPress - (long)q), (writtenNow - frameAtPress) * 1000.0 / rate, peak, note);
                if (csv != null) csv.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},wasapi,{1},0,0,{2:0.000},{2:0.000},{3:0.000},{4}", t, tPress, d, peak, note.Trim()));
            }
            waRunning = false; rt.Join(); client.Stop();
            Console.WriteLine("wasapi: beeps scheduled behind the write cursor: {0}", waLateCount);
            lock (renderClock) Console.WriteLine("wasapi: render IAudioClock vs QPC: {0}", Loopback.Drift(renderClock));
        }
        static long waWrittenAtSched;

        // ---------------- external mode: the real FlowTimer (diagnostic build) runs and logs
        // "SUBMIT <now_qpc> <expected_final_beep_qpc> <after_queue_qpc> <numBeeps> <intervalMs>"; we only listen.

        static string diagLog; static bool envelope; static int envelopeMs = 2600; static float onsetFraction = 0.3f;

        static void RunExternal(Loopback lb, List<double> delays, StreamWriter csv) {
            if (Stopwatch.Frequency != 10_000_000) throw new Exception("external mode assumes a 10 MHz QPC (FlowTimer logs Win32.GetTime()*1e4)");
            Console.WriteLine("external mode: waiting for {0} Submit events in {1} (start the app and drive it now)", trials, diagLog);
            long pos = File.Exists(diagLog) ? new FileInfo(diagLog).Length : 0;
            int t = 0;
            while (t < trials) {
                Thread.Sleep(20);
                if (!File.Exists(diagLog)) continue;
                string[] lines;
                using (FileStream fs = new FileStream(diagLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    if (fs.Length <= pos) continue;
                    fs.Seek(pos, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - pos]; fs.Read(buf, 0, buf.Length); pos = fs.Length;
                    lines = Encoding.ASCII.GetString(buf).Split('\n');
                }
                foreach (string line in lines) {
                    string[] f = line.Trim().Split(' ');
                    if (f.Length < 6 || f[0] != "SUBMIT") continue;
                    long now = long.Parse(f[1]), expectedBeep = long.Parse(f[2]), afterQueue = long.Parse(f[3]);
                    int numBeeps = int.Parse(f[4]), interval = int.Parse(f[5]);
                    long expected = expectedBeep + (long)(beepOnsetFrame * 1e7 / 48000);
                    WaitUntil(expected + (long)((beepFrames * 1000.0 / 48000 + 250) * 1e4));
                    Thread.Sleep(60);
                    if (envelope) Console.WriteLine("  envelope, 10 ms bins, from {0} ms before the expected final beep to 100 ms after (| = expected):\n  {1}|{2}",
                        envelopeMs, lb.Envelope(expected - envelopeMs * 10_000L, expected), lb.Envelope(expected, expected + 1_000_000));
                    // look only after the second-to-last beep has finished
                    long searchFrom = expected - Math.Min(2_000_000, (long)(interval * 1e4) - 1_000_000);
                    string note; float peak; long onsetDevPos; long onset = FindOnset(lb, searchFrom, expected, out peak, out note, out onsetDevPos);
                    if (onset < 0) { Console.WriteLine("submit {0}: no beep found ({1})", t, note); t++; continue; }
                    fitTrials.Add(new FitTrial { expected = expected, onsetDevPos = onsetDevPos });
                    double d = Ms(onset - expected); delays.Add(d);
                    Console.WriteLine("submit {0,3}: final beep late {1,7:0.00} ms  (Submit clock read -> queued {2:0.00} ms, beep {3:0.0} s after Submit, peak {4:0.00}{5})", t, d, Ms(afterQueue - now), Ms(expectedBeep - now) / 1000, peak, note);
                    if (csv != null) csv.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},external,{1},{2:0.000},0,{3:0.000},{3:0.000},{4:0.000},{5}", t, now, Ms(afterQueue - now), d, peak, note.Trim()));
                    t++;
                }
            }
        }

        // ---------------- selftest mode: listen while FRLGStarterTool.exe --beep-selftest runs, then
        // re-measure every beep in its CSVs on the arrival-fit clock instead of the packet stamps.

        static string selftestDir; static double maxSeconds = 900; static float volumeScale = 1f; static double? toolDrift;

        // the ClockDrift the self-test applies, read from the tool's settings file the same way (1.0 when absent)
        static double ReadToolDrift() {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "frlg-startertool", "settings.json");
            if (!File.Exists(path)) return 1.0;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), "\"ClockDrift\"\\s*:\\s*([0-9.eE+-]+)");
            if (!m.Success) return 1.0;
            double d = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return Math.Abs(d - 1.0) < 0.001 ? d : 1.0;
        }

        static void RunSelftest(Loopback lb) {
            if (Stopwatch.Frequency != 10_000_000) throw new Exception("selftest mode assumes a 10 MHz QPC (the tool's target ms = QPC / 1e4 before its drift correction)");
            DateTime started = DateTime.Now;
            string summaryPath = Path.Combine(selftestDir, "beep-selftest-summary.txt");
            Console.WriteLine("selftest mode: listening until {0} is rewritten (max {1} s)", summaryPath, maxSeconds);
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSeconds) {
                Thread.Sleep(250);
                if (File.Exists(summaryPath) && File.GetLastWriteTime(summaryPath) > started) break;
            }
            Thread.Sleep(500);
            Console.WriteLine("stopped listening after {0:0.0} s", sw.Elapsed.TotalSeconds);
            List<Packet> pk; lock (lb.Packets) pk = lb.Packets.ToList();
            long rate = lb.Fmt.Rate;
            float thr = onsetFraction * beepPeak * volumeScale;
            Console.WriteLine("{0} packets captured, onset threshold {1:0.0000} ({2:0.00} of the clip's peak at {3:0}% volume), clip onset at frame {4}", pk.Count, thr, onsetFraction, volumeScale * 100, beepOnsetFrame);
            var files = Directory.GetFiles(selftestDir, "beep-selftest-*.csv").Where(f => File.GetLastWriteTime(f) > started).OrderBy(f => File.GetLastWriteTime(f)).ToList();
            // The self-test's clock is Win32.GetTime() after Win32.SetDrift(settings.ClockDrift), taken once at its start (QPC q0):
            // T = q0/1e4 + (q - q0) / (1e4 * drift). Invert it to get raw QPC. q0 is estimated from the first target
            // (about 1.15 s after start); an error of a second in q0 moves the result by drift - 1 seconds, a few microseconds.
            double drift = toolDrift ?? ReadToolDrift();
            double firstTarget = files.SelectMany(f => File.ReadLines(f).Skip(1)).Select(l => double.Parse(l.Split(',')[2], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Min();
            double q0 = (firstTarget - 1150) * 1e4;
            Console.WriteLine("self-test clock drift correction {0:0.000000000} ({1:+0.00;-0.00} ppm), converted back to raw QPC", drift, (drift - 1) * 1e6);
            string outCsv = csvPath != null ? csvPath + ".crosscheck.csv" : Path.Combine(selftestDir, "beepprobe-crosscheck.csv");
            using StreamWriter w = new StreamWriter(outCsv);
            w.WriteLine("file,press,beep,target_ms,selftest_late_ms,stamp_late_ms,fit_late_ms,fit_rms_ms,flags");
            foreach (string file in files) {
                List<double> fitAll = new List<double>(), fitFinal = new List<double>(), stAll = new List<double>(), stFinal = new List<double>(), stampAll = new List<double>(), diff = new List<double>();
                int missing = 0;
                foreach (string line in File.ReadLines(file).Skip(1)) {
                    string[] f = line.Split(',');
                    int press = int.Parse(f[0]), beep = int.Parse(f[1]);
                    double targetMs = double.Parse(f[2], CultureInfo.InvariantCulture);
                    double? selfLate = f[4].Length > 0 ? double.Parse(f[4], CultureInfo.InvariantCulture) : (double?)null;
                    long target = (long)Math.Round(q0 + (targetMs * 1e4 - q0) * drift);
                    long expected = target + (long)(beepOnsetFrame * 1e7 / 48000);
                    long from = target - 1_000_000, to = target + 2_500_000;
                    long onsetStamp = -1, onsetDev = -1; string flags = "";
                    foreach (Packet p in pk) {
                        if (p.Qpc + p.Amp.Length * 10_000_000L / rate < from || p.Qpc > to) continue;
                        for (int k = 0; k < p.Amp.Length; k++) {
                            long tf = p.Qpc + k * 10_000_000L / rate;
                            if (tf < from || tf > to || p.Amp[k] <= thr) continue;
                            onsetStamp = tf; onsetDev = (long)p.DevPos + k;
                            if ((p.Flags & Native.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0) flags += "timestamp-error ";
                            if ((p.Flags & Native.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) flags += "discontinuity ";
                            break;
                        }
                        if (onsetStamp >= 0) break;
                    }
                    if (onsetStamp < 0) { missing++; w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:0.000},{4},,,,missing", Path.GetFileName(file), press, beep, targetMs, selfLate?.ToString("0.000", CultureInfo.InvariantCulture) ?? "")); continue; }
                    var lf = lb.ArrivalFitAround(onsetDev / (double)rate, 3.0);
                    double fitLate = Ms((long)(lf.a + lf.b * onsetDev) - expected), stampLate = Ms(onsetStamp - expected);
                    fitAll.Add(fitLate); stampAll.Add(stampLate); if (beep == 5) fitFinal.Add(fitLate);
                    if (selfLate is double sl) { stAll.Add(sl); if (beep == 5) stFinal.Add(sl); diff.Add(sl - fitLate); }
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:0.000},{4},{5:0.000},{6:0.000},{7:0.000},{8}", Path.GetFileName(file), press, beep, targetMs,
                        selfLate?.ToString("0.000", CultureInfo.InvariantCulture) ?? "", stampLate, fitLate, lf.rmsMs, flags.Trim()));
                }
                Console.WriteLine();
                Console.WriteLine("== {0}: {1} beeps, {2} not found by BeepProbe", Path.GetFileName(file), fitAll.Count + missing, missing);
                Report("final beeps, ARRIVAL-FIT clock       ", fitFinal);
                Report("final beeps, self-test (packet stamp)", stFinal);
                Report("all beeps,   ARRIVAL-FIT clock       ", fitAll);
                Report("all beeps,   self-test (packet stamp)", stAll);
                Report("all beeps,   BeepProbe packet stamp  ", stampAll);
                Report("self-test minus arrival-fit, per beep", diff);
                Console.WriteLine("estimated hit rate (self-test formula), arrival-fit: final {0:P2}, all {1:P2}", HitRate(fitFinal), HitRate(fitAll));
                Console.WriteLine("histogram of arrival-fit lateness, all beeps:");
                Histogram(fitAll);
            }
            Console.WriteLine();
            Console.WriteLine("engine clock fit over the whole run: {0}", lb.ClockDrift());
            Console.WriteLine("loopback packets flagged: discontinuity {0}, timestamp-error {1}", lb.Discontinuities, lb.TimestampErrors);
            Console.WriteLine("per-beep rows written to {0}", outCsv);
        }

        // ---------------- clockprobe mode: how the render stream's IAudioClock (position, qpc) pairs behave,
        // opened the way FRLG Starter Tool opens it (InitializeSharedAudioStream at the minimum period, fed 2 periods ahead)

        static void RunClockProbe(IMMDevice dev) {
            object o; Native.Check(dev.Activate(ref Native.IID_IAudioClient3, Native.CLSCTX_ALL, IntPtr.Zero, out o), "Activate(IAudioClient3) render");
            IAudioClient3 client = (IAudioClient3)o;
            IntPtr fp; Native.Check(client.GetMixFormat(out fp), "GetMixFormat");
            MixFormat fmt = MixFormat.Read(fp);
            uint dp, fnd, minp, maxp; Native.Check(client.GetSharedModeEnginePeriod(fp, out dp, out fnd, out minp, out maxp), "GetSharedModeEnginePeriod");
            Native.Check(client.InitializeSharedAudioStream(Native.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, minp, fp, IntPtr.Zero), "InitializeSharedAudioStream");
            uint bufFrames; client.GetBufferSize(out bufFrames);
            uint target = Math.Min(minp * 2, bufFrames);
            AutoResetEvent evt = new AutoResetEvent(false);
            Native.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            object svc; Native.Check(client.GetService(ref Native.IID_IAudioRenderClient, out svc), "GetService(IAudioRenderClient)");
            IAudioRenderClient render = (IAudioRenderClient)svc;
            Native.Check(client.GetService(ref Native.IID_IAudioClock, out svc), "GetService(IAudioClock)");
            IAudioClock clock = (IAudioClock)svc;
            ulong freq; clock.GetFrequency(out freq);
            uint ch; clock.GetCharacteristics(out ch);
            Console.WriteLine("clockprobe: {0}, period {1} frames, buffer {2} frames, fed {3} ahead, IAudioClock frequency {4} ({5:0.###} per frame), characteristics 0x{6:X}",
                fmt.Describe(), minp, bufFrames, target, freq, freq / (double)fmt.Rate, ch);
            IntPtr d; Native.Check(render.GetBuffer(target, out d), "GetBuffer prefill"); render.ReleaseBuffer(target, Native.AUDCLNT_BUFFERFLAGS_SILENT);
            Native.Check(client.Start(), "Start");
            uint idx; Native.AvSetMmThreadCharacteristicsW("Pro Audio", out idx);
            var first = new List<(long call, long qpc, double pos)>();          // first GetPosition of each pass, as FillScheduled does
            var polls = new List<(int pass, long call, long qpc, double pos)>(); // then every ~0.5 ms until 9 ms after the wake
            Stopwatch sw = Stopwatch.StartNew();
            int pass = 0;
            while (sw.Elapsed.TotalSeconds < maxSeconds) {
                if (!evt.WaitOne(200)) continue;
                long wake = Now();
                ulong p, q; Native.Check(clock.GetPosition(out p, out q), "GetPosition");
                first.Add((wake, (long)q, p / (double)freq));
                uint pad; client.GetCurrentPadding(out pad);
                if (pad < target) { uint n = target - pad; if (render.GetBuffer(n, out d) >= 0) render.ReleaseBuffer(n, Native.AUDCLNT_BUFFERFLAGS_SILENT); }
                if (pass % 10 == 0) {
                    while (Now() - wake < 90_000) {
                        WaitUntil(Now() + 5_000);
                        long c = Now(); clock.GetPosition(out p, out q);
                        polls.Add((pass, c, (long)q, p / (double)freq));
                    }
                }
                pass++;
            }
            client.Stop();
            // fit qpc = a + b * pos over the first readings, then look at the residuals
            var fit = FitLine(first.Select(r => (r.pos, (double)r.qpc)).ToList());
            Console.WriteLine("{0} passes; qpc-vs-position fit: {1:+0.0;-0.0} ppm vs nominal", first.Count, (fit.b / 1e7 - 1) * 1e6);
            Console.WriteLine("residual of the reported qpc against the fitted line, first reading of each pass (0.05 ms bins):");
            ResidualHistogram(first.Select(r => (r.qpc - (fit.a + fit.b * r.pos)) / 1e4).ToList());
            Console.WriteLine("call time minus fitted time of the reported position, first reading of each pass (0.25 ms bins):");
            ResidualHistogram(first.Select(r => (r.call - (fit.a + fit.b * r.pos)) / 1e4).ToList(), 0.25);
            int changes = 0, samePos = 0, qpcMoved = 0;
            for (int i = 1; i < polls.Count; i++) {
                if (polls[i].pass != polls[i - 1].pass) continue;
                if (polls[i].pos != polls[i - 1].pos) changes++; else { samePos++; if (polls[i].qpc != polls[i - 1].qpc) qpcMoved++; }
            }
            Console.WriteLine("within-pass polling ({0} readings over {1} passes, ~0.5 ms apart): position changed on {2}, stayed on {3} (of those, qpc still moved on {4})",
                polls.Count, polls.Select(r => r.pass).Distinct().Count(), changes, samePos, qpcMoved);
            Console.WriteLine("residual of polled readings against the same line (0.05 ms bins):");
            ResidualHistogram(polls.Select(r => (r.qpc - (fit.a + fit.b * r.pos)) / 1e4).ToList());
            string outCsv = csvPath != null ? csvPath + ".raw.csv" : "clockprobe.csv";
            File.WriteAllLines(outCsv, new[] { "kind,pass,call_100ns,qpc_100ns,pos_s" }
                .Concat(first.Select((r, i) => string.Format(CultureInfo.InvariantCulture, "first,{0},{1},{2},{3:0.000000000}", i, r.call, r.qpc, r.pos)))
                .Concat(polls.Select(r => string.Format(CultureInfo.InvariantCulture, "poll,{0},{1},{2},{3:0.000000000}", r.pass, r.call, r.qpc, r.pos))));
            Console.WriteLine("raw readings written to {0}", outCsv);
        }

        static (double a, double b) FitLine(List<(double x, double y)> s) {
            double x0 = s[0].x, y0 = s[0].y, sx = 0, sy = 0, sxx = 0, sxy = 0; int n = s.Count;
            foreach (var (x, y) in s) { double dx = x - x0, dy = y - y0; sx += dx; sy += dy; sxx += dx * dx; sxy += dx * dy; }
            double b = (n * sxy - sx * sy) / (n * sxx - sx * sx), c = (sy - b * sx) / n;
            return (y0 + c - b * x0, b);
        }

        static void ResidualHistogram(List<double> v, double bin = 0.05) {
            if (v.Count == 0) return;
            foreach (var g in v.GroupBy(x => Math.Floor(x / bin)).OrderBy(g => g.Key))
                Console.WriteLine("  {0,8:0.00} ms | {1} {2}", g.Key * bin, new string('#', Math.Min(100, (int)Math.Ceiling(g.Count() * 100.0 / v.Count))), g.Count());
        }

        // ---------------- record mode: capture until <selftest-dir>\stop exists (or --max-s), then list every sound onset
        // (first frame above --abs-thr after >= 20 ms below a tenth of it) with its QPC time on the arrival-fit clock

        static void RunRecord(Loopback lb) {
            string stopFile = Path.Combine(selftestDir, "stop");
            if (File.Exists(stopFile)) File.Delete(stopFile);
            File.WriteAllText(Path.Combine(selftestDir, "recording"), Now().ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("record mode: capturing until {0} exists (max {1} s)", stopFile, maxSeconds);
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSeconds && !File.Exists(stopFile)) Thread.Sleep(100);
            Thread.Sleep(300);
            List<Packet> pk; lock (lb.Packets) pk = lb.Packets.ToList();
            long rate = lb.Fmt.Rate;
            int quietNeed = (int)(rate / 50);
            int quiet = quietNeed; bool armed = true;
            var onsets = new List<(long devPos, long stamp, int packet, int frame)>();
            for (int i = 0; i < pk.Count; i++) {
                Packet p = pk[i];
                if ((p.Flags & Native.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) { quiet = 0; armed = false; }
                for (int k = 0; k < p.Amp.Length; k++) {
                    float a = p.Amp[k];
                    // arm after 20 ms below a tenth of the threshold; a soft attack between the two levels keeps it armed
                    if (a >= absThreshold) { if (armed) onsets.Add(((long)p.DevPos + k, p.Qpc + k * 10_000_000L / rate, i, k)); armed = false; quiet = 0; }
                    else if (a < absThreshold * 0.1f) { if (++quiet >= quietNeed) armed = true; }
                    else quiet = 0;
                }
            }
            string outCsv = csvPath != null ? csvPath + ".onsets.csv" : Path.Combine(selftestDir, "onsets.csv");
            using StreamWriter w = new StreamWriter(outCsv);
            w.WriteLine("n,fit_ms,stamp_ms,peak,loud_ms,gap_from_previous_ms");
            double prev = double.NaN; int n = 0;
            foreach (var o in onsets) {
                var lf = lb.ArrivalFitAround(o.devPos / (double)rate, 3.0);
                double fitMs = (lf.a + lf.b * o.devPos) / 1e4;
                // peak over the next 200 ms, and how long after the onset the sound last reaches 10% of that peak
                float peak = 0; int last = 0, walked = 0;
                for (int i = o.packet; i < pk.Count && walked < rate / 5; i++)
                    for (int k = i == o.packet ? o.frame : 0; k < pk[i].Amp.Length && walked < rate / 5; k++, walked++) if (pk[i].Amp[k] > peak) peak = pk[i].Amp[k];
                walked = 0;
                for (int i = o.packet; i < pk.Count && walked < rate / 5; i++)
                    for (int k = i == o.packet ? o.frame : 0; k < pk[i].Amp.Length && walked < rate / 5; k++, walked++) if (pk[i].Amp[k] > peak * 0.1f) last = walked;
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:0.000},{2:0.000},{3:0.0000},{4:0.0},{5:0.000}", n++, fitMs, o.stamp / 1e4, peak, last * 1000.0 / rate, fitMs - prev));
                prev = fitMs;
            }
            Console.WriteLine("{0} onsets written to {1}", onsets.Count, outCsv);
            // loudest frame per 5 ms of stamp time, only for bins that are not silent
            string envCsv = Path.Combine(selftestDir, "envelope.csv");
            var bins = new SortedDictionary<long, float>();
            foreach (Packet p in pk) for (int k = 0; k < p.Amp.Length; k++) {
                if (p.Amp[k] < 0.0005f) continue;
                long b = (p.Qpc + k * 10_000_000L / rate) / 50_000;
                bins[b] = bins.TryGetValue(b, out float m) ? Math.Max(m, p.Amp[k]) : p.Amp[k];
            }
            File.WriteAllLines(envCsv, new[] { "stamp_ms_bin_start,max_amp" }.Concat(bins.Select(kv => string.Format(CultureInfo.InvariantCulture, "{0},{1:0.00000}", kv.Key * 5, kv.Value))));
            Console.WriteLine("{0} non-silent 5 ms bins written to {1}", bins.Count, envCsv);
        }

        static float absThreshold = 0.02f;
        static string deviceId;

        static double HitRate(List<double> v) {
            if (v.Count == 0) return double.NaN;
            List<double> s = v.OrderBy(x => x).ToList();
            double at = (s.Count - 1) * 0.5; int lo = (int)at;
            double median = s[lo] + (s[Math.Min(lo + 1, s.Count - 1)] - s[lo]) * (at - lo);
            return s.Average(e => Math.Max(0, 1 - Math.Abs(e - median) / 16.743));
        }

        // a shared-mode render stream that plays silence for the life of the process
        static void StartSilentKeepalive(IMMDevice dev) {
            object o; Native.Check(dev.Activate(ref Native.IID_IAudioClient3, Native.CLSCTX_ALL, IntPtr.Zero, out o), "Activate keepalive");
            IAudioClient3 c = (IAudioClient3)o;
            IntPtr fp; Native.Check(c.GetMixFormat(out fp), "GetMixFormat keepalive");
            Native.Check(c.Initialize(0, Native.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, fp, IntPtr.Zero), "Initialize keepalive");
            uint buf; c.GetBufferSize(out buf);
            AutoResetEvent evt = new AutoResetEvent(false);
            Native.Check(c.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle keepalive");
            object svc; Native.Check(c.GetService(ref Native.IID_IAudioRenderClient, out svc), "GetService keepalive");
            IAudioRenderClient r = (IAudioRenderClient)svc;
            IntPtr d; if (r.GetBuffer(buf, out d) >= 0) r.ReleaseBuffer(buf, Native.AUDCLNT_BUFFERFLAGS_SILENT);
            Native.Check(c.Start(), "Start keepalive");
            new Thread(() => {
                while (true) {
                    evt.WaitOne(200);
                    uint pad; if (c.GetCurrentPadding(out pad) < 0) continue;
                    uint n = buf - pad; if (n == 0) continue;
                    IntPtr p; if (r.GetBuffer(n, out p) >= 0) r.ReleaseBuffer(n, Native.AUDCLNT_BUFFERFLAGS_SILENT);
                }
            }) { IsBackground = true, Name = "keepalive" }.Start();
            Console.WriteLine("silent keepalive render stream started ({0} frame buffer)", buf);
        }

        // ---------------- helpers

        static uint DescribeClient(IMMDevice dev, string label) {
            object o; Native.Check(dev.Activate(ref Native.IID_IAudioClient3, Native.CLSCTX_ALL, IntPtr.Zero, out o), "Activate(IAudioClient3)");
            IAudioClient3 c = (IAudioClient3)o;
            IntPtr fp; Native.Check(c.GetMixFormat(out fp), "GetMixFormat");
            MixFormat f = MixFormat.Read(fp);
            long defP, minP; c.GetDevicePeriod(out defP, out minP);
            uint dp, fnd, mn, mx; int hr = c.GetSharedModeEnginePeriod(fp, out dp, out fnd, out mn, out mx);
            uint cur; IntPtr cf; int hr2 = c.GetCurrentSharedModeEnginePeriod(out cf, out cur); if (cf != IntPtr.Zero) Native.CoTaskMemFree(cf);
            Console.WriteLine("  [{0}] mix {1}; GetDevicePeriod default {2:0.00} ms min {3:0.00} ms; GetSharedModeEnginePeriod default {4} fund {5} min {6} max {7} frames (hr 0x{8:X}); current engine period {9} frames = {10:0.00} ms (hr 0x{11:X})",
                label, f.Describe(), defP / 1e4, minP / 1e4, dp, fnd, mn, mx, hr, cur, cur * 1000.0 / f.Rate, hr2);
            Native.CoTaskMemFree(fp);
            Marshal.ReleaseComObject(c);
            return hr >= 0 ? mn : 0;
        }

        static string DevId(IMMDevice d) { string id; d.GetId(out id); return id; }
        static string DevName(IMMDevice d) {
            IPropertyStore ps; if (d.OpenPropertyStore(0, out ps) < 0) return "?";
            PropVariant pv; PropertyKey k = Native.PKEY_Device_FriendlyName;
            if (ps.GetValue(ref k, out pv) < 0) return "?";
            string s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : "?";
            Native.PropVariantClear(ref pv);
            return s + "  {" + DevId(d) + "}";
        }

        static void LoadBeep() {
            int rate = 48000, ch = 2;
            if (beepPath != null) {
                byte[] b = File.ReadAllBytes(beepPath);
                int p = 12; byte[] data = null;
                while (p + 8 <= b.Length) {
                    string id = Encoding.ASCII.GetString(b, p, 4); int sz = BitConverter.ToInt32(b, p + 4);
                    if (id == "fmt ") { if (BitConverter.ToUInt16(b, p + 8) != 1 || BitConverter.ToUInt16(b, p + 22) != 16) throw new Exception("beep must be PCM16"); ch = BitConverter.ToUInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); }
                    else if (id == "data") { data = new byte[sz]; Array.Copy(b, p + 8, data, 0, sz); }
                    p += 8 + sz + (sz & 1);
                }
                if (rate != 48000 || ch != 2) throw new Exception("beep must be 48 kHz stereo like FlowTimer's");
                beepPcm = data;
            } else {
                // synthetic: 40 ms 2 kHz burst, hard onset at sample 0
                int n = 48000 * 40 / 1000; beepPcm = new byte[n * 4];
                for (int i = 0; i < n; i++) { short s = (short)(Math.Cos(2 * Math.PI * 2000 * i / 48000.0) * 20000 * (1 - i / (double)n)); beepPcm[i * 4] = beepPcm[i * 4 + 2] = (byte)s; beepPcm[i * 4 + 1] = beepPcm[i * 4 + 3] = (byte)(s >> 8); }
            }
            beepFrames = beepPcm.Length / 4;
            beepPeak = 0; for (int i = 0; i < beepFrames * 2; i++) { float a = Math.Abs(BitConverter.ToInt16(beepPcm, i * 2) / 32768f); if (a > beepPeak) beepPeak = a; }
            beepOnsetFrame = 0; for (int i = 0; i < beepFrames; i++) { float a = Math.Max(Math.Abs(BitConverter.ToInt16(beepPcm, i * 4) / 32768f), Math.Abs(BitConverter.ToInt16(beepPcm, i * 4 + 2) / 32768f)); if (a > onsetFraction * beepPeak) { beepOnsetFrame = i; break; } }
        }

        static float[] BeepAsFloat(int rate, int channels) {
            // nearest-frame resample if the device rate differs (only the timing matters here)
            int outFrames = (int)((long)beepFrames * rate / 48000);
            float[] f = new float[outFrames * channels];
            for (int i = 0; i < outFrames; i++) {
                int src = (int)((long)i * 48000 / rate);
                for (int c = 0; c < channels; c++) f[i * channels + c] = BitConverter.ToInt16(beepPcm, src * 4 + (c < 2 ? c * 2 : 0)) / 32768f;
            }
            return f;
        }

        // FlowTimer.UpdatePCM: buffer sized for the largest offset (plus beep), beep copied at offset*rate frames.
        // Built in the device's obtained format when SDL was opened with ALLOW_ANY_CHANGE.
        static byte[] BuildPcm(double beepAtMs, int rate, int channels, bool isFloat, double bufferSeconds = -1) {
            int bytesPerFrame = channels * (isFloat ? 4 : 2);
            float[] bf = (rate == 48000 && channels == 2 && !isFloat) ? null : BeepAsFloat(rate, channels);
            int beepBytes = bf == null ? beepPcm.Length : (bf.Length / channels) * bytesPerFrame;
            double maxOffsetMs = bufferSeconds > 0 ? Math.Max(bufferSeconds * 1000, beepAtMs) : beepAtMs;
            byte[] pcm = new byte[((int)Math.Ceiling(maxOffsetMs / 1000.0 * rate)) * bytesPerFrame + beepBytes];
            int dest = (int)(beepAtMs / 1000.0 * rate) * bytesPerFrame;
            if (bf == null) Array.Copy(beepPcm, 0, pcm, dest, beepPcm.Length);
            else fixed (byte* p = pcm) FillBeep((IntPtr)(p + dest), 0, bf.Length / channels, 0, bf, channels, isFloat);
            return pcm;
        }

        static void RandomGap(Random rng) {
            // uniform gap so the press phase relative to the engine period is sampled evenly
            double ms = rng.NextDouble() * gapMaxMs;
            WaitUntil(Now() + (long)(ms * 1e4));
        }

        static void WaitUntil(long t) {
            long remain;
            while ((remain = t - Now()) > 0) {
                if (remain > 30_000) Thread.Sleep(1); else Thread.SpinWait(50);
            }
        }

        static long FindOnset(Loopback lb, long t0, long expected, out float peak, out string note) { long dp; return FindOnset(lb, t0, expected, out peak, out note, out dp); }

        // first captured frame above threshold at or after t0; returns its QPC time (100 ns) or -1,
        // plus the loopback stream's device position of that frame (engine timeline)
        static long FindOnset(Loopback lb, long t0, long expected, out float peak, out string note, out long onsetDevPos) {
            Thread.Sleep(60);   // let the last loopback packets arrive
            List<Packet> pk = lb.Take(t0 - 400_000, expected + 4_000_000);
            peak = 0; note = ""; onsetDevPos = -1;
            long rate = lb.Fmt.Rate;
            foreach (Packet p in pk) for (int f = 0; f < p.Amp.Length; f++) { long tf = p.Qpc + f * 10_000_000 / rate; if (tf >= t0 && p.Amp[f] > peak) peak = p.Amp[f]; }
            if (peak < 0.001f) { note = "no signal in window (" + pk.Count + " packets)"; return -1; }
            // threshold against the beep's known peak, not the window's, so background audio cannot trip it
            float thr = onsetFraction * beepPeak;
            if (peak < thr) { note = string.Format(CultureInfo.InvariantCulture, "window peak {0:0.000} below beep threshold {1:0.000}", peak, thr); return -1; }
            foreach (Packet p in pk) {
                if ((p.Flags & Native.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0) note += " [timestamp-error]";
                if ((p.Flags & Native.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) note += " [discontinuity]";
                for (int f = 0; f < p.Amp.Length; f++) {
                    long tf = p.Qpc + f * 10_000_000 / rate;
                    if (tf >= t0 && p.Amp[f] > thr) {
                        onsetDevPos = (long)p.DevPos + f;
                        // independent estimate: packet arrival time minus the frames after the onset in that packet
                        note += string.Format(CultureInfo.InvariantCulture, " [pkt {0} fr, stamp-to-arrival {1:0.00} ms]", p.Amp.Length, Ms(p.Arrival - p.Qpc));
                        return tf;
                    }
                }
            }
            note = "no onset above threshold";
            return -1;
        }

        static void Report(string label, List<double> v) {
            if (v.Count == 0) return;
            List<double> s = v.OrderBy(x => x).ToList();
            double mean = s.Average(), sd = Math.Sqrt(s.Sum(x => (x - mean) * (x - mean)) / s.Count);
            Func<double, double> pct = q => s[(int)Math.Min(s.Count - 1, Math.Round(q * (s.Count - 1)))];
            Console.WriteLine("{0}: n={1} min {2,7:0.00} p5 {3,7:0.00} median {4,7:0.00} mean {5,7:0.00} p95 {6,7:0.00} max {7,7:0.00} ms  spread {8:0.00} ms = {9:0.00} GBA frames, sd {10:0.00} ms",
                label, s.Count, s[0], pct(0.05), pct(0.5), mean, pct(0.95), s[s.Count - 1], s[s.Count - 1] - s[0], (s[s.Count - 1] - s[0]) / 16.7427, sd);
        }

        static void Histogram(List<double> v) {
            if (v.Count == 0) return;
            int lo = (int)Math.Floor(v.Min()), hi = (int)Math.Ceiling(v.Max());
            if (hi - lo > 80) return;
            Console.WriteLine("histogram (1 ms bins):");
            for (int b = lo; b < hi; b++) { int n = v.Count(x => x >= b && x < b + 1); if (n > 0) Console.WriteLine("  {0,4} ms | {1} {2}", b, new string('#', n), n); }
        }
    }
}
