namespace Darkmount.Sensors;

/// <summary>
/// Combines HWiNFO, MSI Afterburner, RTSS and Windows into one <see cref="Snapshot"/> per call.
/// Per field the first source with a value wins: HWiNFO, then Afterburner, then OverMount's own CPU sensor
/// (<see cref="CpuSensorLink"/>, CPU temperature and watts), then Windows' own sensors
/// (<see cref="WindowsSensors"/>: CPU load and watts, GPU load/temperature/VRAM, NVIDIA watts), so a PC without any monitoring
/// app still shows real numbers.
/// </summary>
public sealed class SensorHub : IDisposable
{
    public const string HintHwInfo = "HWiNFO: enable 'Shared Memory Support' in HWiNFO settings (optional)";
    public const string HintAfterburner = "MSI Afterburner (or HWiNFO) adds CPU temperature and watts";
    public const string HintRtss = "Start RivaTuner Statistics Server for game detection";
    public const string HintFpsLow = "Afterburner: enable 'Framerate 1% low' in Monitoring settings";
    public const string HintCpuTemp = "No CPU temperature: tray icon → Set up sensor apps… (OverMount's CPU sensor)";

    /// <summary>How long a game stays detected while it is not in the foreground (alt-tab debounce).</summary>
    private const uint GameDebounceMs = 5000;

    private readonly SensorOptions _options;
    private readonly Func<double?> _ramTotalMb;
    private readonly Func<double?> _ramUsedMb;
    private readonly Func<double?> _vramTotalMb;
    private readonly Func<Snapshot?> _windows;
    private readonly Func<CpuSensorLink.Reading?> _cpuSensor;
    private readonly WindowsSensors? _windowsSensors;
    private readonly Lock _lock = new();

    private int _lastGamePid;
    private uint _lastGameTicks;

    readonly FrameTimeSampler? _frames;

    public SensorHub(SensorOptions options)
        : this(options, () => SystemInfo.RamTotalMb, () => SystemInfo.RamUsedMb, () => SystemInfo.VramTotalMb, windows: null)
    {
        _frames = new FrameTimeSampler();
        _windowsSensors = new WindowsSensors();
        _windows = _windowsSensors.Sample;
        _cpuSensor = CpuSensorLink.TryRead;
    }

    /// <param name="windows">Windows' own sensors (tests pass none, so only the given blobs count).</param>
    /// <param name="cpuSensor">OverMount's own CPU sensor helper (<see cref="CpuSensorLink"/>).</param>
    internal SensorHub(SensorOptions options, Func<double?> ramTotalMb, Func<double?> ramUsedMb, Func<double?> vramTotalMb,
        Func<Snapshot?>? windows = null, Func<CpuSensorLink.Reading?>? cpuSensor = null)
    {
        _options = options;
        _ramTotalMb = ramTotalMb;
        _ramUsedMb = ramUsedMb;
        _vramTotalMb = vramTotalMb;
        _windows = windows ?? (() => null);
        _cpuSensor = cpuSensor ?? (() => null);
    }

    /// <summary>Reads all sources now. Never throws.</summary>
    public Snapshot Sample() => Sample(
        SharedMemory.TryRead(HwInfoReader.MappingName),
        SharedMemory.TryRead(MahmReader.MappingName),
        SharedMemory.TryRead(RtssReader.MappingName),
        SystemInfo.ForegroundProcessId(),
        (uint)Environment.TickCount);

    internal Snapshot Sample(byte[]? hw, byte[]? mahm, byte[]? rtss, int foregroundPid, uint nowTicks)
    {
        var hints = new List<string>();

        Snapshot? h = hw is null ? null : HwInfoReader.Parse(hw, _options);
        if (h is null) hints.Add(HintHwInfo);

        MahmData? mahmData = mahm is null ? null : MahmReader.Parse(mahm);
        Snapshot? m = mahmData?.ToSnapshot(_options);
        if (m is null) hints.Add(HintAfterburner);

        IReadOnlyList<RtssApp>? apps = rtss is null ? null : RtssReader.Parse(rtss, nowTicks);
        if (apps is null) hints.Add(HintRtss);

        RtssApp? game;
        lock (_lock)
            game = DetectGame(apps ?? [], foregroundPid, nowTicks);

        double? fps = null, fpsLow = null;
        string fpsLowLabel = "1% low";
        if (_frames is not null) _frames.TargetPid = game?.Pid ?? 0;
        if (game is not null)
        {
            fps = m?.Fps ?? (game.Fps > 0 ? game.Fps : null);
            // Our own 1% low from RivaTuner frame times (works without any Afterburner setup); Afterburner's as fallback.
            if (_frames?.Lows() is { Low1: { } own }) fpsLow = own;
            else
            {
                fpsLow = m?.FpsLow;
                if (m is not null) fpsLowLabel = m.FpsLowLabel;
                // Only when nothing can provide a low (e.g. the measurement is still warming up).
                if (fpsLow is null && mahmData is not null && !mahmData.HasFpsLowEntries) hints.Add(HintFpsLow);
            }
        }

        Snapshot? w;
        try { w = _windows(); }
        catch (Exception) { w = null; } // Windows' counters are a bonus; never let them break a sample
        CpuSensorLink.Reading? helper;
        try { helper = _cpuSensor(); }
        catch (Exception) { helper = null; }

        double? cpuTemp = h?.CpuTemp ?? m?.CpuTemp ?? helper?.CpuTemp;
        if (cpuTemp is null) hints.Insert(0, HintCpuTemp); // the most useful thing to fix, so it's first (tray tooltip)

        return new Snapshot
        {
            CpuTemp = cpuTemp,
            CpuPower = h?.CpuPower ?? m?.CpuPower ?? helper?.CpuPower ?? w?.CpuPower,
            CpuLoad = h?.CpuLoad ?? m?.CpuLoad ?? w?.CpuLoad,
            GpuTemp = h?.GpuTemp ?? m?.GpuTemp ?? w?.GpuTemp,
            GpuPower = h?.GpuPower ?? m?.GpuPower ?? w?.GpuPower,
            GpuLoad = h?.GpuLoad ?? m?.GpuLoad ?? w?.GpuLoad,
            RamUsedMb = h?.RamUsedMb ?? m?.RamUsedMb ?? _ramUsedMb(),
            RamTotalMb = _ramTotalMb(),
            VramUsedMb = h?.VramUsedMb ?? m?.VramUsedMb ?? w?.VramUsedMb,
            VramTotalMb = w?.VramTotalMb ?? _vramTotalMb(),
            Fps = fps,
            FpsLow = fpsLow,
            FpsLowLabel = fpsLowLabel,
            GameName = game?.ExeName,
            Hints = hints,
        };
    }

    private RtssApp? DetectGame(IReadOnlyList<RtssApp> apps, int foregroundPid, uint nowTicks)
    {
        if (foregroundPid != 0)
        {
            var fg = apps.FirstOrDefault(a => a.Pid == foregroundPid && IsRunningGame(a));
            if (fg is not null)
            {
                _lastGamePid = fg.Pid;
                _lastGameTicks = nowTicks;
                return fg;
            }
        }

        // Debounce alt-tab: keep the last foreground game while it still renders, for a few seconds.
        if (_lastGamePid != 0 && unchecked(nowTicks - _lastGameTicks) < GameDebounceMs)
        {
            var last = apps.FirstOrDefault(a => a.Pid == _lastGamePid && IsRunningGame(a));
            if (last is not null) return last;
        }

        _lastGamePid = 0;
        return null;
    }

    private bool IsRunningGame(RtssApp app) =>
        app.Fps >= _options.GameMinFps
        && app.AgeMs < (uint)Math.Max(0, _options.GameStaleMs)
        && !_options.GameExcludes.Contains(app.ExeName, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        // Shared memory is opened and closed per sample; the frame-time sampler keeps a thread, Windows' sensors handles.
        _frames?.Dispose();
        _windowsSensors?.Dispose();
    }
}
