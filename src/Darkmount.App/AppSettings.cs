using System.Text.Json;
using System.Text.Json.Serialization;
using Darkmount.Screens;
using Darkmount.Sensors;

namespace Darkmount.App;

/// <summary>
/// What the dock shows. <see cref="Auto"/>: stats while a game runs, otherwise the default screen (or the rotation);
/// <see cref="DockDefault"/> hands the screen back to the keyboard's own be quiet! menu; the rest fix one screen.
/// </summary>
public enum ScreenMode { Auto, Stats, Animation, NowPlaying, Clock, Network, FocusTimer, DockDefault }
public enum ScreenKind { Stats, Animation, NowPlaying, Clock, Network, FocusTimer }

public sealed class AlertSettings
{
    public bool CpuTempEnabled { get; set; } = true;
    public double CpuTempMax { get; set; } = 90;
    public bool GpuTempEnabled { get; set; } = true;
    public double GpuTempMax { get; set; } = 85;
    public bool RamEnabled { get; set; } = true;
    public double RamMaxPercent { get; set; } = 90;
    public bool VramEnabled { get; set; } = true;
    public double VramMaxPercent { get; set; } = 95;
    public bool FpsEnabled { get; set; } = true;
    public double FpsMin { get; set; } = 30;
    public double FpsSeconds { get; set; } = 4;
    public double HoldSeconds { get; set; } = 10;
}

public sealed class AppSettings
{
    public ScreenMode Mode { get; set; } = ScreenMode.Auto;

    /// <summary>Screen shown in Auto mode when no game runs.</summary>
    public ScreenKind DefaultScreen { get; set; } = ScreenKind.Stats;

    /// <summary>Stats screen: which CPU/GPU value is big and what the graphs draw.</summary>
    public StatsLayout StatsLayout { get; set; } = new();

    /// <summary>In Auto mode without a game, take turns showing <see cref="Rotation"/> instead of the default screen.</summary>
    public bool RotateScreens { get; set; }
    public List<ScreenKind> Rotation { get; set; } = [ScreenKind.Stats, ScreenKind.NowPlaying, ScreenKind.Clock, ScreenKind.Network];
    public int RotateSeconds { get; set; } = 30;

    /// <summary>In Auto mode without a game: Now playing for a while when a song starts, the focus timer while it runs.</summary>
    public bool SmartScreens { get; set; } = true;

    /// <summary>Focus timer (Pomodoro) lengths in minutes.</summary>
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public string FocusHotkey { get; set; } = "Ctrl+Alt+Shift+F";

    public AnimationKind AnimationKind { get; set; } = AnimationKind.Plasma;
    public string? AnimationPath { get; set; }

    /// <summary>Kept for old settings files; the dock idle delay is always 1 s while the app drives the dock.</summary>
    public int DockIdleSeconds { get; set; } = 1;

    /// <summary>Target time between dock images. Each upload takes ~2.2 s and is followed by a 3 s rest.</summary>
    public int RefreshMs { get; set; } = 5000;
    public string Hotkey { get; set; } = "Ctrl+Alt+Shift+D";
    public bool StartWithWindows { get; set; } = true;
    public AlertSettings Alerts { get; set; } = new();

    /// <summary>Lighting-studio scene on the keyboard's LEDs (off = the keyboard's own lighting effect).</summary>
    public bool RgbEnabled { get; set; }

    /// <summary>The Lighting-studio scene (null = the first preset).</summary>
    public Darkmount.Keyboard.Lamps.LightingScene? Scene { get; set; }

    /// <summary>Live overlays drawn on top of the scene.</summary>
    public Overlays.OverlaySettings Overlays { get; set; } = new();

    /// <summary>Fade the LEDs out while Windows is locked (and after <see cref="RgbIdleMinutes"/> without input).</summary>
    public bool RgbDimWhenLocked { get; set; } = true;

    /// <summary>Minutes without input before the LEDs fade out (0 = only when locked).</summary>
    public int RgbIdleMinutes { get; set; } = 10;

    /// <summary>Which side the numpad is attached to (for the lighting picture and effect geometry).</summary>
    public Darkmount.Keyboard.NumpadSide NumpadSide { get; set; } = Darkmount.Keyboard.NumpadSide.Right;

    /// <summary>Flash the keyboard red while a dock alert is active.</summary>
    public bool RgbAlertFlash { get; set; } = true;
    public SensorOptions Sensors { get; set; } = new();

    /// <summary>Look for a newer release on GitHub once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>A release the user chose to skip ("1.2.0"), so it isn't offered again.</summary>
    public string? SkippedUpdate { get; set; }

    /// <summary>The sensor-apps assistant was offered once on its own (it stays reachable from Home and the tray).</summary>
    public bool CompanionsOffered { get; set; }

    /// <summary>The assistant was offered again for OverMount's own CPU sensor (1.3), when CPU temperature was missing.</summary>
    public bool CpuSensorOffered { get; set; }
}

public static class SettingsStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverMount", "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            Log.Write($"Settings file unreadable, using defaults: {e.Message}");
            return new();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
