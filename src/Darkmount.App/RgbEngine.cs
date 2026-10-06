using System.Diagnostics;
using System.Runtime.InteropServices;
using Darkmount.App.Input;
using Darkmount.App.Overlays;
using Darkmount.Dock;
using Darkmount.Keyboard.Lamps;
using Darkmount.Sensors;

namespace Darkmount.App;

/// <summary>
/// Drives the keyboard's LEDs through the standard HID LampArray interface: the Lighting-studio scene (layers of
/// effects, up to ~30 fps), live overlays (lock keys, volume bar, mic mute, shortcut helper, focus timer), the alert
/// flash, a welcome sweep, and fading out while the PC is locked or idle. Whenever it has nothing to show (or IO Center
/// / Windows Dynamic Lighting owns the LEDs) it hands the lighting back to the keyboard's own effect.
/// </summary>
public sealed class RgbEngine : IDisposable
{
    readonly Func<AppSettings> _settings;
    readonly Func<Snapshot?> _snapshot;
    readonly Func<bool> _alertActive;
    readonly Func<OverlayState> _overlayState;
    readonly KeyPressFeed? _keys;
    readonly AudioAnalyzer _audio = new();
    readonly ScreenSampler _screen = new();
    readonly BeatDetector _beats = new();
    readonly FlashDetector _flashes = new();
    readonly MouseTracker _mouse = new();
    readonly Thread _thread;
    readonly Stopwatch _clock;
    volatile bool _stop;
    volatile bool _locked;

    LampArrayDevice? _device;
    IReadOnlyList<LampPoint> _layout = [];
    Dictionary<int, int> _lampOfKey = [];
    readonly Dictionary<int, LampColor> _sent = [];
    IReadOnlyList<LampColor> _screenGrid = [];
    DateTime _nextScreenSample;
    Darkmount.Keyboard.NumpadSide _numpadSide;
    double _fade = 1, _welcomeUntil;
    bool _hostControl;

    public string Status { get; private set; } = "Off";
    public double Fps { get; private set; }

    /// <summary>The latest rendered frame (lamp id → colour) for the Lighting studio's live preview.</summary>
    public IReadOnlyDictionary<int, LampColor> LastFrame { get; private set; } = new Dictionary<int, LampColor>();

    /// <summary>Lamp positions once the keyboard's lighting interface has been opened (for the studio preview).</summary>
    public IReadOnlyList<LampPoint> Layout => _layout;

    /// <summary>Edge-light number (1..96) → lamp id, once the lighting interface has been opened.</summary>
    public IReadOnlyDictionary<int, int> EdgeLights => EdgeMap(_layout);

    static IReadOnlyDictionary<int, int> EdgeMap(IReadOnlyList<LampPoint> layout) =>
        layout.Where(p => p.Edge > 0).GroupBy(p => p.Edge).ToDictionary(g => g.Key, g => g.First().LampId);

    /// <summary>
    /// The edge-light map, reading the lamp list from the keyboard if the engine hasn't opened it (read-only: lamp
    /// attributes only, no colours or modes are sent).
    /// </summary>
    public IReadOnlyDictionary<int, int> ReadEdgeLights()
    {
        if (_layout.Count > 0) return EdgeLights;
        try
        {
            using var device = LampArrayDevice.Open();
            return device is null ? new Dictionary<int, int>() : EdgeMap(RgbEffects.Layout(device.Lamps, device.Map));
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Log.Write($"Reading the lamp list failed: {e.Message}");
            return new Dictionary<int, int>();
        }
    }

    public RgbEngine(Func<AppSettings> settings, Func<Snapshot?> snapshot, Func<bool> alertActive, Func<OverlayState> overlayState,
        KeyPressFeed? keys, Stopwatch clock)
    {
        _settings = settings;
        _snapshot = snapshot;
        _alertActive = alertActive;
        _overlayState = overlayState;
        _keys = keys;
        _clock = clock;
        _thread = new Thread(Run) { IsBackground = true, Name = "RGB engine" };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e) =>
        _locked = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
            || (_locked && e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect));

    public void Start()
    {
        _welcomeUntil = _clock.Elapsed.TotalSeconds + 2.2;
        _thread.Start();
    }

    void Run()
    {
        var fpsWindow = Stopwatch.StartNew();
        int frames = 0;
        while (!_stop)
        {
            try
            {
                var s = _settings();
                bool alert = s.RgbAlertFlash && _alertActive();
                bool welcome = s.RgbEnabled && _clock.Elapsed.TotalSeconds < _welcomeUntil;
                bool wanted = s.RgbEnabled || alert;

                bool ioCenter = IoCenterRunning();
                if (!wanted || ioCenter)
                {
                    _audio.SetActive(false);
                    HandBack(ioCenter ? "Paused: IO Center is running" : "Off (keyboard's own effect)");
                    Thread.Sleep(250);
                    continue;
                }
                if (_device is not null && s.NumpadSide != _numpadSide) CloseDevice(handBack: false); // re-lay out the keys
                if (!EnsureDevice()) { Thread.Sleep(2000); continue; }
                if (!_hostControl) { _device!.SetAutonomousMode(false); _hostControl = true; _sent.Clear(); }

                var scene = s.Scene ?? ScenePresets.All[0];
                bool needsAudio = scene.Layers.Any(l => l.Enabled && SceneEffects.Get(l.Effect).NeedsAudio);
                bool needsScreen = scene.Layers.Any(l => l.Enabled && SceneEffects.Get(l.Effect).NeedsScreen);
                bool needsMouse = scene.Layers.Any(l => l.Enabled && SceneEffects.Get(l.Effect).NeedsMouse);
                _audio.SetActive(needsAudio && !alert);
                var (level, bands) = needsAudio ? _audio.Analyse() : (0, Array.Empty<double>());
                if (needsScreen && DateTime.UtcNow >= _nextScreenSample)
                {
                    _screenGrid = _screen.Sample();
                    _nextScreenSample = DateTime.UtcNow.AddMilliseconds(80);
                    _flashes.Feed(_clock.Elapsed.TotalSeconds, _screenGrid);
                }

                double t = _clock.Elapsed.TotalSeconds;
                if (needsAudio) _beats.Feed(t, bands, level);
                if (needsMouse) _mouse.Poll(t);
                var snap = _snapshot();
                var ctx = new SceneContext
                {
                    Seconds = t,
                    Lamps = _layout,
                    KeyPressTimes = _keys?.PressTimes ?? new Dictionary<int, double>(),
                    KeyHeat = _keys?.Heat() ?? new Dictionary<int, double>(),
                    CpuTemp = snap?.CpuTemp, CpuLoad = snap?.CpuLoad, GpuTemp = snap?.GpuTemp, GpuLoad = snap?.GpuLoad,
                    AudioLevel = level,
                    AudioBands = bands,
                    ScreenGrid = _screenGrid,
                    ScreenGridWidth = ScreenSampler.GridWidth,
                    ScreenGridHeight = ScreenSampler.GridHeight,
                    KeyPresses = _keys?.RecentPresses(4) ?? [],
                    BeatTimes = needsAudio ? _beats.Beats : [],
                    ScreenFlashes = needsScreen ? _flashes.Flashes : [],
                    MouseX = needsMouse ? _mouse.X : null,
                    MouseY = needsMouse ? _mouse.Y : null,
                    MouseClicks = needsMouse ? _mouse.Clicks : [],
                    LocalHours = DateTime.Now.TimeOfDay.TotalHours,
                };

                Dictionary<int, LampColor> frame;
                if (alert)
                {
                    var c = (long)(t * 1000 / 350) % 2 == 0 ? new LampColor(255, 0, 0) : new LampColor(40, 0, 0);
                    frame = _layout.ToDictionary(p => p.LampId, _ => c);
                    Status = "Alert flash";
                }
                else if (welcome)
                {
                    frame = Welcome(t);
                    Status = "Welcome";
                }
                else
                {
                    frame = SceneRenderer.Render(scene, ctx);
                    LightingOverlays.Apply(frame, s.Overlays, _overlayState(), k => _lampOfKey.TryGetValue(k, out int l) ? l : null);
                    Status = $"{scene.Name}";
                }

                // Fade out while the PC is locked or idle, fade back in when the user returns.
                bool dark = s.RgbDimWhenLocked && (_locked || IdleSeconds() > s.RgbIdleMinutes * 60 && s.RgbIdleMinutes > 0);
                _fade = Math.Clamp(_fade + (dark ? -0.05 : 0.1), 0, 1);
                if (_fade < 1) foreach (var id in frame.Keys.ToList()) frame[id] = Scale(frame[id], _fade);

                LastFrame = frame;
                Send(frame);

                frames++;
                if (fpsWindow.ElapsedMilliseconds >= 2000)
                {
                    Fps = frames * 1000.0 / fpsWindow.ElapsedMilliseconds;
                    frames = 0;
                    fpsWindow.Restart();
                }
            }
            catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException
                                          or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Write($"RGB engine: {e.Message}");
                CloseDevice(handBack: false);
                Status = "Keyboard lighting not available";
                Thread.Sleep(2000);
            }
            catch (Exception e)
            {
                // A bug in a scene must never end the app (this is a raw thread): hand the LEDs back and retry.
                Log.Write($"RGB engine error: {e}");
                try { CloseDevice(handBack: true); } catch (Exception) { }
                Status = "Lighting error (see the log); retrying";
                Thread.Sleep(2000);
            }
        }
        HandBack("Off");
    }

    long _ioCenterCheckedAt = long.MinValue / 2;
    bool _ioCenterRunning;

    /// <summary>
    /// Whether IO Center runs, checked at most every 2 s. The check lists every process on the PC (~4 ms with 400 of
    /// them); done for each frame at 30 fps it was most of OverMount's CPU time.
    /// </summary>
    bool IoCenterRunning()
    {
        long now = Environment.TickCount64;
        if (now - _ioCenterCheckedAt >= 2000)
        {
            _ioCenterRunning = IoCenterDetector.IsRunning();
            _ioCenterCheckedAt = now;
        }
        return _ioCenterRunning;
    }

    /// <summary>A bright band sweeping left → right with a be quiet! orange trail.</summary>
    Dictionary<int, LampColor> Welcome(double t)
    {
        double x = 1 - (_welcomeUntil - t) / 2.2 * 1.3;
        return _layout.ToDictionary(p => p.LampId, p =>
        {
            double d = x - p.X;
            if (d < 0) return new LampColor(0, 0, 0);
            if (d < 0.06) return new LampColor(255, 255, 255);
            double k = Math.Max(0, 1 - d * 1.5);
            return new LampColor((byte)(255 * k), (byte)(40 * k), 0);
        });
    }

    bool EnsureDevice()
    {
        if (_device is not null) return true;
        var device = LampArrayDevice.Open();
        if (device is null) { Status = "Keyboard lighting interface not found"; return false; }
        if (device.DevicePath is { } path && DynamicLighting.Read(path).WindowsMayDrive)
        {
            device.Dispose();
            Status = "Windows Dynamic Lighting controls the keyboard (see Home → Setup check)";
            return false;
        }
        var lamps = device.Lamps;
        _numpadSide = _settings().NumpadSide;
        _layout = RgbEffects.Layout(lamps, device.Map, _numpadSide);
        _lampOfKey = _layout.Where(p => p.KeyId > 0).GroupBy(p => p.KeyId).ToDictionary(g => g.Key, g => g.First().LampId);
        _device = device;
        Log.Write($"RGB engine: {lamps.Count} lamps, {_lampOfKey.Count} keys");
        return true;
    }

    /// <summary>
    /// Sends only lamps whose colour changed; runs of ≥ 3 consecutive lamp ids with one colour go as a single range
    /// report (a whole-keyboard colour is one report, ~5 ms).
    /// </summary>
    void Send(Dictionary<int, LampColor> frame)
    {
        var changed = frame.Where(kv => !_sent.TryGetValue(kv.Key, out var old) || old != kv.Value).OrderBy(kv => kv.Key).ToList();
        if (changed.Count == 0) { Thread.Sleep(33); return; }

        var ranges = new List<(int, int, LampColor)>();
        var single = new Dictionary<int, LampColor>();
        for (int i = 0; i < changed.Count;)
        {
            int j = i;
            while (j + 1 < changed.Count && changed[j + 1].Key == changed[j].Key + 1 && changed[j + 1].Value == changed[i].Value) j++;
            if (j - i >= 2) ranges.Add((changed[i].Key, changed[j].Key, changed[i].Value));
            else for (int k = i; k <= j; k++) single[changed[k].Key] = changed[k].Value;
            i = j + 1;
        }
        _device!.SetFrame(single, ranges);
        foreach (var (id, c) in changed) _sent[id] = c;
    }

    static LampColor Scale(LampColor c, double k) => new((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    static double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? (Environment.TickCount - (int)info.Time) / 1000.0 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo { public uint Size, Time; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);

    void HandBack(string status)
    {
        Status = status;
        CloseDevice(handBack: true);
    }

    void CloseDevice(bool handBack)
    {
        if (_device is null) return;
        try { if (handBack && _hostControl) _device.SetAutonomousMode(true); }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) { }
        _device.Dispose();
        _device = null;
        _hostControl = false;
        _sent.Clear();
        Fps = 0;
    }

    public void Dispose()
    {
        _stop = true;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        CloseDevice(handBack: true);
        _audio.Dispose();
    }
}
