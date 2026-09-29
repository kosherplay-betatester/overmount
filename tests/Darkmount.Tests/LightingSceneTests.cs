using System.Diagnostics;
using System.Text.Json;
using Darkmount.Keyboard;
using Darkmount.Keyboard.Lamps;

namespace Darkmount.Tests;

public class LightingSceneTests
{
    const int KeyW = 17, KeyA = 30, KeyS = 31, KeyD = 32;

    // ---------------------------------------------------------------- lamp layouts

    /// <summary>
    /// A Dark Mount-like LampArray: lamps 0–104 are the keys (InputBinding = key id 1–105), lamps 105–200 the 96 edge LEDs
    /// (bindings 106–201) on the perimeter, run through the real <see cref="RgbEffects.Layout"/>.
    /// </summary>
    static readonly IReadOnlyList<LampPoint> Board = BuildBoard();

    static IReadOnlyList<LampPoint> BuildBoard()
    {
        const int scale = 170; // layout units → µm (roughly)
        var rects = KeyGeometry.Keys(PhysicalLayout.Iso, NumpadSide.Right).Where(r => r.KeyId <= 105).ToDictionary(r => (int)r.KeyId);
        var lamps = new List<LampInfo>();
        for (int keyId = 1; keyId <= 105; keyId++)
        {
            var r = rects[keyId];
            lamps.Add(Lamp(lamps.Count, (r.X + r.Width / 2) * scale, (r.Y + r.Height / 2) * scale, keyId));
        }
        int w = 2600 * scale, h = 890 * scale, binding = DarkmountKeys.FirstEdgeLightBinding;
        for (int i = 0; i < 34; i++) lamps.Add(Lamp(lamps.Count, w * i / 33, 0, binding++));      // top
        for (int i = 0; i < 14; i++) lamps.Add(Lamp(lamps.Count, w, h * (i + 1) / 15, binding++)); // right
        for (int i = 0; i < 34; i++) lamps.Add(Lamp(lamps.Count, w - w * i / 33, h, binding++));  // bottom
        for (int i = 0; i < 14; i++) lamps.Add(Lamp(lamps.Count, 0, h - h * (i + 1) / 15, binding++)); // left
        return RgbEffects.Layout(lamps, LampMap.Build(lamps, LampBindingKind.DarkmountKeyId));
    }

    static LampInfo Lamp(int id, int x, int y, int binding) =>
        new(id, x, y, 0, 1000, LampPurposes.Illumination, 255, 255, 255, null, true, binding);

    static int LampOf(int keyId) => Board.First(l => l.KeyId == keyId).LampId;

    static IEnumerable<LampPoint> Edges => Board.Where(l => !l.IsKey);

    /// <summary>One row of 51 keys along X at mid height (key ids 1–51).</summary>
    static readonly IReadOnlyList<LampPoint> Row =
        Enumerable.Range(0, 51).Select(i => new LampPoint(1000 + i, i / 50.0, 0.5, true, i + 1)).ToList();

    static SceneContext Ctx(double t = 0, IReadOnlyList<LampPoint>? lamps = null) => new() { Seconds = t, Lamps = lamps ?? Board };

    static SceneContext FullCtx(double t) => new()
    {
        Seconds = t,
        Lamps = Board,
        KeyPressTimes = new Dictionary<int, double> { [KeyW] = t - 0.2, [57] = t - 0.5, [KeyIds.Esc] = t - 3 },
        KeyHeat = new Dictionary<int, double> { [KeyW] = 1, [KeyA] = 0.6, [KeyS] = 0.3, [57] = 0.9 },
        CpuTemp = 67, CpuLoad = 42, GpuTemp = 55, GpuLoad = 80,
        AudioLevel = 0.7,
        AudioBands = [0.9, 0.7, 0.5, 0.6, 0.3, 0.2, 0.1, 0.05],
        ScreenGridWidth = 4, ScreenGridHeight = 2,
        ScreenGrid = [.. Enumerable.Range(0, 8).Select(i => new LampColor((byte)(i * 30), 80, (byte)(255 - i * 30)))],
    };

    static LightingScene Scene(params LightLayer[] layers) => new() { Layers = [.. layers] };

    static LightLayer Layer(SceneEffect effect, params string[] colors)
    {
        var layer = new LightLayer { Effect = effect };
        if (colors.Length > 0)
        {
            layer.Colors = [.. colors];
            layer.ColorMode = colors.Length switch { 1 => SceneColorMode.Single, 2 => SceneColorMode.Dual, _ => SceneColorMode.Gradient };
        }
        return layer;
    }

    static (int R, int G, int B) Rgb(LampColor c) => (c.R, c.G, c.B);
    static int Max(LampColor c) => Math.Max(c.R, Math.Max(c.G, c.B));

    // ---------------------------------------------------------------- layout

    [Fact]
    public void Layout_fills_key_ids_for_the_105_keys()
    {
        Assert.Equal(201, Board.Count);
        var keys = Board.Where(l => l.IsKey).ToList();
        Assert.Equal(105, keys.Count);
        Assert.Equal(Enumerable.Range(1, 105), keys.Select(k => k.KeyId).Order());
        Assert.All(Edges, e => Assert.Equal(0, e.KeyId));
        Assert.All(Board, l => Assert.InRange(l.X, 0, 1));
        Assert.All(Board, l => Assert.InRange(l.Y, 0, 1));
    }

    // ---------------------------------------------------------------- catalogue

    [Fact]
    public void Every_effect_has_exactly_one_catalogue_entry()
    {
        Assert.Equal(Enum.GetValues<SceneEffect>().Order(), SceneEffects.All.Select(i => i.Effect).Order());
        foreach (var e in Enum.GetValues<SceneEffect>())
        {
            var info = SceneEffects.Get(e);
            Assert.Equal(e, info.Effect);
            Assert.False(string.IsNullOrWhiteSpace(info.Name));
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
        }
        Assert.Equal(SceneEffects.All.Count, SceneEffects.All.Select(i => i.Name).Distinct().Count());
        Assert.False(SceneEffects.Get(SceneEffect.Reactive).IsOpaque);
        Assert.True(SceneEffects.Get(SceneEffect.Reactive).NeedsKeyPresses);
        Assert.True(SceneEffects.Get(SceneEffect.CpuTemperature).NeedsSensors);
        Assert.True(SceneEffects.Get(SceneEffect.AudioSpectrum).NeedsAudio);
        Assert.True(SceneEffects.Get(SceneEffect.ScreenSync).NeedsScreen);
        Assert.Equal([SceneEffect.ScreenSync, SceneEffect.ScreenMood, SceneEffect.ScreenFlash],
            SceneEffects.All.Where(i => i.NeedsScreen).Select(i => i.Effect));
        Assert.Equal([SceneEffect.MouseSpotlight], SceneEffects.All.Where(i => i.NeedsMouse).Select(i => i.Effect));
        Assert.True(SceneEffects.Get(SceneEffect.Static).IsOpaque);
    }

    [Fact]
    public void CreateLayer_uses_the_effect_defaults()
    {
        var layer = SceneEffects.CreateLayer(SceneEffect.Matrix);
        var info = SceneEffects.Get(SceneEffect.Matrix);
        Assert.Equal(SceneEffect.Matrix, layer.Effect);
        Assert.Equal(info.DefaultColors, layer.Colors);
        Assert.Contains(layer.ColorMode, info.ColorModes);
        Assert.Equal("Matrix", layer.Name);
    }

    // ---------------------------------------------------------------- every effect renders

    public static TheoryData<SceneEffect> AllEffects() => [.. Enum.GetValues<SceneEffect>()];

    [Theory]
    [MemberData(nameof(AllEffects))]
    public void Every_effect_colours_every_lamp_in_all_modes_and_contexts(SceneEffect effect)
    {
        var info = SceneEffects.Get(effect);
        var modes = info.ColorModes.Count > 0 ? info.ColorModes : [SceneColorMode.Single];
        var directions = info.Directions.Count > 0 ? info.Directions : [SceneDirection.Right];
        string[] gradient = ["FF0000", "00FF00", "0000FF", "FFFFFF", "000000", "FF00FF", "00FFFF"];
        foreach (var mode in modes)
            foreach (var dir in directions)
                foreach (double t in new[] { 0, 0.37, 1.5, 12.3, 1000.01 })
                    foreach (var ctx in new[] { Ctx(t), FullCtx(t), Ctx(t, Row), Ctx(t, []) })
                        foreach (int speed in new[] { 1, 10 })
                        {
                            var layer = new LightLayer
                            {
                                Effect = effect, ColorMode = mode, Direction = dir, Speed = speed,
                                Colors = mode switch { SceneColorMode.Single => ["00FF80"], SceneColorMode.Dual => ["FF0000", "0000FF"], _ => [.. gradient] },
                            };
                            var colors = SceneRenderer.Render(Scene(layer), ctx);
                            Assert.Equal(ctx.Lamps.Select(l => l.LampId).Order(), colors.Keys.Order());
                        }
    }

    [Theory]
    [MemberData(nameof(AllEffects))]
    public void Opaque_effects_cover_their_lamps_and_rendering_is_deterministic(SceneEffect effect)
    {
        var info = SceneEffects.Get(effect);
        foreach (double t in new[] { 0.5, 3.25, 71.9 })
        {
            var layer = SceneEffects.CreateLayer(effect);
            var onBlack = SceneRenderer.Render(new LightingScene { Layers = [layer], Background = "000000" }, FullCtx(t));
            var onWhite = SceneRenderer.Render(new LightingScene { Layers = [layer], Background = "FFFFFF" }, FullCtx(t));
            if (info.IsOpaque) Assert.Equal(onBlack, onWhite);
            Assert.Equal(onBlack, SceneRenderer.Render(new LightingScene { Layers = [layer.Clone()] }, FullCtx(t)));
        }
    }

    [Theory]
    [InlineData(SceneEffect.ColorWave)] [InlineData(SceneEffect.Tornado)] [InlineData(SceneEffect.Breathing)]
    [InlineData(SceneEffect.Matrix)] [InlineData(SceneEffect.Rainbow)] [InlineData(SceneEffect.Plasma)]
    [InlineData(SceneEffect.Aurora)] [InlineData(SceneEffect.Fire)] [InlineData(SceneEffect.Ocean)]
    [InlineData(SceneEffect.Twinkle)] [InlineData(SceneEffect.Rain)] [InlineData(SceneEffect.Heartbeat)]
    [InlineData(SceneEffect.Police)] [InlineData(SceneEffect.Scanner)] [InlineData(SceneEffect.ColorCycle)]
    [InlineData(SceneEffect.Lava)] [InlineData(SceneEffect.Candy)]
    [InlineData(SceneEffect.Starfield)] [InlineData(SceneEffect.Fireworks)] [InlineData(SceneEffect.Comet)]
    [InlineData(SceneEffect.Glitch)] [InlineData(SceneEffect.Radar)] [InlineData(SceneEffect.Disco)] [InlineData(SceneEffect.Snow)]
    [InlineData(SceneEffect.Bubbles)] [InlineData(SceneEffect.Embers)] [InlineData(SceneEffect.Fireflies)]
    public void Animated_effects_change_over_time(SceneEffect effect)
    {
        var scene = Scene(SceneEffects.CreateLayer(effect));
        var a = SceneRenderer.Render(scene, Ctx(1.0));
        var b = SceneRenderer.Render(scene, Ctx(1.33));
        Assert.Contains(Board, l => a[l.LampId] != b[l.LampId]);
    }

    // ---------------------------------------------------------------- colours, brightness, background

    [Fact]
    public void Static_is_exactly_the_colour()
    {
        var colors = SceneRenderer.Render(Scene(Layer(SceneEffect.Static, "FF8000")), Ctx(5));
        Assert.All(colors.Values, c => Assert.Equal(new LampColor(255, 128, 0), c));
    }

    [Fact]
    public void Brightness_scales_the_colour()
    {
        var layer = Layer(SceneEffect.Static, "FF8000");
        layer.Brightness = 50;
        Assert.All(SceneRenderer.Render(Scene(layer), Ctx()).Values, c => Assert.Equal((128, 64, 0), Rgb(c)));
    }

    [Fact]
    public void Brightness_zero_and_disabled_layers_have_no_effect()
    {
        var off = Layer(SceneEffect.Static, "FF0000");
        off.Brightness = 0;
        var disabled = Layer(SceneEffect.Static, "00FF00");
        disabled.Enabled = false;
        var scene = new LightingScene { Layers = [off, disabled], Background = "102030" };

        Assert.All(SceneRenderer.Render(scene, Ctx()).Values, c => Assert.Equal((0x10, 0x20, 0x30), Rgb(c)));
    }

    [Fact]
    public void Lamps_no_layer_covers_show_the_background()
    {
        var keysOnly = Layer(SceneEffect.Static, "FF0000");
        keysOnly.AllEdges = false;
        var colors = SceneRenderer.Render(new LightingScene { Layers = [keysOnly], Background = "0000FF" }, Ctx());

        Assert.All(Board.Where(l => l.IsKey), l => Assert.Equal((255, 0, 0), Rgb(colors[l.LampId])));
        Assert.All(Edges, l => Assert.Equal((0, 0, 255), Rgb(colors[l.LampId])));
    }

    [Fact]
    public void Invalid_colours_fall_back_instead_of_throwing()
    {
        var bad = Layer(SceneEffect.Static, "not a colour");
        var scene = new LightingScene { Layers = [bad], Background = "zz" };
        var c = SceneRenderer.Render(scene, Ctx())[LampOf(KeyW)];
        Assert.Equal(SceneRenderer.Render(Scene(Layer(SceneEffect.Static, SceneEffects.Get(SceneEffect.Static).DefaultColors[0])), Ctx())[LampOf(KeyW)], c);

        var hashed = Layer(SceneEffect.Static, "#00ff00");
        Assert.Equal((0, 255, 0), Rgb(SceneRenderer.Render(Scene(hashed), Ctx())[LampOf(KeyW)]));
    }

    [Fact]
    public void Static_gradient_spans_the_direction()
    {
        var layer = Layer(SceneEffect.Static, "FF0000", "0000FF");
        layer.Direction = SceneDirection.Right;
        var colors = SceneRenderer.Render(Scene(layer), Ctx(0, Row));
        Assert.Equal((255, 0, 0), Rgb(colors[1000]));
        Assert.Equal((0, 0, 255), Rgb(colors[1050]));

        layer.Direction = SceneDirection.Left;
        colors = SceneRenderer.Render(Scene(layer), Ctx(0, Row));
        Assert.Equal((0, 0, 255), Rgb(colors[1000]));
    }

    // ---------------------------------------------------------------- compositing and targeting

    [Fact]
    public void Top_layer_wins_where_it_is_opaque()
    {
        var red = Layer(SceneEffect.Static, "FF0000");
        red.AllEdges = false;
        var blue = Layer(SceneEffect.Static, "0000FF");

        var colors = SceneRenderer.Render(Scene(red, blue), Ctx());
        Assert.All(Board.Where(l => l.IsKey), l => Assert.Equal((255, 0, 0), Rgb(colors[l.LampId])));
        Assert.All(Edges, l => Assert.Equal((0, 0, 255), Rgb(colors[l.LampId])));

        colors = SceneRenderer.Render(Scene(blue, red), Ctx()); // blue on top covers everything
        Assert.All(colors.Values, c => Assert.Equal((0, 0, 255), Rgb(c)));
    }

    [Fact]
    public void Layers_only_touch_their_selected_keys_and_edge_lamps()
    {
        int edge = Edges.First().LampId;
        var layer = Layer(SceneEffect.Static, "00FF00");
        layer.AllKeys = false;
        layer.Keys = [KeyW];
        layer.AllEdges = false;
        layer.EdgeLamps = [edge];

        var colors = SceneRenderer.Render(Scene(layer), Ctx());
        foreach (var l in Board)
            Assert.Equal(l.LampId == LampOf(KeyW) || l.LampId == edge ? (0, 255, 0) : (0, 0, 0), Rgb(colors[l.LampId]));
    }

    [Fact]
    public void TargetLamps_resolves_keys_and_edges()
    {
        Assert.Equal(201, SceneRenderer.TargetLamps(new LightLayer(), Board).Count);
        Assert.Equal(105, SceneRenderer.TargetLamps(new LightLayer { AllEdges = false }, Board).Count);
        Assert.Equal(96, SceneRenderer.TargetLamps(new LightLayer { AllKeys = false }, Board).Count);
        Assert.Equal([LampOf(KeyW), LampOf(KeyA)],
            SceneRenderer.TargetLamps(new LightLayer { AllKeys = false, Keys = [KeyA, KeyW, 999], AllEdges = false }, Board).Order());
        int edge = Edges.Last().LampId;
        Assert.Equal([edge],
            SceneRenderer.TargetLamps(new LightLayer { AllKeys = false, AllEdges = false, EdgeLamps = [edge, LampOf(KeyW)] }, Board));
        Assert.Empty(SceneRenderer.TargetLamps(new LightLayer { AllKeys = false, AllEdges = false }, Board));
    }

    [Fact]
    public void Only_the_first_eight_layers_are_drawn()
    {
        var layers = Enumerable.Range(0, LightingScene.MaxLayers)
            .Select(_ => new LightLayer { AllKeys = false, AllEdges = false }).ToList();
        layers.Add(Layer(SceneEffect.Static, "FF0000"));
        var colors = SceneRenderer.Render(new LightingScene { Layers = layers }, Ctx());
        Assert.All(colors.Values, c => Assert.Equal((0, 0, 0), Rgb(c)));
    }

    // ---------------------------------------------------------------- reactive / ripple

    [Fact]
    public void Reactive_is_transparent_until_a_key_is_pressed_then_fades()
    {
        var reactive = Layer(SceneEffect.Reactive, "FFFFFF");
        var scene = Scene(reactive, Layer(SceneEffect.Static, "FF0000"));
        int w = LampOf(KeyW);

        Assert.All(SceneRenderer.Render(scene, Ctx(10)).Values, c => Assert.Equal((255, 0, 0), Rgb(c)));

        SceneContext Pressed(double t) => new() { Seconds = t, Lamps = Board, KeyPressTimes = new Dictionary<int, double> { [KeyW] = 10 } };
        var atPress = SceneRenderer.Render(scene, Pressed(10));
        Assert.Equal((255, 255, 255), Rgb(atPress[w]));
        Assert.Equal((255, 0, 0), Rgb(atPress[LampOf(KeyA)]));

        var fading = SceneRenderer.Render(scene, Pressed(10.3))[w];
        Assert.Equal(255, fading.R);
        Assert.InRange(fading.G, 1, 254);
        Assert.True(SceneRenderer.Render(scene, Pressed(10.6))[w].G < fading.G);

        Assert.Equal((255, 0, 0), Rgb(SceneRenderer.Render(scene, Pressed(15))[w]));
        Assert.Equal((255, 0, 0), Rgb(SceneRenderer.Render(scene, Pressed(9.5))[w])); // press in the future: ignored
    }

    [Fact]
    public void Reactive_dual_starts_with_the_first_colour_and_speed_shortens_the_fade()
    {
        var slow = Layer(SceneEffect.Reactive, "00FF00", "0000FF");
        slow.Speed = 1;
        var fast = slow.Clone();
        fast.Speed = 10;
        SceneContext Pressed(double t) => new() { Seconds = t, Lamps = Board, KeyPressTimes = new Dictionary<int, double> { [KeyW] = 3 } };
        int w = LampOf(KeyW);

        Assert.Equal((0, 255, 0), Rgb(SceneRenderer.Render(Scene(slow), Pressed(3))[w]));
        Assert.True(Max(SceneRenderer.Render(Scene(slow), Pressed(3.8))[w]) > 0);
        Assert.Equal((0, 0, 0), Rgb(SceneRenderer.Render(Scene(fast), Pressed(3.8))[w]));
    }

    [Fact]
    public void Ripple_ring_grows_with_time_and_is_transparent_elsewhere()
    {
        var scene = Scene(Layer(SceneEffect.Ripple, "FFFFFF"));
        Assert.All(SceneRenderer.Render(scene, Ctx(4, Row)).Values, c => Assert.Equal((0, 0, 0), Rgb(c)));

        double RingRadius(double age)
        {
            var ctx = new SceneContext { Seconds = 4 + age, Lamps = Row, KeyPressTimes = new Dictionary<int, double> { [26] = 4 } }; // key 26 at x = 0.5
            var colors = SceneRenderer.Render(scene, ctx);
            var brightest = Row.Where(l => l.X > 0.5).MaxBy(l => Max(colors[l.LampId]));
            return brightest.X - 0.5;
        }

        double r1 = RingRadius(0.1), r2 = RingRadius(0.3), r3 = RingRadius(0.5);
        Assert.True(r1 < r2 && r2 < r3, $"{r1} {r2} {r3}");
        var early = SceneRenderer.Render(scene, new SceneContext { Seconds = 4.1, Lamps = Row, KeyPressTimes = new Dictionary<int, double> { [26] = 4 } });
        Assert.Equal((0, 0, 0), Rgb(early[1050])); // the far end is still dark
    }

    // ---------------------------------------------------------------- sensors, typing, audio, screen

    [Fact]
    public void PerformanceMeter_lights_more_function_keys_at_higher_cpu_load()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.PerformanceMeter));
        int LitFKeys(double load, double? gpu = null)
        {
            var colors = SceneRenderer.Render(scene, new SceneContext { Seconds = 1, Lamps = Board, CpuLoad = load, GpuLoad = gpu });
            return Enumerable.Range(KeyIds.F1, 12).Count(id => Max(colors[LampOf(id)]) > 128);
        }

        Assert.Equal(0, LitFKeys(0));
        Assert.Equal(3, LitFKeys(25));
        Assert.Equal(9, LitFKeys(75));
        Assert.Equal(12, LitFKeys(100));

        var gpuFull = SceneRenderer.Render(scene, new SceneContext { Seconds = 1, Lamps = Board, GpuLoad = 100 });
        Assert.All(Enumerable.Range(2, 10), id => Assert.True(Max(gpuFull[LampOf(id)]) > 128)); // number row 1–0
        Assert.True(Max(gpuFull[LampOf(KeyW)]) < 80); // the rest is dim
        var one = gpuFull[LampOf(2)];
        var zero = gpuFull[LampOf(11)];
        Assert.True(one.G > one.R && zero.R > zero.G, "the bar goes green → red");
    }

    [Fact]
    public void PerformanceMeter_edges_show_cpu_temperature()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.PerformanceMeter));
        var hot = SceneRenderer.Render(scene, new SceneContext { Seconds = 1, Lamps = Board, CpuTemp = 95 });
        var cool = SceneRenderer.Render(scene, new SceneContext { Seconds = 1, Lamps = Board, CpuTemp = 35 });
        int edge = Edges.First().LampId;
        Assert.True(hot[edge].R > hot[edge].G);
        Assert.True(cool[edge].G > cool[edge].R);
    }

    [Fact]
    public void CpuTemperature_goes_green_to_red_and_blue_when_unknown()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.CpuTemperature));
        var cool = SceneRenderer.Render(scene, new SceneContext { Seconds = 1, Lamps = Board, CpuTemp = 40 })[LampOf(KeyW)];
        var hot = SceneRenderer.Render(scene, new SceneContext { Seconds = 1, Lamps = Board, CpuTemp = 92 })[LampOf(KeyW)];
        var unknown = SceneRenderer.Render(scene, Ctx(1))[LampOf(KeyW)];

        Assert.Equal((0, 255, 0), Rgb(cool));
        Assert.True(hot.R > 100 && hot.G < 30 && hot.B < 30);
        Assert.True(unknown.B > unknown.R && unknown.B > unknown.G);
    }

    [Fact]
    public void TypingHeatmap_is_hot_red_for_busy_keys_and_cold_blue_for_idle_keys()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.TypingHeatmap));
        var colors = SceneRenderer.Render(scene, new SceneContext
        {
            Seconds = 1, Lamps = Board, KeyHeat = new Dictionary<int, double> { [KeyW] = 1.0, [KeyA] = 0.0 },
        });
        var hot = colors[LampOf(KeyW)];
        var cold = colors[LampOf(KeyA)];
        Assert.True(hot.R > hot.B && hot.R > 200);
        Assert.True(cold.B > cold.R);
        Assert.Equal(cold, colors[LampOf(KeyD)]); // no entry = cold
    }

    [Fact]
    public void AudioSpectrum_bar_heights_follow_the_bands()
    {
        // 5 columns × 6 rows of keys.
        var grid = (from c in Enumerable.Range(0, 5) from r in Enumerable.Range(0, 6)
                    select new LampPoint(c * 10 + r, (c + 0.5) / 5, r / 5.0, true, c * 10 + r + 1)).ToList();
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.AudioSpectrum));
        var colors = SceneRenderer.Render(scene, new SceneContext { Seconds = 2, Lamps = grid, AudioBands = [0, 0.3, 0.55, 0.85, 1.0] });

        int[] lit = [.. Enumerable.Range(0, 5).Select(c => grid.Count(l => l.LampId / 10 == c && Max(colors[l.LampId]) > 127))];
        Assert.Equal(0, lit[0]);
        Assert.Equal(6, lit[4]);
        for (int c = 1; c < 5; c++) Assert.True(lit[c] > lit[c - 1], string.Join(",", lit));

        // Bars rise from the bottom: in a half-full column the bottom key is lit, the top key is not.
        Assert.True(Max(colors[2 * 10 + 5]) > 127);
        Assert.True(Max(colors[2 * 10 + 0]) < 20);

        // No bands: derived from the overall level.
        var quiet = SceneRenderer.Render(scene, new SceneContext { Seconds = 2, Lamps = grid, AudioLevel = 0 });
        var loud = SceneRenderer.Render(scene, new SceneContext { Seconds = 2, Lamps = grid, AudioLevel = 1 });
        Assert.True(loud.Values.Sum(Max) > quiet.Values.Sum(Max));
    }

    [Fact]
    public void AudioPulse_brightness_follows_the_level()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.AudioPulse));
        int Total(double level) => SceneRenderer.Render(scene, new SceneContext { Seconds = 1, Lamps = Board, AudioLevel = level }).Values.Sum(Max);
        Assert.True(Total(0.1) < Total(0.5));
        Assert.True(Total(0.5) < Total(0.95));
    }

    [Fact]
    public void ScreenSync_takes_the_colour_of_the_matching_screen_region()
    {
        const int w = 24, h = 8;
        var grid = new LampColor[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                grid[y * w + x] = x < w / 2 ? new LampColor(255, 0, 0) : new LampColor(0, 0, 255);
        var ctx = new SceneContext { Seconds = 1, Lamps = Board, ScreenGrid = grid, ScreenGridWidth = w, ScreenGridHeight = h };
        var colors = SceneRenderer.Render(Scene(SceneEffects.CreateLayer(SceneEffect.ScreenSync)), ctx);

        Assert.All(Board.Where(l => l.X < 0.45), l => Assert.Equal((255, 0, 0), Rgb(colors[l.LampId])));
        Assert.All(Board.Where(l => l.X > 0.55), l => Assert.Equal((0, 0, 255), Rgb(colors[l.LampId])));
    }

    [Fact]
    public void ScreenSync_edges_sample_the_screen_border()
    {
        const int w = 3, h = 3;
        var grid = new LampColor[w * h];
        grid[1] = new LampColor(255, 0, 0);            // top middle
        grid[3] = new LampColor(0, 255, 0);            // left middle
        grid[4] = new LampColor(255, 255, 255);        // centre: never an edge
        var ctx = new SceneContext { Seconds = 1, Lamps = Board, ScreenGrid = grid, ScreenGridWidth = w, ScreenGridHeight = h };
        var colors = SceneRenderer.Render(Scene(SceneEffects.CreateLayer(SceneEffect.ScreenSync)), ctx);

        var topMiddle = Edges.Where(e => e.Y < 0.01).MinBy(e => Math.Abs(e.X - 0.5));
        var leftMiddle = Edges.Where(e => e.X < 0.01).MinBy(e => Math.Abs(e.Y - 0.5));
        Assert.Equal((255, 0, 0), Rgb(colors[topMiddle.LampId]));
        Assert.Equal((0, 255, 0), Rgb(colors[leftMiddle.LampId]));
        Assert.DoesNotContain(Edges, e => Rgb(colors[e.LampId]) == (255, 255, 255));
    }

    [Fact]
    public void ScreenSync_without_a_grid_falls_back_to_a_dim_background()
    {
        var scene = new LightingScene { Layers = [SceneEffects.CreateLayer(SceneEffect.ScreenSync)], Background = "204080" };
        var c = SceneRenderer.Render(scene, Ctx(1))[LampOf(KeyW)];
        Assert.True(c.B > c.R && c.B <= 0x80);
    }

    // ---------------------------------------------------------------- directions

    [Fact]
    public void ColorWave_moves_in_its_direction()
    {
        var wave = Layer(SceneEffect.ColorWave, "FF0000", "00FF00", "0000FF");
        wave.Direction = SceneDirection.Right;
        var right = SceneRenderer.Render(Scene(wave), Ctx(1, Row));
        var rightLater = SceneRenderer.Render(Scene(wave), Ctx(1.2, Row));
        wave.Direction = SceneDirection.Left;
        var left = SceneRenderer.Render(Scene(wave), Ctx(1, Row));

        // Moving right: what lamp i shows now, lamp i + k shows a little later.
        int shift = Enumerable.Range(1, 20).MinBy(k => Diff(right[1010], rightLater[1010 + k]));
        Assert.True(Diff(right[1010], rightLater[1010 + shift]) < Diff(right[1010], rightLater[1010]));
        Assert.NotEqual(right[1005], left[1005]);
    }

    [Fact]
    public void Tornado_turns_both_ways()
    {
        var cw = Layer(SceneEffect.Tornado, "FF0000", "00FF00", "0000FF");
        cw.Direction = SceneDirection.Clockwise;
        var ccw = cw.Clone();
        ccw.Direction = SceneDirection.CounterClockwise;
        Assert.Contains(Board, l => SceneRenderer.Render(Scene(cw), Ctx(2))[l.LampId] != SceneRenderer.Render(Scene(ccw), Ctx(2))[l.LampId]);
    }

    static int Diff(LampColor a, LampColor b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    // ---------------------------------------------------------------- presets

    [Fact]
    public void Presets_are_plentiful_unique_and_valid()
    {
        var presets = ScenePresets.All;
        Assert.True(presets.Count >= 24, $"{presets.Count} presets");
        Assert.Equal(presets.Count, presets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var p in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Description), p.Name);
            Assert.InRange(p.Layers.Count, 1, LightingScene.MaxLayers);
            foreach (var layer in p.Layers)
            {
                Assert.InRange(layer.Colors.Count, 1, 7);
                Assert.All(layer.Colors, c => Assert.Matches("^[0-9A-F]{6}$", c));
                Assert.InRange(layer.Speed, 1, 10);
                Assert.InRange(layer.Brightness, 1, 100);
            }
            // The demo feed types, plays music, moves the mouse and flashes the screen, so reactive presets light up too.
            var frames = new[] { 0.02, 0.9, 1.7, 3.3 }.Select(t => SceneRenderer.Render(p, SceneDemo.Context(t + 5, Board))).ToList();
            Assert.All(frames, colors => Assert.Equal(201, colors.Count));
            Assert.True(frames.Any(colors => colors.Values.Any(c => Max(c) > 40)), $"{p.Name} stays dark"); // Police strobes
        }
    }

    [Theory]
    [InlineData("Rainbow wave")] [InlineData("Cyberpunk")] [InlineData("Gamer WASD")] [InlineData("be quiet! orange")]
    [InlineData("Knight Rider")] [InlineData("Screen sync (Ambilight)")] [InlineData("Typing heatmap")]
    public void Named_presets_exist(string name) => Assert.Contains(ScenePresets.All, p => p.Name == name);

    [Fact]
    public void Gamer_WASD_highlights_movement_keys_and_reacts_to_presses()
    {
        var scene = ScenePresets.All.Single(p => p.Name == "Gamer WASD");
        var idle = SceneRenderer.Render(scene, Ctx(1));
        foreach (int id in new[] { KeyW, KeyA, KeyS, KeyD, KeyIds.Up, KeyIds.Left, KeyIds.Down, KeyIds.Right })
            Assert.True(idle[LampOf(id)].R > 200, $"key {id}");
        Assert.True(Max(idle[LampOf(KeyIds.Esc)]) < 100);

        var pressed = SceneRenderer.Render(scene, new SceneContext
        {
            Seconds = 1, Lamps = Board, KeyPressTimes = new Dictionary<int, double> { [KeyIds.Esc] = 1 },
        });
        Assert.Equal((255, 255, 255), Rgb(pressed[LampOf(KeyIds.Esc)]));
    }

    [Fact]
    public void Lightning_is_a_dark_sky_with_occasional_bright_flashes()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.Lightning));
        var frames = Enumerable.Range(0, 400).Select(i => SceneRenderer.Render(scene, Ctx(i * 0.05))).ToList(); // 20 s
        Assert.Contains(frames, f => f.Values.Any(c => Max(c) > 200));            // a strike
        Assert.Contains(frames, f => f.Values.All(c => Max(c) < 40));              // and calm between strikes
    }

    [Fact]
    public void Presets_are_grouped_into_categories_and_the_default_comes_first()
    {
        var categories = ScenePresets.Categories;
        Assert.True(categories.Count >= 6);
        Assert.All(categories, c => Assert.NotEmpty(c.Scenes));
        Assert.Equal(ScenePresets.All.Select(p => p.Name), categories.SelectMany(c => c.Scenes).Select(p => p.Name));
        Assert.Equal("Rainbow wave", ScenePresets.All[0].Name); // the scene new users start with
        Assert.True(ScenePresets.All.Count >= 70, $"{ScenePresets.All.Count} presets");
        // Every effect except the special ones appears in at least one preset.
        var used = ScenePresets.All.SelectMany(p => p.Layers).Select(l => l.Effect).ToHashSet();
        Assert.All(Enum.GetValues<SceneEffect>().Where(e => e is not SceneEffect.PerKey), e => Assert.Contains(e, used));
    }

    [Fact]
    public void Combo_meter_fills_further_the_faster_you_type()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.ComboMeter));
        int LitEdges(int presses)
        {
            var ctx = new SceneContext
            {
                Seconds = 10, Lamps = Board,
                KeyPresses = [.. Enumerable.Range(0, presses).Select(i => new KeyPress(KeyW, 10 - i * 0.15))],
            };
            var frame = SceneRenderer.Render(scene, ctx);
            return Board.Count(l => !l.IsKey && Max(frame[l.LampId]) > 60);
        }
        Assert.True(LitEdges(0) < LitEdges(4), "a few presses fill some of the bar");
        Assert.True(LitEdges(4) < LitEdges(12), "fast typing fills more");
    }

    [Fact]
    public void Mouse_spotlight_is_brightest_under_the_pointer_and_off_without_a_mouse()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.MouseSpotlight));
        var ctx = new SceneContext { Seconds = 3, Lamps = Board, MouseX = 0.1, MouseY = 0.5 };
        var frame = SceneRenderer.Render(scene, ctx);
        var near = Board.Where(l => l.IsKey).MinBy(l => Math.Abs(l.X - 0.1) + Math.Abs(l.Y - 0.5));
        var far = Board.Where(l => l.IsKey).MaxBy(l => Math.Abs(l.X - 0.1));
        Assert.True(Max(frame[near.LampId]) > 150);
        Assert.True(Max(frame[far.LampId]) < 30);
        Assert.All(SceneRenderer.Render(scene, Ctx(3)).Values, c => Assert.True(Max(c) == 0)); // no pointer known: dark
    }

    [Fact]
    public void Keystroke_lightning_links_the_last_two_keys()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.KeyLightning));
        var ctx = new SceneContext
        {
            Seconds = 5, Lamps = Board, KeyPresses = [new KeyPress(KeyA, 4.9), new KeyPress(KeyIds.Right, 4.95)],
        };
        var frame = SceneRenderer.Render(scene, ctx);
        Assert.True(Max(frame[LampOf(KeyA)]) > 150 && Max(frame[LampOf(KeyIds.Right)]) > 150);
        Assert.True(Board.Count(l => l.IsKey && Max(frame[l.LampId]) > 80) > 6, "an arc crosses the keys in between");
    }

    [Fact]
    public void Demo_feed_types_plays_music_and_moves_the_mouse()
    {
        var ctx = SceneDemo.Context(12.3, Board);
        Assert.NotEmpty(ctx.KeyPresses);
        Assert.NotEmpty(ctx.BeatTimes);
        Assert.NotNull(ctx.MouseX);
        Assert.Equal(ctx.ScreenGridWidth * ctx.ScreenGridHeight, ctx.ScreenGrid.Count);
        Assert.All(ctx.KeyPresses, p => Assert.True(p.At <= 12.3));
    }

    static SceneContext Typing(double now, params (int Key, double At)[] presses) =>
        new() { Seconds = now, Lamps = Board, KeyPresses = [.. presses.Select(p => new KeyPress(p.Key, p.At))] };

    [Fact]
    public void Soft_press_lights_the_pressed_key_and_only_hints_at_its_neighbours()
    {
        var scene = Scene(Layer(SceneEffect.SoftPress, "FFFFFF"));
        var frame = SceneRenderer.Render(scene, Typing(10, (KeyS, 9.95)));
        Assert.True(Max(frame[LampOf(KeyS)]) > 230);
        Assert.InRange(Max(frame[LampOf(KeyA)]), 1, 90);                       // a faint glow next door
        Assert.True(Max(frame[LampOf(KeyIds.Esc)]) == 0);
        Assert.All(SceneRenderer.Render(scene, Typing(15, (KeyS, 9.95))).Values, c => Assert.Equal(0, Max(c))); // eased back
    }

    [Fact]
    public void Wake_on_type_rests_dim_and_brightens_while_typing()
    {
        var scene = Scene(Layer(SceneEffect.WakeOnType, "FFFFFF"));
        int idle = Max(SceneRenderer.Render(scene, Typing(10))[LampOf(KeyW)]);
        int typing = Max(SceneRenderer.Render(scene, Typing(10, (KeyW, 9.9), (KeyA, 9.95)))[LampOf(KeyW)]);
        Assert.InRange(idle, 20, 45);
        Assert.True(typing > 200, $"{typing}");
    }

    [Theory]
    [InlineData(SceneEffect.WakeOnType)] [InlineData(SceneEffect.SoftPress)] [InlineData(SceneEffect.TypingMood)]
    public void Calm_typing_effects_have_eased_back_before_the_press_history_ends(SceneEffect effect)
    {
        // The engine keeps 4 s of key presses: at the slowest speed the fade must be over by then, or it would snap.
        var layer = SceneEffects.CreateLayer(effect);
        layer.Speed = 1;
        var scene = Scene(layer);
        var rest = SceneRenderer.Render(scene, Typing(20))[LampOf(KeyW)];
        var justBeforeDrop = SceneRenderer.Render(scene, Typing(20, (KeyW, 20 - 3.95)))[LampOf(KeyW)];
        Assert.True(Math.Abs(Max(justBeforeDrop) - Max(rest)) <= 3, $"{effect}: {justBeforeDrop} vs resting {rest}");
    }

    [Fact]
    public void Typing_mood_moves_from_the_calm_colour_to_the_active_one()
    {
        var scene = Scene(new LightLayer { Effect = SceneEffect.TypingMood, ColorMode = SceneColorMode.Dual, Colors = ["0000FF", "FF0000"] });
        var calm = SceneRenderer.Render(scene, Typing(10))[LampOf(KeyW)];
        var busy = SceneRenderer.Render(scene, Typing(10, [.. Enumerable.Range(0, 20).Select(i => (KeyW, 10 - i * 0.12))]))[LampOf(KeyW)];
        Assert.True(calm.B > 200 && calm.R < 30);
        Assert.True(busy.R > 200 && busy.B < 60);
    }

    [Fact]
    public void Presets_are_independent_copies()
    {
        var a = ScenePresets.All[0];
        a.Layers[0].Colors[0] = "123456";
        Assert.NotEqual("123456", ScenePresets.All[0].Layers[0].Colors[0]);
    }

    // ---------------------------------------------------------------- model

    static LightingScene Sample() => new()
    {
        Name = "Test", Description = "d", Background = "010203",
        Layers =
        [
            new LightLayer
            {
                Name = "Top", Enabled = false, Effect = SceneEffect.Tornado, ColorMode = SceneColorMode.Gradient,
                Colors = ["FF0000", "00FF00", "0000FF"], Direction = SceneDirection.CounterClockwise, Speed = 7, Brightness = 42,
                AllKeys = false, Keys = [17, 30], AllEdges = false, EdgeLamps = [150, 151],
            },
            new LightLayer(),
        ],
    };

    [Fact]
    public void Scene_round_trips_through_json_with_enum_names()
    {
        var scene = Sample();
        string json = JsonSerializer.Serialize(scene);
        Assert.Contains("\"Tornado\"", json);
        Assert.Contains("\"CounterClockwise\"", json);
        Assert.Contains("\"Gradient\"", json);

        var back = JsonSerializer.Deserialize<LightingScene>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(back));
        Assert.Equal(["FF0000", "00FF00", "0000FF"], back.Layers[0].Colors);
        Assert.Equal(["FF2800"], back.Layers[1].Colors); // defaults are replaced, not appended to
    }

    [Fact]
    public void Clone_is_deep()
    {
        var scene = Sample();
        var copy = scene.Clone();
        copy.Layers[0].Colors.Add("FFFFFF");
        copy.Layers[0].Keys.Add(99);
        copy.Layers[0].EdgeLamps.Clear();
        copy.Layers.RemoveAt(1);
        copy.Name = "Other";

        Assert.Equal(2, scene.Layers.Count);
        Assert.Equal(3, scene.Layers[0].Colors.Count);
        Assert.Equal([17, 30], scene.Layers[0].Keys);
        Assert.Equal([150, 151], scene.Layers[0].EdgeLamps);
        Assert.Equal("Test", scene.Name);
        Assert.Equal(JsonSerializer.Serialize(Sample()), JsonSerializer.Serialize(scene));
    }

    // ---------------------------------------------------------------- performance

    [Fact]
    public void Rendering_is_fast_enough_for_60_fps()
    {
        var scene = Scene(SceneEffects.CreateLayer(SceneEffect.Reactive), SceneEffects.CreateLayer(SceneEffect.Matrix),
            SceneEffects.CreateLayer(SceneEffect.Fire));
        var ctx = FullCtx(1);
        SceneRenderer.Render(scene, ctx); // warm up

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            SceneRenderer.Render(scene, new SceneContext { Seconds = i / 60.0, Lamps = Board, KeyPressTimes = ctx.KeyPressTimes });
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} ms");
    }
}
