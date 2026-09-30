using static Darkmount.Keyboard.Lamps.SceneMath;

namespace Darkmount.Keyboard.Lamps;

/// <summary>A lamp as the effects see it: position stretched to 0..1 (keys and edge LEDs separately), key id or 0.</summary>
internal readonly record struct Pixel(int LampId, double X, double Y, bool IsKey, int KeyId);

/// <summary>Per-frame data shared by all layers.</summary>
internal sealed class SceneFrame(SceneContext ctx, Pixel[] pixels, Rgbf background)
{
    Dictionary<int, Pixel>? _byKey;

    public SceneContext Ctx { get; } = ctx;
    public Pixel[] Pixels { get; } = pixels;
    public Rgbf Background { get; } = background;

    public bool TryGetKey(int keyId, out Pixel pixel)
    {
        if (_byKey is null)
        {
            _byKey = [];
            foreach (var p in Pixels)
                if (p.IsKey && p.KeyId != 0) _byKey.TryAdd(p.KeyId, p);
        }
        return _byKey.TryGetValue(keyId, out pixel);
    }
}

/// <summary>Per-layer data: resolved palette, speed and direction.</summary>
internal sealed class LayerFrame
{
    public LayerFrame(SceneFrame scene, LightLayer layer)
    {
        Scene = scene;
        Layer = layer;
        Info = SceneEffects.Get(layer.Effect);
        Palette = Palette.For(layer, Info.DefaultColors);
        SpeedFactor = SceneShaders.SpeedFactor(layer.Speed);
        Direction = Info.Directions.Count == 0 || Info.Directions.Contains(layer.Direction) ? layer.Direction : Info.DefaultDirection;
    }

    public SceneFrame Scene { get; }
    public LightLayer Layer { get; }
    public SceneEffectInfo Info { get; }
    public Palette Palette { get; }
    public SceneDirection Direction { get; }

    /// <summary>0.3 (speed 1) … 1 (speed 5) … 2 (speed 10).</summary>
    public double SpeedFactor { get; }

    public SceneContext Ctx => Scene.Ctx;
    public double Seconds => Scene.Ctx.Seconds;

    /// <summary>Effect time: the clock scaled by the layer's speed.</summary>
    public double T => Scene.Ctx.Seconds * SpeedFactor;

    public SceneColorMode Mode => Palette.Count == 1 ? SceneColorMode.Single : Layer.ColorMode;
}

internal delegate Rgba Shader(in Pixel p);

/// <summary>
/// The effect maths. Each effect turns a <see cref="LayerFrame"/> into a per-lamp <see cref="Shader"/>; per-frame work
/// (ripple centres, lava blobs, sensor lookups) happens once in the factory, not per lamp.
/// </summary>
internal static partial class SceneShaders
{
    /// <summary>Width / height of the keyboard: distances and angles use X × Aspect so circles look round.</summary>
    public const double Aspect = 3.6;

    static readonly double MaxRadius = Math.Sqrt(Aspect * Aspect / 4 + 0.25);
    const double Tau = 2 * Math.PI;

    public static double SpeedFactor(int speed)
    {
        int s = Math.Clamp(speed, 1, 10);
        return s <= 5 ? 0.3 + 0.175 * (s - 1) : 1 + 0.2 * (s - 5);
    }

    public static Shader Create(LayerFrame f) => f.Info.Effect switch // Info maps unknown values to Static
    {
        SceneEffect.Static => Static(f),
        SceneEffect.ColorWave => ColorWave(f),
        SceneEffect.Tornado => Tornado(f),
        SceneEffect.Breathing => Breathing(f),
        SceneEffect.Matrix => Streams(f, MatrixStyle),
        SceneEffect.Reactive => Reactive(f),
        SceneEffect.Ripple => Ripple(f),
        SceneEffect.Rainbow => Rainbow(f),
        SceneEffect.Plasma => Plasma(f),
        SceneEffect.Aurora => Aurora(f),
        SceneEffect.Fire => Fire(f),
        SceneEffect.Ocean => Ocean(f),
        SceneEffect.Twinkle => Twinkle(f),
        SceneEffect.Rain => Streams(f, RainStyle),
        SceneEffect.Heartbeat => Heartbeat(f),
        SceneEffect.Police => Police(f),
        SceneEffect.Scanner => Scanner(f),
        SceneEffect.ColorCycle => ColorCycle(f),
        SceneEffect.CpuTemperature => CpuTemperature(f),
        SceneEffect.PerformanceMeter => PerformanceMeter(f),
        SceneEffect.TypingHeatmap => TypingHeatmap(f),
        SceneEffect.AudioPulse => AudioPulse(f),
        SceneEffect.AudioSpectrum => AudioSpectrum(f),
        SceneEffect.Lava => Lava(f),
        SceneEffect.Candy => Candy(f),
        SceneEffect.ScreenSync => ScreenSync(f),
        SceneEffect.PerKey => PerKey(f),
        SceneEffect.Starfield => Starfield(f),
        SceneEffect.Fireworks => Fireworks(f),
        SceneEffect.Comet => Comet(f),
        SceneEffect.Lightning => Lightning(f),
        SceneEffect.Glitch => Glitch(f),
        SceneEffect.Radar => Radar(f),
        SceneEffect.Disco => Disco(f),
        SceneEffect.Snow => Particles(f, SnowStyle),
        SceneEffect.Bubbles => Particles(f, BubbleStyle),
        SceneEffect.Embers => Particles(f, EmberStyle),
        SceneEffect.Fireflies => Particles(f, FireflyStyle),
        SceneEffect.KeyLightning => KeyLightning(f),
        SceneEffect.LaserTyping => LaserTyping(f),
        SceneEffect.RainbowTyping => RainbowTyping(f),
        SceneEffect.ComboMeter => ComboMeter(f),
        SceneEffect.KeySparks => KeySparks(f),
        SceneEffect.BeatRings => BeatRings(f),
        SceneEffect.Waveform => Waveform(f),
        SceneEffect.ClubLights => ClubLights(f),
        SceneEffect.ScreenMood => ScreenMood(f),
        SceneEffect.ScreenFlash => ScreenFlashes(f),
        SceneEffect.MouseSpotlight => MouseSpotlight(f),
        SceneEffect.SoftPress => SoftPress(f),
        SceneEffect.WakeOnType => WakeOnType(f),
        SceneEffect.TypingMood => TypingMood(f),
        SceneEffect.PaintSplash => PaintSplash(f),
        SceneEffect.KeyDrops => KeyDrops(f),
        SceneEffect.WordWaves => WordWaves(f),
        SceneEffect.TypingSnake => TypingSnake(f),
        SceneEffect.VuMeter => VuMeter(f),
        SceneEffect.DayNight => DayNight(f),
        _ => (in Pixel _) => Rgba.Clear,
    };

    // ------------------------------------------------------------------ geometry

    /// <summary>0..1 coordinate that increases in the direction of motion (angle for turning, radius for radial directions).</summary>
    static double Along(SceneDirection d, double x, double y) => d switch
    {
        SceneDirection.Left => 1 - x,
        SceneDirection.Up => 1 - y,
        SceneDirection.Down => y,
        SceneDirection.Clockwise => Angle(x, y),
        SceneDirection.CounterClockwise => 1 - Angle(x, y),
        SceneDirection.Outward => Radius(x, y) / MaxRadius,
        SceneDirection.Inward => 1 - Radius(x, y) / MaxRadius,
        _ => x,
    };

    /// <summary>Angle around the centre, 0..1, increasing clockwise on screen (Y points down).</summary>
    static double Angle(double x, double y) => Frac(Math.Atan2(y - 0.5, (x - 0.5) * Aspect) / Tau);

    /// <summary>Distance from the centre in keyboard heights.</summary>
    static double Radius(double x, double y)
    {
        double dx = (x - 0.5) * Aspect, dy = y - 0.5;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = (x1 - x2) * Aspect, dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static bool IsTurning(SceneDirection d) => d is SceneDirection.Clockwise or SceneDirection.CounterClockwise;
    static bool IsHorizontal(SceneDirection d) => d is SceneDirection.Left or SceneDirection.Right;

    /// <summary>
    /// Travel coordinate in keyboard heights (so horizontal and vertical motion look alike) and the across coordinate, for
    /// the four linear directions.
    /// </summary>
    static (double Along, double Across) Axes(SceneDirection d, double x, double y) => d switch
    {
        SceneDirection.Left => ((1 - x) * Aspect, y),
        SceneDirection.Up => (1 - y, x * Aspect),
        SceneDirection.Down => (y, x * Aspect),
        _ => (x * Aspect, y),
    };

    // ------------------------------------------------------------------ colour helpers

    /// <summary>Colour for a 0..1 field value: cyclic through several colours, dark → colour → pale for one.</summary>
    static Func<double, Rgbf> FieldColors(Palette palette)
    {
        if (palette.Count == 1)
        {
            var ramp = palette.AsRamp();
            return u => ramp.Sample(u);
        }
        return palette.SampleCyclic;
    }

    static Rgba Opaque(Rgbf c) => new(c, 1);

    /// <summary>Painted keys and edge LEDs in their own colour; everything else transparent.</summary>
    static Shader PerKey(LayerFrame f)
    {
        static Dictionary<int, Rgbf> Parse(Dictionary<int, string>? colors)
        {
            var parsed = new Dictionary<int, Rgbf>();
            foreach (var (id, hex) in colors ?? [])
                if (Rgbf.TryParse(hex, out var c)) parsed[id] = c;
            return parsed;
        }
        var keys = Parse(f.Layer.KeyColors);
        var edges = Parse(f.Layer.EdgeColors);
        if (keys.Count == 0 && edges.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) => (p.IsKey ? keys.TryGetValue(p.KeyId, out var c) : edges.TryGetValue(p.LampId, out c)) ? Opaque(c) : Rgba.Clear;
    }

    // ------------------------------------------------------------------ classic effects

    static Shader Static(LayerFrame f)
    {
        var pal = f.Palette;
        if (pal.Count == 1) { var c = Opaque(pal.First); return (in Pixel _) => c; }
        var dir = f.Direction;
        bool cyclic = IsTurning(dir);
        return (in Pixel p) =>
        {
            double u = Along(dir, p.X, p.Y);
            return Opaque(cyclic ? pal.SampleCyclic(u) : pal.Sample(u));
        };
    }

    static Shader ColorWave(LayerFrame f)
    {
        var pal = f.Palette;
        var dir = f.Direction;
        double shift = f.T * 0.25;                                     // wave cycles per second at speed 5
        double cycles = dir is SceneDirection.Outward or SceneDirection.Inward ? 1.3 : 1.0;
        if (f.Mode == SceneColorMode.Single)
        {
            var c = pal.First;
            return (in Pixel p) =>
            {
                double band = 0.5 + 0.5 * Math.Cos(Tau * (Along(dir, p.X, p.Y) * cycles * 1.5 - shift));
                return Opaque(c * (0.06 + 0.94 * Math.Pow(band, 2.2)));
            };
        }
        return (in Pixel p) => Opaque(pal.SampleCyclic(Along(dir, p.X, p.Y) * cycles - shift));
    }

    static Shader Tornado(LayerFrame f)
    {
        var pal = f.Palette;
        double spin = f.T * 0.22 * (f.Direction == SceneDirection.CounterClockwise ? -1 : 1);
        bool single = f.Mode == SceneColorMode.Single;
        return (in Pixel p) =>
        {
            double r = Radius(p.X, p.Y);
            double phase = Angle(p.X, p.Y) + r * 0.3 - spin;
            if (!single) return Opaque(pal.SampleCyclic(phase));
            double arm = 0.5 + 0.5 * Math.Cos(Tau * phase * 2);          // two arms
            double eye = SmoothStep(0.35, 0, r);                         // bright core
            return Opaque(pal.First * Math.Max(0.05 + 0.95 * Math.Pow(arm, 1.8), eye));
        };
    }

    static Shader Breathing(LayerFrame f)
    {
        double phase = f.T * 0.3;
        // exp(sin) breathing curve: 0 at whole cycles (the colour changes while dark), 1 half-way.
        double level = (Math.Exp(Math.Sin(Tau * phase - Math.PI / 2)) - 1 / Math.E) / (Math.E - 1 / Math.E);
        var c = Opaque(f.Palette[(int)Math.Floor(phase)] * (0.02 + 0.98 * level));
        return (in Pixel _) => c;
    }

    static Shader Rainbow(LayerFrame f)
    {
        var dir = f.Direction;
        double shift = f.T * 0.25;
        double scale = dir is SceneDirection.Outward or SceneDirection.Inward ? 1.3 : 1.0;
        return (in Pixel p) => Opaque(Rgbf.Hsv(Along(dir, p.X, p.Y) * scale - shift));
    }

    static Shader ColorCycle(LayerFrame f)
    {
        var pal = f.Palette;
        var c = Opaque(pal.Count == 1 ? Rgbf.Hsv(f.T * 0.05) : pal.SampleCyclic(f.T * 0.25 / pal.Count));
        return (in Pixel _) => c;
    }

    // ------------------------------------------------------------------ streams: Matrix and Rain

    sealed record StreamStyle(double MinSpeed, double MaxSpeed, double Trail, double Head, int Drops, double Density,
        double MinGap, double GapRange, int Salt, bool Glyphs);

    static readonly StreamStyle MatrixStyle = new(0.45, 1.05, 0.62, 0.14, 2, 0.8, 0.2, 1.0, 101, true);
    static readonly StreamStyle RainStyle = new(1.3, 2.1, 0.26, 0.1, 2, 0.6, 0.4, 1.4, 202, false);

    /// <summary>
    /// Drops running along key columns (rows for Left/Right): a bright head and a fading trail, transparent elsewhere. Each
    /// column has its own speed and phase from a hash, so the pattern is random-looking but reproducible.
    /// </summary>
    static Shader Streams(LayerFrame f, StreamStyle s)
    {
        var dir = f.Direction;
        bool horizontal = IsHorizontal(dir);
        double colWidth = horizontal ? 0.19 : 0.045;
        double speedScale = horizontal ? 0.55 : 1;
        double trail = s.Trail * (horizontal ? 0.45 : 1), head = s.Head * (horizontal ? 0.35 : 1);
        double t = f.T;
        int glyphTick = (int)Math.Floor(f.Seconds * 9);
        var pal = f.Palette;
        var mode = f.Mode;
        Rgbf headSingle = Rgbf.Lerp(pal.First, Rgbf.White, 0.6);

        return (in Pixel p) =>
        {
            double along = dir switch
            {
                SceneDirection.Up => 1 - p.Y, SceneDirection.Left => 1 - p.X, SceneDirection.Right => p.X, _ => p.Y,
            };
            int col = (int)Math.Floor((horizontal ? p.Y : p.X) / colWidth);
            double best = 0, bestDelta = 0;
            for (int d = 0; d < s.Drops; d++)
            {
                double speed = (s.MinSpeed + (s.MaxSpeed - s.MinSpeed) * Noise.Hash(col, d, s.Salt)) * speedScale;
                double span = 1 + trail + s.MinGap + s.GapRange * Noise.Hash(col, d, s.Salt + 1);
                double pos = t * speed + Noise.Hash(col, d, s.Salt + 2) * span;
                double cycle = Math.Floor(pos / span);
                if (Noise.Hash(col, d * 7919 + (int)cycle, s.Salt + 3) > s.Density) continue;
                double delta = pos - cycle * span - along;                   // how far the head is past this lamp
                if (delta < 0 || delta > trail) continue;
                double i = delta <= head ? 1 : Math.Pow(1 - (delta - head) / (trail - head), 1.6);
                if (i > best) { best = i; bestDelta = delta; }
            }
            if (best <= 0) return Rgba.Clear;
            if (s.Glyphs && bestDelta > head) best *= 0.6 + 0.4 * Noise.Hash(p.LampId, glyphTick, 77); // changing code
            var c = mode switch
            {
                SceneColorMode.Single => bestDelta <= head ? headSingle : pal.First,
                SceneColorMode.Dual => Rgbf.Lerp(pal[0], pal[1], SmoothStep(head * 0.5, head * 2.5, bestDelta)),
                _ => pal.Sample(bestDelta / trail),
            };
            return new Rgba(c, best);
        };
    }

    // ------------------------------------------------------------------ key presses

    /// <summary>Fade time: 1.5 s at speed 1 … 0.6 s at speed 10.</summary>
    static double ReactiveDuration(int speed) => 1.5 - (Math.Clamp(speed, 1, 10) - 1) * 0.1;

    static Shader Reactive(LayerFrame f)
    {
        var presses = f.Ctx.KeyPressTimes;
        if (presses.Count == 0) return (in Pixel _) => Rgba.Clear;
        double now = f.Seconds, duration = ReactiveDuration(f.Layer.Speed);
        var pal = f.Palette;
        var mode = f.Mode;
        return (in Pixel p) =>
        {
            if (p.KeyId == 0 || !presses.TryGetValue(p.KeyId, out double at)) return Rgba.Clear;
            double age = now - at;
            if (age < 0 || age >= duration) return Rgba.Clear;
            double q = age / duration;
            return mode switch
            {
                SceneColorMode.Single => new(pal.First, Math.Pow(1 - q, 1.6)),
                SceneColorMode.Dual => new(Rgbf.Lerp(pal[0], pal[1], SmoothStep(0, 0.5, q)), 1 - SmoothStep(0.4, 1, q)),
                _ => new(pal.Sample(q / 0.75), 1 - SmoothStep(0.5, 1, q)),
            };
        };
    }

    static Shader Ripple(LayerFrame f)
    {
        double speed = 1.35 * f.SpeedFactor;              // keyboard heights per second
        double life = 1.8 / f.SpeedFactor, width = 0.16;
        var rings = new List<(double X, double Y, double Radius, double Fade, double Age)>();
        foreach (var (keyId, at) in f.Ctx.KeyPressTimes)
        {
            double age = f.Seconds - at;
            if (age < 0 || age >= life || !f.Scene.TryGetKey(keyId, out var key)) continue;
            rings.Add((key.X, key.Y, age * speed, Math.Pow(1 - age / life, 1.5), age / life));
        }
        if (rings.Count == 0) return (in Pixel _) => Rgba.Clear;
        var pal = f.Palette;
        var mode = f.Mode;
        return (in Pixel p) =>
        {
            double best = 0, bestAge = 0;
            foreach (var r in rings)
            {
                double off = (Distance(p.X, p.Y, r.X, r.Y) - r.Radius) / width;
                if (off is > 4 or < -4) continue;
                double a = Math.Exp(-off * off) * r.Fade;
                if (a > best) { best = a; bestAge = r.Age; }
            }
            if (best < 0.002) return Rgba.Clear;
            var c = mode switch
            {
                SceneColorMode.Single => pal.First,
                SceneColorMode.Dual => Rgbf.Lerp(pal[0], pal[1], SmoothStep(0, 0.7, bestAge)),
                _ => pal.Sample(bestAge),
            };
            return new Rgba(c, best);
        };
    }

    // ------------------------------------------------------------------ ambient fields

    static Shader Plasma(LayerFrame f)
    {
        var color = FieldColors(f.Palette);
        double t = f.T;
        double cx = Aspect / 2 + Math.Sin(t * 0.3) * 1.2, cy = 0.5 + Math.Cos(t * 0.4) * 0.4, drift = t * 0.03;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, y = p.Y;
            double dx = x - cx, dy = y - cy;
            double v = Math.Sin(x * 1.6 + t * 0.9)
                       + Math.Sin(y * 4.2 - t * 0.7 + x * 0.3)
                       + Math.Sin((x * 0.9 + y * 2.5) * 1.3 + t * 0.55)
                       + Math.Sin(Math.Sqrt(dx * dx + dy * dy) * 3.2 - t * 1.1);
            return Opaque(color(v / 8 + 0.5 + drift));
        };
    }

    static Shader Aurora(LayerFrame f)
    {
        var ramp = f.Palette.AsRamp();
        double s = f.T * 0.12;
        double drift = f.Direction == SceneDirection.Left ? -1 : 1;
        var sky = new Rgbf(0, 0.012, 0.04);
        return (in Pixel p) =>
        {
            double x = p.X * Aspect - drift * s * 2, y = p.Y;
            double curtain = SmoothStep(0.3, 0.62, Noise.Fbm(x * 0.9, 3.7 + s * 0.8));
            double centre = 0.3 + 0.2 * Math.Sin(x * 0.7 + s * 2.5);
            double dy = y - centre;
            double profile = Math.Exp(-dy * dy / (dy > 0 ? 0.3 : 0.09));      // long tail downwards
            double rays = 0.6 + 0.4 * Math.Sin(x * 7.3 + 4 * Noise.Value(x * 0.8 + s * 4, 1.3));
            double intensity = Clamp01((curtain * rays * 1.5 + 0.15) * profile);
            double hue = SmoothStep(0.25, 0.75, Noise.Fbm(x * 0.35 + 9.1, s * 0.5));
            var c = ramp.Sample(Clamp01(hue * 0.45 + (1 - y) * 0.45 * (1 - profile))); // green in the curtains, violet above
            return Opaque(sky + c * intensity);
        };
    }

    static Shader Fire(LayerFrame f)
    {
        var ramp = f.Palette.AsRamp();
        bool down = f.Direction == SceneDirection.Down;
        double t = f.T;
        return (in Pixel p) =>
        {
            double v = down ? p.Y : 1 - p.Y;                                  // 0 at the flames' base
            double x = p.X * Aspect;
            double n = Noise.Fbm(x * 1.3, v * 2.4 - t * 1.7);                  // pattern rises with time
            double flicker = Noise.Value(x * 2.1 + 11, t * 6);
            double heat = Clamp01(0.92 - v * 1.2 + (n - 0.5) * 1.2 + (flicker - 0.5) * 0.3);
            return Opaque(heat < 0.3 ? ramp.First * SmoothStep(0.02, 0.3, heat) : ramp.Sample((heat - 0.3) / 0.7));
        };
    }

    static Shader Ocean(LayerFrame f)
    {
        var ramp = f.Palette.AsRamp();
        var dir = f.Direction;
        double t = f.T;
        return (in Pixel p) =>
        {
            var (u, a) = Axes(dir, p.X, p.Y);
            double swell = Math.Sin(Tau * (u * 0.35 - t * 0.18) + 1.3 * Math.Sin(a * 2.2 + t * 0.3));
            double chop = Noise.Fbm(u * 1.4 - t * 0.45, a * 1.8 + t * 0.12);
            double v = 0.42 + 0.26 * swell + 0.45 * (chop - 0.5);
            return Opaque(ramp.Sample(Clamp01(v)));
        };
    }

    static Shader Lava(LayerFrame f)
    {
        var ramp = f.Palette.AsRamp();
        double t = f.T;
        const int n = 6;
        var blobs = new (double X, double Y, double R2)[n];
        for (int i = 0; i < n; i++)
        {
            double x = Aspect * (0.5 + 0.44 * Math.Sin(t * 0.13 * (1 + Noise.Hash(i, 1)) + Tau * Noise.Hash(i, 2)));
            double y = 0.5 + 0.6 * Math.Sin(t * 0.11 * (1 + Noise.Hash(i, 3)) + Tau * Noise.Hash(i, 4));
            double r = 0.22 + 0.12 * Noise.Hash(i, 5);
            blobs[i] = (x, y, r * r);
        }
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, y = p.Y, field = 0;
            foreach (var b in blobs)
            {
                double dx = x - b.X, dy = y - b.Y;
                field += b.R2 / (dx * dx + dy * dy + 0.02);
            }
            double v = SmoothStep(0.6, 2.2, field) + (Noise.Value(x * 1.5, y * 1.5 + t * 0.1) - 0.5) * 0.14;
            return Opaque(ramp.Sample(Clamp01(0.08 + 0.92 * v)));
        };
    }

    static Shader Candy(LayerFrame f)
    {
        var color = FieldColors(f.Palette);
        var dir = f.Direction;
        double t = f.T;
        return (in Pixel p) =>
        {
            var (u, a) = Axes(dir, p.X, p.Y);
            double phase = u * 0.25 + 0.12 * Math.Sin(Tau * (a * 0.8 + t * 0.1))
                           + 0.25 * (Noise.Fbm(u * 0.6 - t * 0.1, a * 1.5) - 0.5) - t * 0.08;
            return Opaque(color(phase));
        };
    }

    static Shader Twinkle(LayerFrame f)
    {
        var pal = f.Palette;
        var mode = f.Mode;
        double t = f.T;
        return (in Pixel p) =>
        {
            var baseColor = mode switch
            {
                SceneColorMode.Single => pal.First * 0.1,
                SceneColorMode.Dual => pal[1],
                _ => pal.Sample(p.X) * 0.1,
            };
            double period = 1.4 + Noise.Hash(p.LampId, 11) * 2.2;
            double pos = (t + Noise.Hash(p.LampId, 12) * period) / period;
            int cycle = (int)Math.Floor(pos);
            if (Noise.Hash(p.LampId, cycle, 13) >= 0.42) return Opaque(baseColor);
            double q = pos - cycle;
            double env = q < 0.12 ? SmoothStep(0, 0.12, q) : Math.Pow(1 - (q - 0.12) / 0.88, 2.2);
            var sparkle = mode == SceneColorMode.Gradient ? pal[(int)(Noise.Hash(p.LampId, cycle, 14) * pal.Count)] : pal.First;
            return Opaque(Rgbf.Lerp(baseColor, sparkle, env));
        };
    }

    static Shader Heartbeat(LayerFrame f)
    {
        var pal = f.Palette;
        var mode = f.Mode;
        double period = 60 / (40 + 32 * f.SpeedFactor);                    // 72 bpm at speed 5
        double now = f.Seconds;
        return (in Pixel p) =>
        {
            double phase = Frac((now - Radius(p.X, p.Y) * 0.07) / period);  // spreads out from the centre
            double lub = WrapDelta(phase, 0) / 0.045, dub = WrapDelta(phase, 0.2) / 0.05;
            double beat = Clamp01(Math.Exp(-0.5 * lub * lub) + 0.65 * Math.Exp(-0.5 * dub * dub));
            return Opaque(mode switch
            {
                SceneColorMode.Single => pal.First * (0.05 + 0.95 * beat),
                SceneColorMode.Dual => Rgbf.Lerp(pal[1], pal[0], beat),
                _ => pal.Sample(beat),
            });
        };
    }

    static Shader Police(LayerFrame f)
    {
        double phase = Frac(f.T);                                           // one left + right cycle per second at speed 5
        bool leftTurn = phase < 0.5;
        int strobe = (int)Math.Floor((phase - (leftTurn ? 0 : 0.5)) * 12);  // 6 slots per half: on, off, on, off, on, off
        bool on = strobe % 2 == 0;
        var left = Opaque(leftTurn && on ? f.Palette[0] : Rgbf.Black);
        var right = Opaque(!leftTurn && on ? f.Palette[1] : Rgbf.Black);
        return (in Pixel p) => p.X < 0.5 ? left : right;
    }

    static Shader Scanner(LayerFrame f)
    {
        var dir = f.Direction;
        double period = 2.4 / f.SpeedFactor;                                // there and back
        double decay = 0.3 / Math.Sqrt(f.SpeedFactor);                      // trail, seconds
        const double overshoot = 0.06;
        double tau = Frac(f.Seconds / period);
        double head = -overshoot + (tau < 0.5 ? tau * 2 : 2 - tau * 2) * (1 + 2 * overshoot);
        var pal = f.Palette;
        var mode = f.Mode;
        return (in Pixel p) =>
        {
            double a = dir switch
            {
                SceneDirection.Left => 1 - p.X, SceneDirection.Up => 1 - p.Y, SceneDirection.Down => p.Y, _ => p.X,
            };
            // The head passes this lamp at tau1 on the way out and tau2 on the way back; take the latest pass.
            double h = (a + overshoot) / (1 + 2 * overshoot);
            double sinceOut = Frac(tau - h / 2), sinceBack = Frac(tau - (1 - h / 2));
            double trail = Math.Exp(-Math.Min(sinceOut, sinceBack) * period / decay);
            double d = (a - head) / 0.03;
            double intensity = Math.Max(trail, Math.Exp(-d * d));
            if (intensity < 0.002) return Rgba.Clear;
            var c = mode switch
            {
                SceneColorMode.Single => pal.First,
                SceneColorMode.Dual => Rgbf.Lerp(pal[0], pal[1], 1 - intensity),
                _ => pal.Sample(1 - intensity),
            };
            return new Rgba(c, intensity);
        };
    }

    // ------------------------------------------------------------------ sensors

    static readonly Palette TemperaturePalette = new([new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)]);
    static readonly Rgbf UnknownTemperature = new(0, 80 / 255.0, 1);

    static Rgbf Temperature(double? celsius, Palette palette) =>
        celsius is { } c ? palette.Sample(Clamp01((c - 50) / 35)) : UnknownTemperature;

    static Shader CpuTemperature(LayerFrame f)
    {
        var pal = f.Palette.Count > 1 ? f.Palette : TemperaturePalette;
        double? temp = f.Ctx.CpuTemp;
        double pulse = temp >= 85 ? 0.65 + 0.35 * Math.Cos(Tau * f.Seconds * 1.2) : 1; // alarm pulse when hot
        var c = Opaque(Temperature(temp, pal) * pulse);
        return (in Pixel _) => c;
    }

    /// <summary>F1–F12 (CPU bar) and the number row 1–0 (GPU bar), from the key labels.</summary>
    static readonly Dictionary<int, int> FunctionRow = KeyIds.All
        .Where(k => k.Zone == KeyZone.Keyboard && k.Label.Length >= 2 && k.Label[0] == 'F' && int.TryParse(k.Label[1..], out int n) && n is >= 1 and <= 12)
        .ToDictionary(k => (int)k.Id, k => int.Parse(k.Label[1..]) - 1);

    static readonly Dictionary<int, int> NumberRow = KeyIds.All
        .Where(k => k.Zone == KeyZone.Keyboard && k.Label.Length == 1 && char.IsAsciiDigit(k.Label[0]))
        .ToDictionary(k => (int)k.Id, k => k.Label[0] == '0' ? 9 : k.Label[0] - '1');

    static Shader PerformanceMeter(LayerFrame f)
    {
        var bar = f.Palette.Count > 1 ? f.Palette : TemperaturePalette;
        var rest = Opaque(f.Palette.First * 0.15);
        var edges = Opaque(Temperature(f.Ctx.CpuTemp, TemperaturePalette));
        double cpu = Clamp01((f.Ctx.CpuLoad ?? 0) / 100) * FunctionRow.Count;
        double gpu = Clamp01((f.Ctx.GpuLoad ?? 0) / 100) * NumberRow.Count;

        static Rgba Segment(Palette bar, int index, int count, double fill) =>
            Opaque(bar.Sample(index / (double)(count - 1)) * (0.06 + 0.94 * Clamp01(fill - index)));

        return (in Pixel p) =>
        {
            if (!p.IsKey) return edges;
            if (FunctionRow.TryGetValue(p.KeyId, out int i)) return Segment(bar, i, FunctionRow.Count, cpu);
            if (NumberRow.TryGetValue(p.KeyId, out i)) return Segment(bar, i, NumberRow.Count, gpu);
            return rest;
        };
    }

    static Shader TypingHeatmap(LayerFrame f)
    {
        var pal = f.Palette.Count > 1 ? f.Palette : f.Palette.AsRamp();
        var heat = f.Ctx.KeyHeat;
        double average = heat.Count == 0 ? 0 : heat.Values.Average(v => Clamp01(v));
        Rgbf Color(double h) => pal.Sample(h) * (0.3 + 0.7 * SmoothStep(0, 0.35, h));
        var edges = Opaque(Color(average) * 0.5);
        return (in Pixel p) =>
        {
            if (!p.IsKey) return edges;
            double h = p.KeyId != 0 && heat.TryGetValue(p.KeyId, out double v) ? Clamp01(v) : 0;
            return Opaque(Color(h));
        };
    }

    // ------------------------------------------------------------------ audio

    static Shader AudioPulse(LayerFrame f)
    {
        var pal = f.Palette;
        bool single = pal.Count == 1;
        double level = Clamp01(f.Ctx.AudioLevel);
        double gain = 0.04 + 0.96 * level, radius = level * 1.05;
        return (in Pixel p) =>
        {
            double d = Radius(p.X, p.Y) / MaxRadius;
            double inside = SmoothStep(radius + 0.15, radius - 0.15, d);
            var c = single ? pal.First : pal.Sample(d);
            return Opaque(c * (gain * (0.3 + 0.7 * inside)));
        };
    }

    static Shader AudioSpectrum(LayerFrame f)
    {
        var bands = f.Ctx.AudioBands.Count > 0 ? f.Ctx.AudioBands : DerivedBands(f.Ctx.AudioLevel, f.Seconds);
        int n = bands.Count;
        var pal = f.Palette;
        return (in Pixel p) =>
        {
            double level = Clamp01(bands[Math.Clamp((int)Math.Floor(p.X * n), 0, n - 1)]);
            double v = 1 - p.Y;                                            // 0 = bottom row
            double threshold = 0.08 + 0.9 * v;
            double lit = SmoothStep(threshold - 0.04, threshold + 0.04, level);
            return lit < 0.002 ? Rgba.Clear : new Rgba(pal.Sample(v), lit);
        };
    }

    /// <summary>A lively 16-band spectrum shaped from the overall level (bass a little stronger).</summary>
    static double[] DerivedBands(double level, double seconds)
    {
        const int n = 16;
        level = Clamp01(level);
        var bands = new double[n];
        for (int i = 0; i < n; i++)
            bands[i] = Clamp01(level * (1.1 - 0.4 * i / n) * (0.45 + 0.55 * Noise.Value(i * 0.9 + seconds * 2.5, 3.3)));
        return bands;
    }

    // ------------------------------------------------------------------ screen

    static Shader ScreenSync(LayerFrame f)
    {
        var ctx = f.Ctx;
        int w = ctx.ScreenGridWidth, h = ctx.ScreenGridHeight;
        var grid = ctx.ScreenGrid;
        if (w <= 0 || h <= 0 || grid.Count < w * h)
        {
            var dim = Opaque(f.Scene.Background * 0.5);
            return (in Pixel _) => dim;
        }
        Rgba Cell(int col, int row) => Opaque(Rgbf.From(grid[Math.Clamp(row, 0, h - 1) * w + Math.Clamp(col, 0, w - 1)]));
        int Col(double x) => (int)Math.Floor(x * w);
        int Row(double y) => (int)Math.Floor(y * h);

        return (in Pixel p) =>
        {
            if (p.IsKey) return Cell(Col(p.X), Row(p.Y));
            // Edge LEDs take the nearest border of the screen, like an ambilight.
            double left = p.X * Aspect, right = (1 - p.X) * Aspect, top = p.Y, bottom = 1 - p.Y;
            double min = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            if (min == top) return Cell(Col(p.X), 0);
            if (min == bottom) return Cell(Col(p.X), h - 1);
            return min == left ? Cell(0, Row(p.Y)) : Cell(w - 1, Row(p.Y));
        };
    }
}
