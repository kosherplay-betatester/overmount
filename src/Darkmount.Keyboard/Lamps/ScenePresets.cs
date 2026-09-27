namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// Premade scenes for the lighting studio, grouped by mood. <see cref="All"/> and <see cref="Categories"/> return fresh
/// copies, so callers may edit them freely. <see cref="All"/>[0] is the default scene.
/// </summary>
public static class ScenePresets
{
    public static IReadOnlyList<LightingScene> All => [.. Build().SelectMany(c => c.Scenes)];

    /// <summary>The presets by category, in display order.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<LightingScene> Scenes)> Categories =>
        [.. Build().Select(c => (c.Name, (IReadOnlyList<LightingScene>)c.Scenes))];

    enum On { All, Keys, Edges }

    // Key ids used by the presets (KeyIds has the full table).
    const byte W = 17, A = 30, S = 31, D = 32, Q = 16, E = 18, R = 19, F = 33, Tab = 15, Space = 57, LShift = 42, LCtrl = 54;
    const byte One = 2, Two = 3, Three = 4, Four = 5, Five = 6;

    static LightLayer Fx(SceneEffect effect, string[]? colors = null, SceneDirection? direction = null, int speed = 5,
        int brightness = 100, On on = On.All, int[]? keys = null, string? name = null, SceneColorMode? mode = null)
    {
        var layer = SceneEffects.CreateLayer(effect);
        if (colors is { Length: > 0 })
        {
            layer.Colors = [.. colors];
            layer.ColorMode = colors.Length switch { 1 => SceneColorMode.Single, 2 => SceneColorMode.Dual, _ => SceneColorMode.Gradient };
        }
        if (mode is { } m) layer.ColorMode = m;
        if (direction is { } d) layer.Direction = d;
        if (name is not null) layer.Name = name;
        layer.Speed = speed;
        layer.Brightness = brightness;
        layer.AllKeys = on != On.Edges && keys is null;
        layer.Keys = keys is null ? [] : [.. keys];
        layer.AllEdges = on != On.Keys && keys is null;
        return layer;
    }

    static LightingScene Scene(string name, string description, params LightLayer[] layers) =>
        new() { Name = name, Description = description, Layers = [.. layers] };

    static readonly string[] Rainbow = ["FF0000", "FFFF00", "00FF00", "00FFFF", "0000FF", "FF00FF"];
    static readonly int[] Wasd = [W, A, S, D];
    static readonly int[] Arrows = [KeyIds.Up, KeyIds.Left, KeyIds.Down, KeyIds.Right];

    static List<(string Name, List<LightingScene> Scenes)> Build() =>
    [
        ("Signature", [
            Scene("Rainbow wave", "The full spectrum rolling from left to right across keys and edge lights.",
                Fx(SceneEffect.Rainbow, direction: SceneDirection.Right)),

            Scene("be quiet! orange", "The be quiet! signature orange on the keys, gently breathing on the edges.",
                Fx(SceneEffect.Breathing, ["FF2800"], speed: 3, on: On.Edges, name: "Breathing edges"),
                Fx(SceneEffect.Static, ["FF2800"], on: On.Keys, name: "Orange keys")),

            Scene("Silent wings", "A be quiet! orange comet gliding around the frame and across dimly glowing orange keys.",
                Fx(SceneEffect.Comet, ["FF6A00"], SceneDirection.Clockwise, speed: 3, name: "Comet"),
                Fx(SceneEffect.Static, ["FF2800"], brightness: 18, name: "Ember glow")),

            Scene("Pure white", "Clean, even white keys with softly breathing white edges. Great for typing at night.",
                Fx(SceneEffect.Breathing, ["FFFFFF"], speed: 2, on: On.Edges, name: "Breathing edges"),
                Fx(SceneEffect.Static, ["FFFFFF"], brightness: 85, on: On.Keys, name: "White keys")),

            Scene("Color cycle", "The whole keyboard glides slowly through the rainbow.",
                Fx(SceneEffect.ColorCycle, Rainbow, speed: 3)),
        ]),

        ("Neon & synth", [
            Scene("Cyberpunk", "Hot pink fading into electric cyan, with white flashes under your fingers.",
                Fx(SceneEffect.Reactive, ["FFFFFF"], speed: 6, on: On.Keys, name: "Key flash"),
                Fx(SceneEffect.ColorWave, ["FF0080", "B000FF", "00E5FF"], SceneDirection.Right, speed: 4, name: "Neon wave")),

            Scene("Synthwave", "Retro sunset stripes rising up the keys under a pulsing magenta-cyan frame.",
                Fx(SceneEffect.Breathing, ["FF00C8", "00E5FF"], speed: 4, on: On.Edges, name: "Frame"),
                Fx(SceneEffect.ColorWave, ["FF2A6D", "D100D1", "7B2FF7", "05D9E8"], SceneDirection.Up, speed: 3, on: On.Keys, name: "Stripes")),

            Scene("Vaporwave", "Pastel pink, teal and lilac washing slowly up the keys, the frame breathing in pink and teal.",
                Fx(SceneEffect.Breathing, ["FF71CE", "01CDFE"], speed: 2, on: On.Edges, name: "Frame"),
                Fx(SceneEffect.ColorWave, ["FF71CE", "01CDFE", "05FFA1", "B967FF", "FFFB96"], SceneDirection.Up, speed: 2, on: On.Keys, name: "Pastel waves")),

            Scene("Neon comets", "Four neon comets chasing each other around the keyboard through a deep violet night.",
                Fx(SceneEffect.Comet, ["FF00C8", "00E5FF", "FFE600", "39FF14"], SceneDirection.Clockwise, speed: 6, name: "Comets"),
                Fx(SceneEffect.Static, ["0A0014"], name: "Night")),

            Scene("Tron", "Light-cycle cyan racing around the frame while a scan line sweeps the dark grid.",
                Fx(SceneEffect.Comet, ["00F6FF"], SceneDirection.CounterClockwise, speed: 7, on: On.Edges, name: "Light cycle"),
                Fx(SceneEffect.Scanner, ["00F6FF"], SceneDirection.Down, speed: 3, brightness: 70, on: On.Keys, name: "Scan line"),
                Fx(SceneEffect.Static, ["00080C"], name: "Grid")),

            Scene("Glitch city", "Corrupted neon: torn rows, flickering blocks and dead pixels; keys flash white as you type.",
                Fx(SceneEffect.Reactive, ["FFFFFF"], speed: 7, on: On.Keys, name: "Key flash"),
                Fx(SceneEffect.Glitch, speed: 5, name: "Glitch")),

            Scene("Laser show", "Magenta laser beams sweeping around a dark club, cyan sparks where you type.",
                Fx(SceneEffect.Ripple, ["00FFFF"], speed: 7, name: "Sparks"),
                Fx(SceneEffect.Radar, ["FF00FF", "08000C"], SceneDirection.CounterClockwise, speed: 8, name: "Lasers")),

            Scene("Neon pulse", "Magenta and cyan rings flowing out from the centre.",
                Fx(SceneEffect.ColorWave, ["FF00FF", "00FFFF"], SceneDirection.Outward, speed: 6)),

            Scene("Plasma", "Swirling neon interference patterns.",
                Fx(SceneEffect.Plasma, speed: 4)),

            Scene("Toxic", "Radioactive green and yellow plasma; keys flare yellow when pressed.",
                Fx(SceneEffect.Reactive, ["F2FF00"], speed: 6, on: On.Keys, name: "Flare"),
                Fx(SceneEffect.Plasma, ["1AFF00", "B6FF00", "FFF200", "00FF6A"], speed: 4, name: "Plasma")),
        ]),

        ("Space", [
            Scene("Hyperspace", "Punch it: stars stretch into streaks as you jump to lightspeed.",
                Fx(SceneEffect.Starfield, ["E6F0FF"], SceneDirection.Outward, speed: 7, name: "Stars"),
                Fx(SceneEffect.Static, ["020416"], name: "Space")),

            Scene("Deep space", "Coloured stars drifting past a slowly turning violet galaxy.",
                Fx(SceneEffect.Starfield, ["FFFFFF", "9FD4FF", "FF9FE0"], SceneDirection.Outward, speed: 2, name: "Stars"),
                Fx(SceneEffect.Tornado, ["1A0033", "6A00FF", "FF00C8", "00D4FF", "1A0033"], SceneDirection.CounterClockwise, speed: 1, brightness: 45, name: "Galaxy")),

            Scene("Galaxy", "A slow violet and cyan spiral galaxy turning counter-clockwise.",
                Fx(SceneEffect.Tornado, ["1A0033", "6A00FF", "FF00C8", "00D4FF", "1A0033"], SceneDirection.CounterClockwise, speed: 2)),

            Scene("Meteor shower", "Bright meteors streaking down through a twinkling night sky.",
                Fx(SceneEffect.Rain, ["FFFFFF", "5A7BFF"], SceneDirection.Down, speed: 9, name: "Meteors"),
                Fx(SceneEffect.Twinkle, ["FFF4D6", "030314"], speed: 2, name: "Stars")),

            Scene("Starry night", "Stars twinkling on a deep navy sky.",
                Fx(SceneEffect.Twinkle, ["FFF4D6", "050520"], speed: 3)),
        ]),

        ("Nature", [
            Scene("Aurora borealis", "Slow curtains of green, teal and violet shimmering over a night sky.",
                Fx(SceneEffect.Aurora, speed: 4)),

            Scene("Arctic night", "Snow drifting down through the northern lights.",
                Fx(SceneEffect.Snow, ["FFFFFF"], speed: 4, brightness: 85, name: "Snow"),
                Fx(SceneEffect.Aurora, speed: 3, brightness: 70, name: "Aurora")),

            Scene("Campfire", "Sparks rising and winking out above crackling flames.",
                Fx(SceneEffect.Embers, speed: 5, name: "Sparks"),
                Fx(SceneEffect.Fire, speed: 4, brightness: 75, name: "Flames")),

            Scene("Fire", "Flames licking up from the space bar: deep red, orange and yellow.",
                Fx(SceneEffect.Fire, speed: 6)),

            Scene("Thunderstorm", "Rain streaming down a black sky torn open by lightning.",
                Fx(SceneEffect.Rain, ["C8E6FF"], SceneDirection.Down, speed: 6, brightness: 80, name: "Rain"),
                Fx(SceneEffect.Lightning, speed: 5, name: "Lightning")),

            Scene("Rainy day", "Blue raindrops streaming down over a dark stormy background.",
                Fx(SceneEffect.Rain, ["C8E6FF", "2060FF"], SceneDirection.Down, speed: 5, name: "Drops"),
                Fx(SceneEffect.Static, ["05081A"], name: "Storm")),

            Scene("Summer night", "Fireflies drifting and blinking under a starry sky.",
                Fx(SceneEffect.Fireflies, ["D8FF4A"], speed: 4, name: "Fireflies"),
                Fx(SceneEffect.Twinkle, ["FFF4D6", "02040C"], speed: 2, name: "Stars")),

            Scene("Enchanted forest", "Golden and mint fireflies over softly shifting mossy greens.",
                Fx(SceneEffect.Fireflies, ["D8FF4A", "7CFFB2", "FFD27F"], speed: 5, name: "Fireflies"),
                Fx(SceneEffect.Plasma, ["002A12", "0A6B2A", "3FA33A", "0A4A1E"], speed: 2, brightness: 45, name: "Moss")),

            Scene("Cherry blossom", "Pink petals floating down over a deep plum dusk.",
                Fx(SceneEffect.Snow, ["FFB7D5", "FF8FB8", "FFE4F0"], speed: 3, name: "Petals"),
                Fx(SceneEffect.Static, ["2A0A1A", "4A1030"], SceneDirection.Right, name: "Dusk")),

            Scene("Ocean", "Rolling blue swells; every key press sends a ripple across the water.",
                Fx(SceneEffect.Ripple, ["E0FFFF", "0080FF"], speed: 5, name: "Splashes"),
                Fx(SceneEffect.Ocean, direction: SceneDirection.Right, speed: 4, name: "Waves")),

            Scene("Underwater", "Bubbles wobbling up through slow, deep blue swells.",
                Fx(SceneEffect.Bubbles, ["A0F8FF"], speed: 5, name: "Bubbles"),
                Fx(SceneEffect.Ocean, direction: SceneDirection.Right, speed: 3, brightness: 70, name: "Deep water")),

            Scene("Coral reef", "Pastel bubbles rising over turquoise water with coral highlights.",
                Fx(SceneEffect.Bubbles, ["FF8AD8", "7FF0FF", "FFF08A"], speed: 4, name: "Bubbles"),
                Fx(SceneEffect.Ocean, ["002A3A", "006D77", "00B4A6", "83E8D5", "FFB4A2"], SceneDirection.Left, speed: 3, name: "Reef")),

            Scene("Desert mirage", "Sand, amber and terracotta shimmering in the heat.",
                Fx(SceneEffect.Candy, ["FFB347", "FF7F50", "FFD89B", "E26D5A"], SceneDirection.Right, speed: 2)),

            Scene("Sunset", "Orange melting into pink and purple, drifting slowly across the keys.",
                Fx(SceneEffect.ColorWave, ["FF6A00", "FF2E63", "B5179E", "5A189A"], SceneDirection.Left, speed: 2)),

            Scene("Lava lamp", "Glowing orange blobs drifting slowly through molten red.",
                Fx(SceneEffect.Lava, speed: 4)),

            Scene("Breathing ice", "Frosty whites and blues slowly breathing in and out.",
                Fx(SceneEffect.Breathing, ["DFF6FF", "7FD4FF", "2F7BFF"], speed: 3)),

            Scene("Candy", "Pastel pink, sky blue, lemon and lilac flowing gently.",
                Fx(SceneEffect.Candy, direction: SceneDirection.Right, speed: 4)),
        ]),

        ("Gaming", [
            Scene("Gamer WASD", "Movement keys and arrows in bright red over a dim base; every key flashes white when pressed.",
                Fx(SceneEffect.Reactive, ["FFFFFF"], speed: 6, on: On.Keys, name: "Key flash"),
                Fx(SceneEffect.Static, ["FF0000"], keys: [.. Wasd, .. Arrows], name: "WASD + arrows"),
                Fx(SceneEffect.Static, ["FF2800"], brightness: 15, name: "Dim base")),

            Scene("Tactical FPS", "Teal movement, reload, jump, sprint and crouch keys; radar sweeping the frame; orange muzzle flashes as you type.",
                Fx(SceneEffect.Reactive, ["FF8C00"], speed: 7, on: On.Keys, name: "Muzzle flash"),
                Fx(SceneEffect.Static, ["00FFC8"], keys: [.. Wasd, R, Space, LShift, LCtrl], name: "Action keys"),
                Fx(SceneEffect.Radar, ["00FFC8", "001410"], SceneDirection.Clockwise, speed: 4, on: On.Edges, name: "Radar"),
                Fx(SceneEffect.Static, ["00261E"], brightness: 60, on: On.Keys, name: "Dim base")),

            Scene("MOBA", "Abilities QWER in gold, D and F in cyan, items 1–5 in violet; ripples on every cast.",
                Fx(SceneEffect.Ripple, ["FFFFFF", "7A5CFF"], speed: 6, name: "Casts"),
                Fx(SceneEffect.Static, ["FFC400"], keys: [Q, W, E, R], name: "Abilities"),
                Fx(SceneEffect.Static, ["00E5FF"], keys: [D, F], name: "Summoner spells"),
                Fx(SceneEffect.Static, ["9B4DFF"], keys: [One, Two, Three, Four, Five], name: "Items"),
                Fx(SceneEffect.Static, ["050A20"], name: "Base")),

            Scene("Nitro", "Red-hot racing: fire-coloured waves speeding across the keys, comets lapping the frame.",
                Fx(SceneEffect.Comet, ["FF2000", "FFB000"], SceneDirection.Clockwise, speed: 9, on: On.Edges, name: "Laps"),
                Fx(SceneEffect.ColorWave, ["FF0000", "FF5A00", "FFB000", "FF0000"], SceneDirection.Right, speed: 8, on: On.Keys, name: "Speed")),

            Scene("Boss fight", "A racing red heartbeat; every key you hit flashes white.",
                Fx(SceneEffect.Reactive, ["FFFFFF"], speed: 7, on: On.Keys, name: "Hits"),
                Fx(SceneEffect.Heartbeat, ["FF0020", "140000"], speed: 8, name: "Heartbeat")),

            Scene("Radar sweep", "A green sonar beam sweeping the keyboard, movement keys locked on.",
                Fx(SceneEffect.Static, ["00FF66"], keys: [.. Wasd], name: "WASD"),
                Fx(SceneEffect.Radar, speed: 5, name: "Radar")),

            Scene("Stealth", "Almost dark: faint white keys, dim red WASD, and a soft glow only where you type.",
                Fx(SceneEffect.Reactive, ["C8D8FF"], speed: 4, on: On.Keys, name: "Soft glow"),
                Fx(SceneEffect.Static, ["FF0000"], brightness: 35, keys: [.. Wasd], name: "WASD"),
                Fx(SceneEffect.Static, ["FFFFFF"], brightness: 6, name: "Dim")),

            Scene("Hacker terminal", "Green code rain with glitches in the system; keys flash green as you type.",
                Fx(SceneEffect.Reactive, ["B6FFB6"], speed: 6, on: On.Keys, name: "Keystrokes"),
                Fx(SceneEffect.Matrix, direction: SceneDirection.Down, speed: 6, name: "Code rain"),
                Fx(SceneEffect.Glitch, ["00FF41", "00B32C", "004D14", "B6FFB6"], speed: 4, brightness: 40, name: "Glitch")),

            Scene("Victory royale", "You won: gold, white, cyan and pink fireworks over a purple sky.",
                Fx(SceneEffect.Fireworks, ["FFD700", "FFFFFF", "00E5FF", "FF4FD8"], speed: 6, name: "Fireworks"),
                Fx(SceneEffect.Static, ["0A0520"], name: "Sky")),

            Scene("Ultra instinct", "Silver-blue comets streaking around a calm, shimmering aura.",
                Fx(SceneEffect.Comet, ["C0E8FF", "5AA9FF", "FFFFFF"], SceneDirection.Clockwise, speed: 8, name: "Comets"),
                Fx(SceneEffect.Plasma, ["0A1030", "2A4B8C", "8FB8FF", "E6F0FF"], speed: 3, brightness: 50, name: "Aura")),

            Scene("Matrix", "Green digital rain running down the key columns over a faint green glow.",
                Fx(SceneEffect.Matrix, direction: SceneDirection.Down, speed: 5, name: "Code rain"),
                Fx(SceneEffect.Static, ["001A06"], name: "Glow")),

            Scene("Knight Rider", "KITT's red scanner sweeping back and forth with a glowing trail.",
                Fx(SceneEffect.Scanner, ["FF0000"], SceneDirection.Right, speed: 5, name: "Scanner"),
                Fx(SceneEffect.Static, ["1A0000"], name: "Dim red")),

            Scene("Police", "Red and blue emergency flashes alternating left and right.",
                Fx(SceneEffect.Police, ["FF0000", "0028FF"], speed: 6)),

            Scene("Heartbeat", "A red lub-dub pulse spreading out from the centre.",
                Fx(SceneEffect.Heartbeat, ["FF0020", "100002"], speed: 5)),
        ]),

        ("Party & music", [
            Scene("Beat rings", "Every kick drum sends a coloured ring across the keys and flashes the frame.",
                Fx(SceneEffect.BeatRings, speed: 5, name: "Rings"),
                Fx(SceneEffect.Static, ["03010A"], name: "Dark")),

            Scene("Nightclub", "The bass kicks the frame red, the melody colours the keys, the hi-hats sparkle.",
                Fx(SceneEffect.ClubLights, speed: 5)),

            Scene("Festival", "Beat rings bursting over club lights: the full festival stage.",
                Fx(SceneEffect.BeatRings, ["FFFFFF", "FFE600"], speed: 6, name: "Rings"),
                Fx(SceneEffect.ClubLights, ["B000FF", "FF0080", "00B3FF", "FFD000"], speed: 5, brightness: 70, name: "Stage")),

            Scene("Oscilloscope", "A glowing waveform dancing across the keys to your music.",
                Fx(SceneEffect.Waveform, speed: 5, name: "Waveform"),
                Fx(SceneEffect.Static, ["000805"], name: "Screen")),

            Scene("Disco fever", "A light-up dance floor: blocks of keys change colour on every beat while the edges chase.",
                Fx(SceneEffect.Disco, speed: 5)),

            Scene("Rave", "Every colour at once on the dance floor, with stars streaking out of the middle.",
                Fx(SceneEffect.Starfield, ["FFFFFF"], SceneDirection.Outward, speed: 8, name: "Strobe stars"),
                Fx(SceneEffect.Disco, ["FF0000"], speed: 7, mode: SceneColorMode.Single, name: "Dance floor")),

            Scene("Fireworks show", "Colourful fireworks bursting all over a night sky.",
                Fx(SceneEffect.Fireworks, speed: 5, name: "Fireworks"),
                Fx(SceneEffect.Static, ["02030F"], name: "Night sky")),

            Scene("Bass drop", "The keyboard swells with the music while neon comets race around it.",
                Fx(SceneEffect.Comet, ["FF00C8", "00E5FF", "FFE600"], SceneDirection.Clockwise, speed: 7, on: On.Edges, name: "Comets"),
                Fx(SceneEffect.AudioPulse, ["FF00C8", "8000FF", "00A0FF"], name: "Pulse")),

            Scene("Neon equalizer", "A cyan-to-pink spectrum analyser with comets lapping the frame.",
                Fx(SceneEffect.Comet, ["00FFF0", "FF00C8"], SceneDirection.CounterClockwise, speed: 6, on: On.Edges, name: "Comets"),
                Fx(SceneEffect.AudioSpectrum, ["00FFF0", "7000FF", "FF00C8"], on: On.Keys, name: "Bars"),
                Fx(SceneEffect.Static, ["05050F"], name: "Backdrop")),

            Scene("Audio pulse", "Light swells out from the centre with whatever is playing.",
                Fx(SceneEffect.AudioPulse)),

            Scene("Audio spectrum", "A spectrum analyser: bass on the left, treble on the right, green to red bars.",
                Fx(SceneEffect.AudioSpectrum, name: "Bars"),
                Fx(SceneEffect.Static, ["05050F"], name: "Backdrop")),

            Scene("Tornado rainbow", "A rainbow spiral whirling around the middle of the keyboard.",
                Fx(SceneEffect.Tornado, Rainbow, SceneDirection.Clockwise, speed: 5)),
        ]),

        ("Seasonal", [
            Scene("Christmas", "Red, green and gold lights twinkling like a Christmas tree.",
                Fx(SceneEffect.Twinkle, ["FF1010", "10FF10", "FFD700", "FFFFFF"], speed: 4)),

            Scene("Winter wonderland", "Snowflakes falling through a frosty blue night with twinkling lights.",
                Fx(SceneEffect.Snow, ["FFFFFF"], speed: 4, name: "Snow"),
                Fx(SceneEffect.Twinkle, ["BFE3FF", "0A1A3A"], speed: 2, name: "Frost")),

            Scene("New Year's Eve", "Gold and silver fireworks over a sparkling midnight blue.",
                Fx(SceneEffect.Fireworks, ["FFD700", "FFF4C2", "C0C0C0", "FFFFFF"], speed: 6, name: "Fireworks"),
                Fx(SceneEffect.Twinkle, ["FFE08A", "050A24"], speed: 3, name: "Sparkle")),

            Scene("Halloween", "Purple lightning over a haunted night, with orange, violet and toxic-green sparks rising.",
                Fx(SceneEffect.Embers, ["FF7A00", "B000FF", "39FF14"], speed: 4, name: "Spooky sparks"),
                Fx(SceneEffect.Lightning, ["B070FF", "0A0010"], speed: 4, name: "Purple lightning")),

            Scene("Valentine", "Pink and white hearts floating up over a slow, loving heartbeat.",
                Fx(SceneEffect.Bubbles, ["FF4D88", "FF99BB", "FFFFFF"], speed: 3, name: "Floating hearts"),
                Fx(SceneEffect.Heartbeat, ["FF1A5E", "200008"], speed: 3, name: "Heartbeat")),

            Scene("Spring bloom", "Blossom petals drifting over soft spring pastels.",
                Fx(SceneEffect.Snow, ["FFD1E8", "FFFFFF", "C8F7C5"], speed: 3, name: "Petals"),
                Fx(SceneEffect.Candy, ["B5EAD7", "FFDAC1", "E2F0CB", "C7CEEA"], SceneDirection.Right, speed: 2, brightness: 70, name: "Pastels")),
        ]),

        ("Typing", [
            Scene("Keystroke lightning", "Lightning arcs jump from each key to the next one you press.",
                Fx(SceneEffect.KeyLightning, ["7FDBFF"], speed: 5, name: "Arcs"),
                Fx(SceneEffect.Static, ["00030C"], name: "Night")),

            Scene("Storm typer", "White-violet lightning jumps between your keys inside a real thunderstorm.",
                Fx(SceneEffect.KeyLightning, ["FFFFFF", "B070FF"], speed: 6, name: "Arcs"),
                Fx(SceneEffect.Lightning, ["8FA8FF", "03030F"], speed: 4, brightness: 60, name: "Storm")),

            Scene("Laser typing", "Every key fires coloured laser beams along its row.",
                Fx(SceneEffect.LaserTyping, speed: 5, name: "Lasers"),
                Fx(SceneEffect.Static, ["050008"], name: "Dark")),

            Scene("Rainbow typing", "Each key you press lights up in the next colour of the rainbow and fades slowly.",
                Fx(SceneEffect.RainbowTyping, ["FF0000"], speed: 5, name: "Rainbow keys"),
                Fx(SceneEffect.Static, ["FFFFFF"], brightness: 4, name: "Dim base")),

            Scene("Combo meter", "Type fast and the frame fills like a fighting-game combo bar; flat out, everything turns rainbow.",
                Fx(SceneEffect.ComboMeter, speed: 5)),

            Scene("Welding sparks", "Sparks fly out of every key you hit and rain down.",
                Fx(SceneEffect.KeySparks, speed: 5, name: "Sparks"),
                Fx(SceneEffect.Static, ["0A0300"], name: "Workshop")),

            Scene("Typing heatmap", "Keys warm from cold blue to hot red the more you type on them.",
                Fx(SceneEffect.TypingHeatmap)),

            Scene("Ripple", "Every key press sends rings of light across a dark blue keyboard.",
                Fx(SceneEffect.Ripple, ["FFFFFF", "00A0FF"], speed: 5, name: "Rings"),
                Fx(SceneEffect.Static, ["000A2E"], name: "Dark blue")),

            Scene("Keystroke fireworks", "Every key press bursts into a white-gold-pink-violet ring over a sparkling night.",
                Fx(SceneEffect.Ripple, ["FFFFFF", "FFD000", "FF0080", "7000FF"], speed: 6, name: "Bursts"),
                Fx(SceneEffect.Twinkle, ["FFF4D6", "04030E"], speed: 2, name: "Night")),

            Scene("Electric keys", "Keys crackle white-cyan-violet and send blue shockwaves when pressed.",
                Fx(SceneEffect.Reactive, ["FFFFFF", "00E5FF", "7000FF"], speed: 6, on: On.Keys, name: "Sparks"),
                Fx(SceneEffect.Ripple, ["00A0FF"], speed: 7, name: "Shockwaves"),
                Fx(SceneEffect.Static, ["000814"], name: "Dark")),

            Scene("Lava keys", "Keys glow molten yellow to red as you press them, over slowly bubbling lava.",
                Fx(SceneEffect.Reactive, ["FFE08A", "FF6A00", "FF0000"], speed: 4, on: On.Keys, name: "Molten keys"),
                Fx(SceneEffect.Lava, speed: 3, brightness: 35, name: "Lava")),

            Scene("Ink drop", "Dark blue ink spreading through a bright white keyboard with every key press.",
                Fx(SceneEffect.Ripple, ["001A66", "0080FF"], speed: 4, name: "Ink"),
                Fx(SceneEffect.Static, ["E8F4FF"], brightness: 80, name: "Paper")),
        ]),

        ("System", [
            Scene("Performance meter", "CPU load on F1–F12, GPU load on the number row, CPU temperature on the edges.",
                Fx(SceneEffect.PerformanceMeter, ["FF2800"])),

            Scene("CPU temperature", "The whole keyboard shows the CPU temperature: green when cool, red (pulsing) when hot.",
                Fx(SceneEffect.CpuTemperature)),

            Scene("Thermal frame", "White keys for work; the frame shows the CPU temperature from green to red.",
                Fx(SceneEffect.CpuTemperature, on: On.Edges, name: "CPU temperature"),
                Fx(SceneEffect.Static, ["FFFFFF"], brightness: 40, on: On.Keys, name: "White keys")),

        ]),

        ("Screen & mouse", [
            Scene("Screen sync (Ambilight)", "Keys mirror the screen above them and the edge lights glow with its borders.",
                Fx(SceneEffect.ScreenSync)),

            Scene("Screen mood", "The whole keyboard glows in your screen's overall colour.",
                Fx(SceneEffect.ScreenMood)),

            Scene("Game flashes", "Explosions and muzzle flashes on screen flash the keyboard, over the screen's mood colour.",
                Fx(SceneEffect.ScreenFlash, speed: 5, name: "Flashes"),
                Fx(SceneEffect.ScreenMood, brightness: 60, name: "Mood")),

            Scene("Ambilight typing", "Screen sync, plus a white flash on every key you press.",
                Fx(SceneEffect.Reactive, ["FFFFFF"], speed: 6, on: On.Keys, name: "Key flash"),
                Fx(SceneEffect.ScreenSync, name: "Screen sync")),

            Scene("Mouse spotlight", "The keyboard becomes a mini-map of your screens: a spotlight follows your mouse, clicks send ripples.",
                Fx(SceneEffect.MouseSpotlight, speed: 5, name: "Spotlight"),
                Fx(SceneEffect.Static, ["02030A"], name: "Dark")),

            Scene("Cursor radar", "A green radar sweeps the keyboard while a blip tracks your mouse pointer.",
                Fx(SceneEffect.MouseSpotlight, ["B6FFB6", "FFE600"], speed: 5, name: "Blip"),
                Fx(SceneEffect.Radar, speed: 4, brightness: 70, name: "Radar")),
        ]),
    ];
}
