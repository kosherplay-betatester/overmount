namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// What an editor needs to know about a <see cref="SceneEffect"/>: which colour modes and directions it offers, whether it
/// animates (<see cref="HasSpeed"/>), which live inputs it reads, and whether it covers every targeted lamp
/// (<see cref="IsOpaque"/>; transparent effects let lower layers show where they are dark).
/// </summary>
public sealed record SceneEffectInfo(
    SceneEffect Effect, string Name, string Description, IReadOnlyList<SceneColorMode> ColorModes,
    IReadOnlyList<SceneDirection> Directions, bool HasSpeed, bool NeedsKeyPresses, bool NeedsSensors, bool NeedsAudio,
    bool IsOpaque, bool NeedsScreen = false)
{
    /// <summary>Colour mode a new layer starts with (one of <see cref="ColorModes"/>, or Single when there are none).</summary>
    public SceneColorMode DefaultColorMode { get; init; } = SceneColorMode.Single;

    /// <summary>Colours a new layer starts with; also the fallback when a layer's colours are missing or invalid.</summary>
    public IReadOnlyList<string> DefaultColors { get; init; } = ["FF2800"];

    public SceneDirection DefaultDirection { get; init; } = SceneDirection.Right;
}

/// <summary>The effect catalogue for a lighting editor.</summary>
public static class SceneEffects
{
    static readonly SceneColorMode[] AllModes = [SceneColorMode.Single, SceneColorMode.Dual, SceneColorMode.Gradient];
    static readonly SceneColorMode[] NoModes = [];
    static readonly SceneDirection[] AllDirections = Enum.GetValues<SceneDirection>();
    static readonly SceneDirection[] Linear = [SceneDirection.Left, SceneDirection.Right, SceneDirection.Up, SceneDirection.Down];
    static readonly SceneDirection[] Turning = [SceneDirection.Clockwise, SceneDirection.CounterClockwise];
    static readonly SceneDirection[] NoDirections = [];

    const string Fire0 = "FF1800", Fire1 = "FF5A00", Fire2 = "FFA000", Fire3 = "FFE68C";
    static readonly string[] RainbowStops = ["FF0000", "FFFF00", "00FF00", "00FFFF", "0000FF", "FF00FF"];

    public static IReadOnlyList<SceneEffectInfo> All { get; } =
    [
        new(SceneEffect.Static, "Static", "One colour, or a fixed gradient laid out in the chosen direction.",
            AllModes, AllDirections, false, false, false, false, true),
        new(SceneEffect.ColorWave, "Color wave", "Bands of colour sweep across the keyboard: left/right/up/down, around the centre or out from it.",
            AllModes, AllDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["FF2800", "FF9000", "FF0060"] },
        new(SceneEffect.Tornado, "Tornado", "A spiral whirling around the middle of the keyboard.",
            AllModes, Turning, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = RainbowStops, DefaultDirection = SceneDirection.Clockwise },
        new(SceneEffect.Breathing, "Breathing", "The whole selection slowly fades in and out; with several colours each breath takes the next one.",
            AllModes, NoDirections, true, false, false, false, true),
        new(SceneEffect.Matrix, "Matrix", "Digital rain: bright heads run down the key columns trailing fading code.",
            AllModes, Linear, true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["D2FFD2", "00FF41", "00B32C", "004D14"], DefaultDirection = SceneDirection.Down },
        new(SceneEffect.Reactive, "Reactive", "Keys light up when pressed and fade out; unpressed keys show the layers below.",
            AllModes, NoDirections, true, true, false, false, false)
            { DefaultColorMode = SceneColorMode.Single, DefaultColors = ["FFFFFF"] },
        new(SceneEffect.Ripple, "Ripple", "Every key press sends a ring of light across the keyboard.",
            AllModes, NoDirections, true, true, false, false, false)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["FFFFFF", "00A0FF"] },
        new(SceneEffect.Rainbow, "Rainbow", "The full spectrum flowing across the keyboard.",
            NoModes, AllDirections, true, false, false, false, true),
        new(SceneEffect.Plasma, "Plasma", "Smoothly swirling interference patterns.",
            AllModes, NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["FF0080", "7000FF", "0080FF", "00FFC0", "FFE000"] },
        new(SceneEffect.Aurora, "Aurora", "Slow shimmering curtains of green, teal and violet over a night sky.",
            AllModes, [SceneDirection.Left, SceneDirection.Right], true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["00FF80", "00FFD0", "2080FF", "A040FF"] },
        new(SceneEffect.Fire, "Fire", "Flickering flames rising from the bottom row: red, orange, yellow.",
            AllModes, [SceneDirection.Up, SceneDirection.Down], true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = [Fire0, Fire1, Fire2, Fire3], DefaultDirection = SceneDirection.Up },
        new(SceneEffect.Ocean, "Ocean", "Deep blue and cyan swells rolling across the keys with bright crests.",
            AllModes, Linear, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["000A30", "003080", "0070C0", "00B4E6", "A0F0FF"] },
        new(SceneEffect.Twinkle, "Twinkle", "Random keys sparkle and fade over a dim base colour (Dual: sparkle, base).",
            AllModes, NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["FFFFFF", "040418"] },
        new(SceneEffect.Rain, "Rain", "Raindrops falling down the key columns.",
            AllModes, [SceneDirection.Down, SceneDirection.Up], true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["B4DCFF", "0050FF"], DefaultDirection = SceneDirection.Down },
        new(SceneEffect.Heartbeat, "Heartbeat", "A lub-dub double pulse spreading from the centre (Dual: beat, resting colour).",
            AllModes, NoDirections, true, false, false, false, true)
            { DefaultColors = ["FF0010"] },
        new(SceneEffect.Police, "Police", "Left and right halves flash in turn, like emergency lights.",
            [SceneColorMode.Dual, SceneColorMode.Single], NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["FF0000", "0028FF"] },
        new(SceneEffect.Scanner, "Scanner", "A bar sweeps back and forth leaving a glowing trail (Knight Rider).",
            AllModes, Linear, true, false, false, false, false)
            { DefaultColors = ["FF0000"] },
        new(SceneEffect.ColorCycle, "Color cycle", "The whole selection slowly cycles through the colours (Single: the full spectrum).",
            AllModes, NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = RainbowStops },
        new(SceneEffect.CpuTemperature, "CPU temperature", "Green below 50 °C, through yellow, to red from 85 °C (pulsing); blue while unknown.",
            [SceneColorMode.Gradient, SceneColorMode.Dual], NoDirections, false, false, true, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["00FF00", "FFFF00", "FF0000"] },
        new(SceneEffect.PerformanceMeter, "Performance meter", "F1–F12 show CPU load and the number row GPU load as green → red bars; edges show CPU temperature; other keys glow dimly in the first colour.",
            [SceneColorMode.Single, SceneColorMode.Gradient], NoDirections, false, false, true, false, true),
        new(SceneEffect.TypingHeatmap, "Typing heatmap", "Keys warm up from cold blue to hot red the more you use them.",
            [SceneColorMode.Gradient, SceneColorMode.Dual], NoDirections, false, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["0020FF", "00C8FF", "00FF50", "FFE000", "FF0000"] },
        new(SceneEffect.AudioPulse, "Audio pulse", "Light swells out from the centre with the loudness of the sound playing.",
            AllModes, NoDirections, false, false, false, true, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["FF00C8", "8000FF", "00A0FF"] },
        new(SceneEffect.AudioSpectrum, "Audio spectrum", "A spectrum analyser: bass on the left, treble on the right, bars rising from the bottom row.",
            AllModes, NoDirections, false, false, false, true, false)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["00FF00", "FFFF00", "FF0000"] },
        new(SceneEffect.Lava, "Lava", "Slow glowing blobs drifting through deep red, like a lava lamp.",
            AllModes, NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["280000", "A00800", "FF3000", "FF8C00", "FFD250"] },
        new(SceneEffect.Candy, "Candy", "Pastel pink, cyan and lemon flowing gently.",
            AllModes, Linear, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["FF8AD8", "8AE8FF", "FFF08A", "B89AFF"] },
        new(SceneEffect.ScreenSync, "Screen sync", "Ambilight: keys mirror the matching region of the screen, the edge lights its border.",
            NoModes, NoDirections, false, false, false, false, true, NeedsScreen: true),
        new(SceneEffect.Starfield, "Starfield", "Stars streak out from the centre like a jump to hyperspace (Inward: flying backwards).",
            AllModes, [SceneDirection.Outward, SceneDirection.Inward], true, false, false, false, false)
            { DefaultColors = ["DDEBFF"], DefaultDirection = SceneDirection.Outward },
        new(SceneEffect.Fireworks, "Fireworks", "Rockets burst all over the keyboard into glittering rings that grow, slow down and fade.",
            AllModes, NoDirections, true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["FF3040", "FFD000", "40FF70", "30A0FF", "D040FF", "FFFFFF"] },
        new(SceneEffect.Comet, "Comets", "Comets with long tails race around the frame and orbit across the keys (one per colour, up to four).",
            AllModes, Turning, true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["00E5FF", "FF00C8"], DefaultDirection = SceneDirection.Clockwise },
        new(SceneEffect.Lightning, "Lightning storm", "A dark stormy sky split by sudden lightning bolts and flashes (Dual: flash, sky).",
            [SceneColorMode.Dual, SceneColorMode.Single], NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["E0EAFF", "04061A"] },
        new(SceneEffect.Glitch, "Glitch", "Digital corruption: torn rows, flickering blocks and dead pixels over a dim flowing gradient.",
            AllModes, NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["00FFF0", "FF00C8", "7000FF", "00FF66"] },
        new(SceneEffect.Radar, "Radar", "A sweeping radar beam with a fading trail; contacts glow up as it passes (Dual: beam, screen).",
            [SceneColorMode.Dual, SceneColorMode.Single], Turning, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["00FF66", "001A08"], DefaultDirection = SceneDirection.Clockwise },
        new(SceneEffect.Disco, "Disco", "Blocks of keys jump to new colours on every beat while lights chase around the edges (Single: every colour).",
            AllModes, NoDirections, true, false, false, false, true)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["FF0055", "FFD000", "00FF6A", "00B3FF", "B400FF"] },
        new(SceneEffect.Snow, "Snowfall", "Soft snowflakes drifting down (Dual: snow, sky).",
            AllModes, NoDirections, true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["FFFFFF", "030818"] },
        new(SceneEffect.Bubbles, "Bubbles", "Glowing bubbles wobbling up from the bottom row (Dual: bubbles, water).",
            AllModes, NoDirections, true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["7FF0FF", "001A33"] },
        new(SceneEffect.Embers, "Embers", "Glowing sparks rising and flickering out, like a campfire (Dual: sparks, glow).",
            AllModes, NoDirections, true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Gradient, DefaultColors = ["FFE08A", "FF8C00", "FF3000"] },
        new(SceneEffect.Fireflies, "Fireflies", "A few warm lights wandering and blinking in the dark (Dual: fireflies, night).",
            AllModes, NoDirections, true, false, false, false, false)
            { DefaultColorMode = SceneColorMode.Dual, DefaultColors = ["D8FF4A", "020A02"] },
        new(SceneEffect.PerKey, "Per-key colours", "Paint every key and edge LED its own colour, like IO Center's per-key lighting. " +
            "Unpainted lamps show the layers below.", NoModes, NoDirections, false, false, false, false, false),
    ];

    static readonly Dictionary<SceneEffect, SceneEffectInfo> ByEffect = All.ToDictionary(i => i.Effect);

    /// <summary>The effect's info; an unknown value (hand-edited or newer settings file) is treated as Static.</summary>
    public static SceneEffectInfo Get(SceneEffect e) =>
        ByEffect.TryGetValue(e, out var info) ? info : ByEffect[SceneEffect.Static];

    /// <summary>A new layer (all keys and edges) with the effect's default name, colours, colour mode and direction.</summary>
    public static LightLayer CreateLayer(SceneEffect e)
    {
        var info = Get(e);
        return new LightLayer
        {
            Name = info.Name,
            Effect = e,
            ColorMode = info.ColorModes.Contains(info.DefaultColorMode) || info.ColorModes.Count == 0
                ? info.DefaultColorMode : info.ColorModes[0],
            Colors = [.. info.DefaultColors],
            Direction = info.Directions.Contains(info.DefaultDirection) || info.Directions.Count == 0
                ? info.DefaultDirection : info.Directions[0],
        };
    }
}
