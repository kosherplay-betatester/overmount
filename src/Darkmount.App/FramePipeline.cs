using System.Diagnostics;
using System.Globalization;
using Darkmount.App.Sources;
using Darkmount.Dock;
using Darkmount.Screens;
using Darkmount.Sensors;
using SkiaSharp;

namespace Darkmount.App;

/// <summary>
/// Background loop: sample sensors → pick screen → render 320×240 → upload when changed. Runs every
/// <see cref="AppSettings.RefreshMs"/> (the dock needs ~1.6 s per frame), sooner when a new alert fires.
/// </summary>
public sealed class FramePipeline : IDisposable
{
    readonly DockConnection _dock;
    readonly Func<AppSettings> _settings;
    readonly SensorHub _sensors;
    readonly MetricHistory _history = new();
    readonly AutoSwitcher _switcher = new();
    readonly AlertEngine _alerts;
    readonly StatsScreen _stats = new();
    readonly NowPlayingScreen _nowPlaying = new();
    readonly ClockScreen _clock = new(use24Hour: !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h'),
        CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
    readonly NetworkScreen _networkScreen = new();
    readonly PomodoroScreen _focusScreen = new();
    readonly NetworkMonitor _network = new();
    readonly MediaSessionReader _mediaReader = new();
    readonly Thread _thread, _mediaThread;
    readonly AutoResetEvent _wake = new(false);
    readonly ManualResetEventSlim _stopped = new(false);
    volatile bool _stop;
    volatile MediaInfo? _media;
    string? _lastTrack;

    AnimationScreen? _animation;
    (AnimationKind, string?) _animationKey;
    byte[]? _lastUploaded;
    bool _wasInGame;

    /// <summary>Raised after each render with a copy of the frame (the receiver disposes it).</summary>
    public event Action<SKBitmap>? FrameRendered;

    public Snapshot? LastSnapshot { get; private set; }

    /// <summary>Alerts from the latest frame (read by the RGB engine for its alert flash).</summary>
    public IReadOnlyList<Alert> LastAlerts { get; private set; } = [];
    public ScreenKind CurrentScreen => _switcher.Current;
    public AutoSwitcher Switcher => _switcher;

    /// <summary>
    /// Rest after each upload. The dock can only switch to our screen and react to its buttons while no image is
    /// arriving; back-to-back uploads left it stuck on its menu, 3 s rests showed every frame (verified on hardware).
    /// </summary>
    const int MinQuietGapMs = 3000;

    /// <summary>After the user touches the dial or a dock/media key, uploads pause this long so the dock stays responsive.</summary>
    static readonly TimeSpan PauseAfterDockUse = TimeSpan.FromSeconds(3);

    /// <summary>Optional: tells the pipeline when the user last used the dock (dial, buttons).</summary>
    public Func<TimeSpan, bool>? DockInUse { get; set; }

    /// <summary>Optional: the focus timer's state for the Focus timer screen (null = not available).</summary>
    public Func<PomodoroInfo?>? Pomodoro { get; set; }

    /// <summary>The song playing right now (Spotify, browsers, ...), refreshed every 2 s.</summary>
    public MediaInfo? Media => _media;

    public FramePipeline(DockConnection dock, Func<AppSettings> settings)
    {
        _dock = dock;
        _settings = settings;
        _sensors = new SensorHub(settings().Sensors);
        _alerts = new AlertEngine(settings().Alerts);
        _thread = new Thread(Run) { IsBackground = true, Name = "Dock frame pipeline", Priority = ThreadPriority.BelowNormal };
        _mediaThread = new Thread(PollMedia) { IsBackground = true, Name = "Media sessions", Priority = ThreadPriority.BelowNormal };
    }

    public void Start()
    {
        _thread.Start();
        _mediaThread.Start();
    }

    /// <summary>Windows media calls can block for seconds, so they run on their own thread.</summary>
    void PollMedia()
    {
        try
        {
            while (!_stop)
            {
                _media = _mediaReader.Poll();
                _stopped.Wait(2000);
            }
        }
        catch (ObjectDisposedException) { } // shutting down
    }

    /// <summary>Renders and uploads a new frame as soon as possible (screen switch, settings change).</summary>
    public void RefreshNow() => _wake.Set();

    /// <remarks>
    /// Raw threads take the whole process down on an unhandled exception, so everything here is caught; after
    /// <see cref="Dispose"/> the wait handles may already be gone, which simply ends the loop.
    /// </remarks>
    void Run()
    {
        try { RunLoop(); }
        catch (ObjectDisposedException) when (_stop) { }
        catch (Exception e) { Log.Write($"Frame pipeline stopped: {e}"); }
    }

    void RunLoop()
    {
        while (!_stop)
        {
            var started = Stopwatch.StartNew();
            try { RenderAndPresent(); }
            catch (Exception e) { Log.Write($"Frame pipeline error: {e}"); }

            // Wait for the next frame, checking for new alerts every 500 ms so they show immediately. Always leave
            // a short quiet gap after an upload so the dock can switch to (or stay on) our screen.
            int refresh = Math.Clamp(_settings().RefreshMs, 1500, 60000);
            var gap = System.Diagnostics.Stopwatch.StartNew();
            if (DockInUse?.Invoke(PauseAfterDockUse) == true) refresh = 500; // re-check soon after the user stops
            while (!_stop && (started.ElapsedMilliseconds < refresh || gap.ElapsedMilliseconds < MinQuietGapMs))
            {
                if (_wake.WaitOne(500)) break;
                if (CheckForNewAlert()) break;
            }
        }
    }

    bool CheckForNewAlert()
    {
        try
        {
            _alerts.Evaluate(_sensors.Sample(), DateTime.Now);
            return _alerts.HasNewAlert;
        }
        catch (Exception e) { Log.Write($"Alert check failed: {e.Message}"); return false; }
    }

    void RenderAndPresent()
    {
        var settings = _settings();
        _alerts.Settings = settings.Alerts;

        var snapshot = _sensors.Sample();
        LastSnapshot = snapshot;
        if (_wasInGame && !snapshot.InGame) _history.ClearFps();
        _wasInGame = snapshot.InGame;
        _history.Add(snapshot);

        var network = _network.Poll();
        _history.AddNetwork(network?.DownloadBytesPerSec ?? 0, network?.UploadBytesPerSec ?? 0);
        var media = _media;
        string? track = media is { Playing: true } ? $"{media.Artist} - {media.Title}" : null;
        bool newTrack = track is not null && track != _lastTrack;
        if (track is not null) _lastTrack = track;
        var pomodoro = Pomodoro?.Invoke();
        bool focusActive = pomodoro?.Phase is PomodoroInfo.Focus or PomodoroInfo.Break or PomodoroInfo.LongBreak or PomodoroInfo.Paused;

        var now = DateTime.Now;
        var alerts = _alerts.Evaluate(snapshot, now);
        LastAlerts = alerts;
        var kind = _switcher.Update(snapshot.InGame, settings, new ScreenSignals(now, focusActive, newTrack));
        IDockScreen screen = kind switch
        {
            ScreenKind.Animation => Animation(settings),
            ScreenKind.NowPlaying => _nowPlaying,
            ScreenKind.Clock => _clock,
            ScreenKind.Network => _networkScreen,
            ScreenKind.FocusTimer => _focusScreen,
            _ => _stats,
        };

        _stats.Layout = settings.StatsLayout ?? new();
        var ctx = new ScreenContext
        {
            Snapshot = snapshot, History = _history, Alerts = alerts, Now = now, Media = media, Network = network, Pomodoro = pomodoro,
        };
        using var frame = DockRenderer.Render(screen, ctx);
        FrameRendered?.Invoke(frame.Copy());

        _dock.ShowAppScreens = settings.Mode != ScreenMode.DockDefault;
        if (_dock.State is not (DockState.Connected or DockState.Ready)) { _lastUploaded = null; return; }

        if (DockInUse?.Invoke(PauseAfterDockUse) == true) return; // let the dock's own controls respond

        var pixels = Rgb565.FromBitmap(frame);
        if (!_dock.ShowAppScreens) { _dock.Present(pixels); _lastUploaded = null; return; } // releases the screen
        if (_dock.State == DockState.Ready && _lastUploaded is not null && pixels.AsSpan().SequenceEqual(_lastUploaded)) return;
        if (_dock.Present(pixels)) _lastUploaded = pixels;
    }

    AnimationScreen Animation(AppSettings s)
    {
        var key = (s.AnimationKind, s.AnimationPath);
        if (_animation is null || _animationKey != key)
        {
            _animation?.Dispose();
            _animation = new AnimationScreen(AnimationSources.Create(s.AnimationKind, s.AnimationPath));
            _animationKey = key;
        }
        return _animation;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _stopped.Set();
        // An upload in progress can take up to ~13 s (header + chunk timeouts) before the loop sees _stop.
        bool renderDone = !_thread.IsAlive || _thread.Join(TimeSpan.FromSeconds(15));
        bool mediaDone = !_mediaThread.IsAlive || _mediaThread.Join(TimeSpan.FromSeconds(5));
        _mediaReader.Dispose(); // thread-safe: a poll in progress finishes, later polls return null
        if (!renderDone || !mediaDone)
        {
            // Leave what the stuck thread may still touch to the garbage collector instead of pulling it away.
            Log.Write("Frame pipeline did not stop in time; leaving its resources to finish on their own");
            return;
        }
        _animation?.Dispose();
        _sensors.Dispose();
        _wake.Dispose();
        _stopped.Dispose();
    }
}
