using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Darkmount.Sensors;

/// <summary>
/// Sensors Windows provides without any monitoring app or admin rights, used for whatever HWiNFO and Afterburner don't
/// supply: CPU load (system times), GPU load and VRAM (the "GPU Engine" / "GPU Adapter Memory" performance counters
/// Task Manager uses, any vendor), GPU temperature (the display driver's performance data, as in Task Manager), and on
/// NVIDIA cards temperature, load, watts and VRAM straight from the driver's NVML library, and CPU package watts from
/// the processor's own energy counters (RAPL) that Windows publishes as "\Energy Meter" (AMD Ryzen and Intel).
/// CPU temperature needs a kernel driver, so it stays with HWiNFO / Afterburner.
/// </summary>
public sealed partial class WindowsSensors : IDisposable
{
    readonly Lock _gate = new();
    readonly Nvml? _nvml = Nvml.TryOpen();
    GpuCounters? _counters;
    bool _countersFailed;
    CpuPowerCounter? _cpuPower;
    bool _cpuPowerFailed;
    ulong _lastIdle, _lastKernel, _lastUser;

    /// <summary>A partial snapshot (null fields = not available). Never throws.</summary>
    public Snapshot Sample()
    {
        lock (_gate)
        {
            double? cpuLoad = CpuLoad(), cpuPower = null;
            if (_cpuPower is null && !_cpuPowerFailed)
            {
                _cpuPower = CpuPowerCounter.TryOpen(); // read from the next sample on (the counter needs two collects)
                _cpuPowerFailed = _cpuPower is null;
            }
            else cpuPower = _cpuPower?.ReadWatts();
            double? gpuTemp = null, gpuLoad = null, gpuPower = null, vramUsed = null, vramTotal = null;

            if (_nvml?.Read() is { } n)
                (gpuTemp, gpuLoad, gpuPower, vramUsed, vramTotal) = (n.Temp, n.Load, n.PowerW, n.VramUsedMb, n.VramTotalMb);

            if (gpuLoad is null || vramUsed is null || gpuTemp is null)
            {
                if (_counters is null && !_countersFailed)
                {
                    // Opening primes the rate counters; they need real time between two collects, so read from the next sample on.
                    _counters = GpuCounters.TryOpen();
                    _countersFailed = _counters is null;
                }
                else if (_counters?.Read() is { } c)
                {
                    gpuLoad ??= c.Load;
                    vramUsed ??= c.DedicatedMb;
                    gpuTemp ??= c.Luid is { } luid ? AdapterTemperature(luid) : null;
                }
            }

            return new Snapshot
            {
                CpuLoad = cpuLoad, CpuPower = cpuPower, GpuTemp = gpuTemp, GpuLoad = gpuLoad, GpuPower = gpuPower, VramUsedMb = vramUsed, VramTotalMb = vramTotal,
            };
        }
    }

    // ---------------------------------------------------------------- CPU load

    double? CpuLoad()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return null;
        ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
        ulong dIdle = idle - _lastIdle, dTotal = kernel - _lastKernel + (user - _lastUser); // kernel time includes idle
        bool first = _lastKernel == 0;
        (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);
        if (first || dTotal == 0) return null;
        return Math.Clamp(100.0 * (dTotal - Math.Min(dIdle, dTotal)) / dTotal, 0, 100);
    }

    // ---------------------------------------------------------------- GPU temperature (display driver perf data)

    /// <summary>The adapter's temperature from D3DKMT perf data (Windows 10 2004+ with a WDDM 2.7 driver), or null.</summary>
    static double? AdapterTemperature(long luid)
    {
        try
        {
            var enumArgs = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0) return null;
            var infos = new D3DKMT_ADAPTERINFO[enumArgs.NumAdapters];
            var handle = GCHandle.Alloc(infos, GCHandleType.Pinned);
            try
            {
                enumArgs.pAdapters = handle.AddrOfPinnedObject();
                if (D3DKMTEnumAdapters2(ref enumArgs) != 0) return null;
                double? temp = null;
                for (int i = 0; i < enumArgs.NumAdapters; i++)
                {
                    var info = infos[i];
                    if (info.AdapterLuid == luid && temp is null) temp = QueryTemperature(info.hAdapter);
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
                return temp;
            }
            finally { handle.Free(); }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    static double? QueryTemperature(uint adapter)
    {
        var perf = new D3DKMT_ADAPTER_PERFDATA();
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>());
        try
        {
            Marshal.StructureToPtr(perf, buffer, false);
            var query = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = adapter, Type = KmtQaiAdapterPerfData, pPrivateDriverData = buffer,
                PrivateDriverDataSize = (uint)Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>(),
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;
            perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buffer);
            double celsius = perf.Temperature / 10.0; // deci-degrees
            return celsius is > 1 and < 150 ? celsius : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---------------------------------------------------------------- GPU counters (any vendor)

    internal readonly record struct CounterRead(double? Load, double? DedicatedMb, long? Luid);

    /// <summary>"\GPU Engine(*)\Utilization Percentage" and "\GPU Adapter Memory(*)\Dedicated Usage" via PDH.</summary>
    sealed class GpuCounters : IDisposable
    {
        readonly nint _query, _engine, _memory;

        GpuCounters(nint query, nint engine, nint memory) => (_query, _engine, _memory) = (query, engine, memory);

        public static GpuCounters? TryOpen()
        {
            try
            {
                if (PdhOpenQueryW(null, 0, out var query) != 0) return null;
                if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out var engine) != 0
                    || PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out var memory) != 0)
                {
                    PdhCloseQuery(query);
                    return null;
                }
                PdhCollectQueryData(query); // the first sample only primes the rate counters
                return new GpuCounters(query, engine, memory);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }

        public CounterRead? Read()
        {
            if (PdhCollectQueryData(_query) != 0) return null;
            var memory = Items(_memory);
            var engines = Items(_engine);
            return Combine(engines, memory);
        }

        internal static List<(string Name, double Value)> Items(nint counter)
        {
            uint size = 0;
            var result = new List<(string, double)>();
            uint status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, 0);
            if (status != PdhMoreData || size == 0) return result;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out uint count, buffer) != 0) return result;
                int itemSize = IntPtr.Size + 16; // name pointer, CStatus (+padding), double
                for (int i = 0; i < count; i++)
                {
                    nint item = buffer + i * itemSize;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    int cstatus = Marshal.ReadInt32(item + IntPtr.Size);
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                    if (cstatus is 0 or 1 && double.IsFinite(value)) result.Add((name, value));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return result;
        }

        public void Dispose() => PdhCloseQuery(_query);
    }

    // ---------------------------------------------------------------- CPU package power (RAPL energy counters)

    /// <summary>"\Energy Meter(*)\Power": milliwatts per RAPL domain; the "_PKG" instances are the CPU package(s).</summary>
    sealed class CpuPowerCounter : IDisposable
    {
        readonly nint _query, _power;

        CpuPowerCounter(nint query, nint power) => (_query, _power) = (query, power);

        public static CpuPowerCounter? TryOpen()
        {
            try
            {
                if (PdhOpenQueryW(null, 0, out var query) != 0) return null;
                if (PdhAddEnglishCounterW(query, @"\Energy Meter(*)\Power", 0, out var power) != 0)
                {
                    PdhCloseQuery(query);
                    return null;
                }
                PdhCollectQueryData(query);
                return new CpuPowerCounter(query, power);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }

        public double? ReadWatts() => PdhCollectQueryData(_query) != 0 ? null : PackageWatts(GpuCounters.Items(_power));

        public void Dispose() => PdhCloseQuery(_query);
    }

    /// <summary>
    /// CPU package watts from "\Energy Meter(*)\Power" items (milliwatts): the sum of the "…_PKG" instances (one per CPU
    /// socket); null without any, or when the total is implausible (0 W while running, or above 1 kW). Pure, for tests.
    /// </summary>
    internal static double? PackageWatts(IEnumerable<(string Name, double Value)> items)
    {
        var packages = items.Where(i => i.Name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)).ToList();
        if (packages.Count == 0) return null;
        double watts = packages.Sum(i => i.Value) / 1000.0;
        return watts is > 0.5 and < 1000 ? watts : null;
    }

    /// <summary>
    /// The busiest adapter's load like Task Manager (per engine type the sum over processes; the adapter's load is the
    /// busiest engine type) plus its dedicated memory. The adapter using the most dedicated memory counts as the
    /// gaming GPU (an iGPU uses shared memory). Pure, for tests.
    /// </summary>
    internal static CounterRead? Combine(IEnumerable<(string Name, double Value)> engines, IEnumerable<(string Name, double Value)> memory)
    {
        var memoryByLuid = new Dictionary<long, double>();
        foreach (var (name, value) in memory)
            if (ParseLuid(name) is { } luid) memoryByLuid[luid] = memoryByLuid.GetValueOrDefault(luid) + value;

        var engineSums = new Dictionary<(long Luid, string Type), double>();
        foreach (var (name, value) in engines)
        {
            if (ParseLuid(name) is not { } luid) continue;
            var type = EngineTypeRegex().Match(name) is { Success: true } m ? m.Groups[1].Value : "?";
            engineSums[(luid, type)] = engineSums.GetValueOrDefault((luid, type)) + value;
        }
        var loadByLuid = engineSums.GroupBy(kv => kv.Key.Luid).ToDictionary(g => g.Key, g => g.Max(kv => kv.Value));

        long? chosen = memoryByLuid.Count > 0 ? memoryByLuid.MaxBy(kv => kv.Value).Key
            : loadByLuid.Count > 0 ? loadByLuid.MaxBy(kv => kv.Value).Key : null;
        if (chosen is not { } c) return null;
        double? load = loadByLuid.TryGetValue(c, out var l) ? Math.Clamp(l, 0, 100) : null;
        double? mb = memoryByLuid.TryGetValue(c, out var bytes) ? bytes / 1048576.0 : null;
        return new CounterRead(load, mb, c);
    }

    /// <summary>"pid_4_luid_0x00000000_0x0000C6F3_phys_0_eng_3_engtype_3D" → the adapter LUID as a 64-bit value.</summary>
    internal static long? ParseLuid(string instance)
    {
        var m = LuidRegex().Match(instance);
        if (!m.Success) return null;
        long high = long.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        long low = long.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (high << 32) | (low & 0xFFFFFFFF);
    }

    [GeneratedRegex(@"luid_0x([0-9a-fA-F]{8})_0x([0-9a-fA-F]{8})")]
    private static partial Regex LuidRegex();

    [GeneratedRegex(@"engtype_([A-Za-z0-9 ]+)$")]
    private static partial Regex EngineTypeRegex();

    // ---------------------------------------------------------------- NVIDIA (NVML)

    internal readonly record struct NvmlRead(double? Temp, double? Load, double? PowerW, double? VramUsedMb, double? VramTotalMb);

    /// <summary>nvml.dll, installed with every NVIDIA driver; read-only queries, no admin rights needed.</summary>
    sealed class Nvml : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CountFn(out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleFn(uint index, out nint device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TempFn(nint device, int sensor, out uint celsius);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PowerFn(nint device, out uint milliwatts);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UtilFn(nint device, out NvmlUtilization util);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int MemoryFn(nint device, out NvmlMemory memory);

        [StructLayout(LayoutKind.Sequential)] struct NvmlUtilization { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] struct NvmlMemory { public ulong Total, Free, Used; }

        readonly nint _lib;
        readonly InitFn _shutdown;
        readonly CountFn _count;
        readonly HandleFn _handle;
        readonly TempFn _temp;
        readonly PowerFn _power;
        readonly UtilFn _util;
        readonly MemoryFn _memory;

        Nvml(nint lib)
        {
            _lib = lib;
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            _shutdown = Fn<InitFn>("nvmlShutdown");
            _count = Fn<CountFn>("nvmlDeviceGetCount_v2");
            _handle = Fn<HandleFn>("nvmlDeviceGetHandleByIndex_v2");
            _temp = Fn<TempFn>("nvmlDeviceGetTemperature");
            _power = Fn<PowerFn>("nvmlDeviceGetPowerUsage");
            _util = Fn<UtilFn>("nvmlDeviceGetUtilizationRates");
            _memory = Fn<MemoryFn>("nvmlDeviceGetMemoryInfo");
        }

        public static Nvml? TryOpen()
        {
            if (!OperatingSystem.IsWindows()) return null;
            string[] paths =
            [
                "nvml.dll",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
            ];
            foreach (var path in paths)
            {
                if (!NativeLibrary.TryLoad(path, typeof(Nvml).Assembly, DllImportSearchPath.System32, out var lib)
                    && !NativeLibrary.TryLoad(path, out lib)) continue;
                try
                {
                    var init = Marshal.GetDelegateForFunctionPointer<InitFn>(NativeLibrary.GetExport(lib, "nvmlInit_v2"));
                    if (init() != 0) { NativeLibrary.Free(lib); continue; }
                    return new Nvml(lib);
                }
                catch (Exception e) when (e is EntryPointNotFoundException or MarshalDirectiveException)
                {
                    NativeLibrary.Free(lib);
                }
            }
            return null;
        }

        /// <summary>The NVIDIA GPU drawing the most power (the one games run on), or null.</summary>
        public NvmlRead? Read()
        {
            try
            {
                if (_count(out uint count) != 0 || count == 0) return null;
                NvmlRead? best = null;
                for (uint i = 0; i < count; i++)
                {
                    if (_handle(i, out var device) != 0) continue;
                    double? temp = _temp(device, 0, out uint c) == 0 ? c : null;
                    double? power = _power(device, out uint mw) == 0 ? mw / 1000.0 : null;
                    double? load = _util(device, out var u) == 0 ? u.Gpu : null;
                    bool hasMemory = _memory(device, out var m) == 0;
                    double? used = hasMemory ? m.Used / 1048576.0 : null, total = hasMemory ? m.Total / 1048576.0 : null;
                    var read = new NvmlRead(temp, load, power, used, total);
                    if (best is null || (power ?? 0) > (best.Value.PowerW ?? 0)) best = read;
                }
                return best;
            }
            // SEH errors reported by the library are catchable; a real access violation isn't on .NET (it ends the
            // process), which is why only the documented NVML calls with their documented structs are used.
            catch (SEHException) { return null; }
        }

        public void Dispose()
        {
            try { _shutdown(); } catch (Exception e) when (e is SEHException) { }
            NativeLibrary.Free(_lib);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _counters?.Dispose();
            _cpuPower?.Dispose();
            _nvml?.Dispose();
        }
    }

    // ---------------------------------------------------------------- interop

    const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000, PdhMoreData = 0x800007D2;
    const int KmtQaiAdapterPerfData = 62;

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
        public readonly long AdapterLuid => ((long)LuidHigh << 32) | LuidLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public nint pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public nint pPrivateDriverData; public uint PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(nint query);
}
