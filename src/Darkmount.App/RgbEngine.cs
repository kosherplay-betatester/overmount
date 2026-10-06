using System.Diagnostics;
using System.Runtime.InteropServices;
using Darkmount.App.Input;
using Darkmount.App.LightDevices;
using Darkmount.App.Overlays;
using Darkmount.Dock;
using Darkmount.Keyboard.Lamps;
using Darkmount.Sensors;

namespace Darkmount.App;

/// <summary>
/// Draws the Lighting-studio scene (layers of effects, up to ~30 fps), live overlays (lock keys, volume bar, mic mute,
/// shortcut helper, focus timer), the alert flash, a welcome sweep, and fading out while the PC is locked or idle on
/// every RGB keyboard it finds: a be quiet! keyboard, any keyboard or laptop with the Windows Dynamic Lighting standard
/// (HID LampArray), and keyboards OpenRGB controls. Each device gets the same scene laid out over its own lamps.
/// Whenever it has nothing to show (or IO Center / Windows Dynamic Lighting owns a device) it hands that device's
/// lighting back to its own effect.
/// </summary>
public sealed partial class RgbEngine : IDisposable
{
    const int FrameMs = 33;

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

    readonly List<ILightOutput> _outputs = [];
    readonly Dictionary<ILightOutput, double> _lastSent = [];
    IReadOnlyList<LampColor> _screenGrid = [];
    DateTime _nextScreenSample;
    double _fade = 1, _welcomeUntil;

    public string Status { get; private set; } = "Off";
    public double Fps { get; private set; }

    /// <summary>The latest frame drawn on the be quiet! keyboard (lamp id → colour), for the Lighting studio's live preview.</summary>
    public IReadOnlyDictionary<int, LampColor> LastFrame { get; private set; } = new Dictionary<int, LampColor>();

    /// <summary>The be quiet! keyboard's lamp positions once its lighting interface is open (for the studio preview).</summary>
    public IReadOnlyList<LampPoint> Layout => BeQuiet?.Layout ?? [];

    /// <summary>Edge-light number (1..96) → lamp id, once the lighting interface has been opened.</summary>
    public IReadOnlyDictionary<int, int> EdgeLights => EdgeMap(Layout);

    static IReadOnlyDictionary<int, int> EdgeMap(IReadOnlyList<LampPoint> layout) =>
        layout.Where(p => p.Edge > 0).GroupBy(p => p.Edge).ToDictionary(g => g.Key, g => g.First().LampId);

    /// <summary>
    /// The edge-light map, reading the lamp list from the keyboard if the engine hasn't opened it (read-only: lamp
    /// attributes only, no colours or modes are sent).
    /// </summary>
    public IReadOnlyDictionary<int, int> ReadEdgeLights()
    {
        if (Layout.Count > 0) return EdgeLights;
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
        HidSharp.DeviceList.Local.Changed += OnDevicesChanged;
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
            var frameStart = Stopwatch.GetTimestamp();
            try
            {
                var s = _settings();
                bool alert = s.RgbAlertFlash && _alertActive();
                bool welcome = s.RgbEnabled && _clock.Elapsed.TotalSeconds < _welcomeUntil;
                bool wanted = s.RgbEnabled || alert;

                if (!wanted)
                {
                    _audio.SetActive(false);
                    HandBackAll();
                    Status = "Off (keyboard's own effect)";
                    Thread.Sleep(250);
                    continue;
                }
                bool ioCenter = IoCenterRunning();
                UpdateOutputs(s, ioCenter);
                if (_outputs.Count == 0)
                {
                    _audio.SetActive(false);
                    Status = ioCenter ? "Paused: IO Center is running" : _noDeviceStatus;
                    Fps = 0;
                    Thread.Sleep(250);
                    continue;
                }

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
                var pressTimes = _keys?.PressTimes ?? new Dictionary<int, double>();
                var heat = _keys?.Heat() ?? new Dictionary<int, double>();
                var presses = _keys?.RecentPresses(4) ?? [];
                var overlay = _overlayState();
                double localHours = DateTime.Now.TimeOfDay.TotalHours;
                SceneContext Context(IReadOnlyList<LampPoint> lamps) => new()
                {
                    Seconds = t,
                    Lamps = lamps,
                    KeyPressTimes = pressTimes,
                    KeyHeat = heat,
                    CpuTemp = snap?.CpuTemp, CpuLoad = snap?.CpuLoad, GpuTemp = snap?.GpuTemp, GpuLoad = snap?.GpuLoad,
                    AudioLevel = level,
                    AudioBands = bands,
                    ScreenGrid = _screenGrid,
                    ScreenGridWidth = ScreenSampler.GridWidth,
                    ScreenGridHeight = ScreenSampler.GridHeight,
                    KeyPresses = presses,
                    BeatTimes = needsAudio ? _beats.Beats : [],
                    ScreenFlashes = needsScreen ? _flashes.Flashes : [],
                    MouseX = needsMouse ? _mouse.X : null,
                    MouseY = needsMouse ? _mouse.Y : null,
                    MouseClicks = needsMouse ? _mouse.Clicks : [],
                    LocalHours = localHours,
                };

                // Fade out while the PC is locked or idle, fade back in when the user returns.
                bool dark = s.RgbDimWhenLocked && (_locked || IdleSeconds() > s.RgbIdleMinutes * 60 && s.RgbIdleMinutes > 0);
                _fade = Math.Clamp(_fade + (dark ? -0.05 : 0.1), 0, 1);
                Status = alert ? "Alert flash" : welcome ? "Welcome" : DevicesStatus(scene.Name);

                foreach (var output in _outputs.ToList())
                {
                    if (_lastSent.TryGetValue(output, out double last) && t - last < 1 / output.MaxFps - 0.004) continue; // slower devices
                    Dictionary<int, LampColor> frame;
                    if (alert)
                    {
                        var c = (long)(t * 1000 / 350) % 2 == 0 ? new LampColor(255, 0, 0) : new LampColor(40, 0, 0);
                        frame = output.Layout.ToDictionary(p => p.LampId, _ => c);
                    }
                    else if (welcome) frame = Welcome(t, output.Layout);
                    else
                    {
                        frame = SceneRenderer.Render(scene, Context(output.Layout));
                        LightingOverlays.Apply(frame, s.Overlays, overlay, k => output.LampOfKey.TryGetValue(k, out int l) ? l : null);
                    }
                    if (_fade < 1) foreach (var id in frame.Keys.ToList()) frame[id] = Scale(frame[id], _fade);
                    if (output.Via == LightVia.BeQuiet) LastFrame = frame;
                    try
                    {
                        output.Send(frame);
                        _lastSent[output] = t;
                    }
                    catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException
                                                  or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    {
                        Log.Write($"RGB engine: {output.Name}: {e.Message}");
                        Drop(output, handBack: false, $"Lost the connection ({e.Message}); retrying");
                        // Try again after a pause, not every frame (a device that opens but refuses colours would loop).
                        long retry = Environment.TickCount64 + (output.Via == LightVia.BeQuiet ? 2000 : 5000);
                        if (output.Via == LightVia.BeQuiet) Interlocked.Exchange(ref _nextBeQuietTry, retry);
                        else if (output.Via == LightVia.WindowsStandard) _nextScan = retry;
                    }
                }

                frames++;
                if (fpsWindow.ElapsedMilliseconds >= 2000)
                {
                    Fps = frames * 1000.0 / fpsWindow.ElapsedMilliseconds;
                    frames = 0;
                    fpsWindow.Restart();
                }
                int spent = (int)Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                if (spent < FrameMs) Thread.Sleep(FrameMs - spent);
            }
            catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException
                                          or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Write($"RGB engine: {e.Message}");
                CloseAll(handBack: false);
                Status = "Keyboard lighting not available";
                Thread.Sleep(2000);
            }
            catch (Exception e)
            {
                // A bug in a scene must never end the app (this is a raw thread): hand the LEDs back and retry.
                Log.Write($"RGB engine error: {e}");
                try { CloseAll(handBack: true); } catch (Exception) { }
                Status = "Lighting error (see the log); retrying";
                Thread.Sleep(2000);
            }
        }
        CloseAll(handBack: true);
        Status = "Off";
    }

    string DevicesStatus(string scene) => _outputs.Count == 1 ? scene : $"{scene} on {_outputs.Count} devices";

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
    Dictionary<int, LampColor> Welcome(double t, IReadOnlyList<LampPoint> layout)
    {
        double x = 1 - (_welcomeUntil - t) / 2.2 * 1.3;
        return layout.ToDictionary(p => p.LampId, p =>
        {
            double d = x - p.X;
            if (d < 0) return new LampColor(0, 0, 0);
            if (d < 0.06) return new LampColor(255, 255, 255);
            double k = Math.Max(0, 1 - d * 1.5);
            return new LampColor((byte)(255 * k), (byte)(40 * k), 0);
        });
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

    public void Dispose()
    {
        _stop = true;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        HidSharp.DeviceList.Local.Changed -= OnDevicesChanged;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        if (!_thread.IsAlive) CloseAll(handBack: true); // else the engine thread still owns the devices and hands them back itself
        _audio.Dispose();
    }
}
