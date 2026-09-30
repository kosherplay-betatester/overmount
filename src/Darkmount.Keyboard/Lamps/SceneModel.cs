using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Darkmount.Keyboard.Lamps;

/// <summary>What a <see cref="LightLayer"/> draws. See <see cref="SceneEffects"/> for names, options and defaults.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SceneEffect>))]
public enum SceneEffect
{
    Static, ColorWave, Tornado, Breathing, Matrix, Reactive, Ripple, Rainbow, Plasma, Aurora, Fire, Ocean, Twinkle, Rain,
    Heartbeat, Police, Scanner, ColorCycle, CpuTemperature, PerformanceMeter, TypingHeatmap, AudioPulse, AudioSpectrum,
    Lava, Candy, ScreenSync, PerKey,
    Starfield, Fireworks, Comet, Lightning, Glitch, Radar, Disco, Snow, Bubbles, Embers, Fireflies,
    KeyLightning, LaserTyping, RainbowTyping, ComboMeter, KeySparks, BeatRings, Waveform, ClubLights, ScreenMood,
    ScreenFlash, MouseSpotlight, SoftPress, WakeOnType, TypingMood,
    PaintSplash, KeyDrops, WordWaves, TypingSnake, VuMeter, DayNight,
}

/// <summary>One key press: Dark Mount key id and the <see cref="SceneContext.Seconds"/> it happened.</summary>
public readonly record struct KeyPress(int KeyId, double At);

/// <summary>A mouse click at a position across all screens (0..1), 0 = left, 1 = right, 2 = middle button.</summary>
public readonly record struct MouseClick(double X, double Y, int Button, double At);

/// <summary>A sudden brightening of the screen (explosion, muzzle flash, lightning) and the screen's colour then.</summary>
public readonly record struct ScreenFlash(double At, LampColor Color, double Strength);

/// <summary>How <see cref="LightLayer.Colors"/> is read: Single = [0], Dual = [0] and [1], Gradient = all (2..7 stops, evenly spaced).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SceneColorMode>))]
public enum SceneColorMode { Single, Dual, Gradient }

/// <summary>Direction of motion (or of a static gradient). Clockwise/CounterClockwise turn around the centre; Outward/Inward are radial.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SceneDirection>))]
public enum SceneDirection { Left, Right, Up, Down, Clockwise, CounterClockwise, Outward, Inward }

/// <summary>
/// One layer of a <see cref="LightingScene"/>: an effect applied to a selection of keys and/or edge LEDs. Plain settable
/// properties so it round-trips through System.Text.Json and binds to an editor.
/// </summary>
public sealed class LightLayer
{
    public string Name { get; set; } = "Layer";
    public bool Enabled { get; set; } = true;
    public SceneEffect Effect { get; set; } = SceneEffect.Static;
    public SceneColorMode ColorMode { get; set; } = SceneColorMode.Single;

    /// <summary>"RRGGBB" (a leading '#' is accepted). Invalid entries are skipped; if none is valid the effect's defaults are used.</summary>
    public List<string> Colors { get; set; } = ["FF2800"];

    public SceneDirection Direction { get; set; } = SceneDirection.Right;

    /// <summary>1 (slow) … 10 (fast); 5 is the effect's natural pace.</summary>
    public int Speed { get; set; } = 5;

    /// <summary>0 … 100 %: scales the layer's colours. 0 turns the layer off (lower layers show through).</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Target every key (else only <see cref="Keys"/>).</summary>
    public bool AllKeys { get; set; } = true;

    /// <summary>Dark Mount key ids (<see cref="KeyIds"/>), used when <see cref="AllKeys"/> is false.</summary>
    public List<int> Keys { get; set; } = [];

    /// <summary>Target every edge LED (else only <see cref="EdgeLamps"/>).</summary>
    public bool AllEdges { get; set; } = true;

    /// <summary>Lamp ids of specific non-key lamps, used when <see cref="AllEdges"/> is false.</summary>
    public List<int> EdgeLamps { get; set; } = [];

    /// <summary><see cref="SceneEffect.PerKey"/>: Dark Mount key id → "RRGGBB". Keys without a colour show the layers below.</summary>
    public Dictionary<int, string> KeyColors { get; set; } = [];

    /// <summary><see cref="SceneEffect.PerKey"/>: edge-LED lamp id → "RRGGBB".</summary>
    public Dictionary<int, string> EdgeColors { get; set; } = [];

    public LightLayer Clone()
    {
        var copy = (LightLayer)MemberwiseClone();
        copy.Colors = [.. Colors ?? []];
        copy.Keys = [.. Keys ?? []];
        copy.EdgeLamps = [.. EdgeLamps ?? []];
        copy.KeyColors = new(KeyColors ?? []);
        copy.EdgeColors = new(EdgeColors ?? []);
        return copy;
    }
}

/// <summary>
/// A layered per-key lighting scene (IO Center's "Custom illumination"). <see cref="Layers"/>[0] is the top layer: layers are
/// drawn from the last to the first over <see cref="Background"/>, so an opaque upper layer wins where it has lamps.
/// </summary>
public sealed class LightingScene
{
    /// <summary>Layers beyond this many are ignored by <see cref="SceneRenderer"/>.</summary>
    public const int MaxLayers = 8;

    public string Name { get; set; } = "Scene";
    public string Description { get; set; } = "";

    /// <summary>Index 0 = top layer.</summary>
    public List<LightLayer> Layers { get; set; } = [];

    /// <summary>"RRGGBB" for lamps no layer covers (invalid = black).</summary>
    public string Background { get; set; } = "000000";

    public LightingScene Clone()
    {
        var copy = (LightingScene)MemberwiseClone();
        copy.Layers = [.. Layers.Select(l => l.Clone())];
        return copy;
    }
}

/// <summary>Everything a frame depends on besides the scene. All times use the same clock as <see cref="Seconds"/>.</summary>
public sealed class SceneContext
{
    /// <summary>Animation clock in seconds.</summary>
    public double Seconds { get; init; }

    /// <summary>
    /// The lamps to colour, positions normalised 0..1 (X left → right, Y top → bottom), e.g. from <see cref="RgbEffects.Layout"/>.
    /// The renderer stretches the keys and the edge LEDs each to fill 0..1, so effects use the whole keyboard.
    /// </summary>
    public required IReadOnlyList<LampPoint> Lamps { get; init; }

    /// <summary>Dark Mount key id → <see cref="Seconds"/> of its last press.</summary>
    public IReadOnlyDictionary<int, double> KeyPressTimes { get; init; } = ReadOnlyDictionary<int, double>.Empty;

    /// <summary>Dark Mount key id → typing heat 0..1 (decayed by the caller).</summary>
    public IReadOnlyDictionary<int, double> KeyHeat { get; init; } = ReadOnlyDictionary<int, double>.Empty;

    /// <summary>°C, null when unknown.</summary>
    public double? CpuTemp { get; init; }

    /// <summary>0..100 %, null when unknown.</summary>
    public double? CpuLoad { get; init; }

    /// <summary>°C, null when unknown.</summary>
    public double? GpuTemp { get; init; }

    /// <summary>0..100 %, null when unknown.</summary>
    public double? GpuLoad { get; init; }

    /// <summary>0..1 peak level.</summary>
    public double AudioLevel { get; init; }

    /// <summary>Optional 0..1 band levels, low → high frequency; may be empty.</summary>
    public IReadOnlyList<double> AudioBands { get; init; } = [];

    /// <summary>
    /// Average screen colours, row-major from the top-left, <see cref="ScreenGridWidth"/> × <see cref="ScreenGridHeight"/>
    /// (R, G, B are used; I is ignored). Empty when not captured.
    /// </summary>
    public IReadOnlyList<LampColor> ScreenGrid { get; init; } = [];

    public int ScreenGridWidth { get; init; }
    public int ScreenGridHeight { get; init; }

    /// <summary>Recent key presses, oldest first (for effects that follow the order and speed of typing).</summary>
    public IReadOnlyList<KeyPress> KeyPresses { get; init; } = [];

    /// <summary>Recent music beats (<see cref="Seconds"/>, oldest first), detected from the bass.</summary>
    public IReadOnlyList<double> BeatTimes { get; init; } = [];

    /// <summary>The mouse pointer across all screens, 0..1; null when unknown.</summary>
    public double? MouseX { get; init; }
    public double? MouseY { get; init; }

    /// <summary>Recent mouse clicks, oldest first.</summary>
    public IReadOnlyList<MouseClick> MouseClicks { get; init; } = [];

    /// <summary>Recent screen flashes, oldest first.</summary>
    public IReadOnlyList<ScreenFlash> ScreenFlashes { get; init; } = [];

    /// <summary>Local time of day in hours (0 ≤ h &lt; 24); null when unknown (effects assume noon).</summary>
    public double? LocalHours { get; init; }
}
