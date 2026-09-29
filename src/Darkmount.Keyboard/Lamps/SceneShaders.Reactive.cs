using static Darkmount.Keyboard.Lamps.SceneMath;

namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// Effects driven by events: the order and speed of typing, music beats, screen flashes and the mouse. Like every
/// shader they depend only on the frame's <see cref="SceneContext"/> (the engine keeps the short event histories), so
/// they stay reproducible and keep no state between frames.
/// </summary>
internal static partial class SceneShaders
{
    // ------------------------------------------------------------------ typing

    /// <summary>Lightning arcs jump from each key to the next one you press; pressed keys flash.</summary>
    static Shader KeyLightning(LayerFrame f)
    {
        double now = f.Seconds, life = 0.6 / Math.Sqrt(f.SpeedFactor);
        var presses = f.Ctx.KeyPresses;
        var arcs = new List<(double Ax, double Ay, double Dx, double Dy, double Length, double Fade, int Seed)>();
        var flashes = new List<(double X, double Y, double Fade)>();
        for (int i = 0; i < presses.Count; i++)
        {
            double age = now - presses[i].At;
            if (age < 0 || age > life || !f.Scene.TryGetKey(presses[i].KeyId, out var b)) continue;
            double fade = Math.Pow(1 - age / life, 1.4);
            flashes.Add((b.X * Aspect, b.Y, fade));
            if (i == 0 || presses[i].At - presses[i - 1].At > 1.2 || !f.Scene.TryGetKey(presses[i - 1].KeyId, out var a)) continue;
            double dx = (b.X - a.X) * Aspect, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0.01) arcs.Add((a.X * Aspect, a.Y, dx, dy, length, fade, i * 7919 + (int)(presses[i].At * 997)));
        }
        if (arcs.Count == 0 && flashes.Count == 0) return (in Pixel _) => Rgba.Clear;
        int flicker = (int)Math.Floor(now * 24);
        var pal = f.Palette;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, y = p.Y, best = 0;
            foreach (var arc in arcs)
            {
                double t = Clamp01(((x - arc.Ax) * arc.Dx + (y - arc.Ay) * arc.Dy) / (arc.Length * arc.Length));
                double jag = (Noise.Value(t * 7 + arc.Seed % 1000 * 0.013, flicker * 0.61) - 0.5) * 0.5 * Math.Sin(Math.PI * t);
                double px = arc.Ax + arc.Dx * t - arc.Dy / arc.Length * jag, py = arc.Ay + arc.Dy * t + arc.Dx / arc.Length * jag;
                double d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                best = Math.Max(best, Math.Exp(-d * d / 0.006) * arc.Fade);
            }
            foreach (var fl in flashes)
            {
                double d2 = (x - fl.X) * (x - fl.X) + (y - fl.Y) * (y - fl.Y);
                best = Math.Max(best, Math.Exp(-d2 / 0.012) * fl.Fade);
            }
            if (best < 0.01) return Rgba.Clear;
            var c = pal.Count == 1 ? pal.First : pal.Sample(1 - best);
            return new Rgba(Rgbf.Lerp(c, Rgbf.White, 0.5 * best * best), Math.Min(1, best * 1.2)); // white-hot core
        };
    }

    /// <summary>Every key fires laser beams left and right along its row.</summary>
    static Shader LaserTyping(LayerFrame f)
    {
        double now = f.Seconds, life = 0.75;
        double speed = 5.5 * f.SpeedFactor;                            // keyboard heights per second
        var beams = new List<(double X, double Y, double Front, double Fade, Rgbf C)>();
        var pal = f.Palette;
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age < 0 || age > life || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            beams.Add((k.X * Aspect, k.Y, age * speed, 1 - age / life, pal[(int)(Noise.Hash(press.KeyId, (int)(press.At * 10)) * pal.Count)]));
        }
        if (beams.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, best = 0;
            Rgbf color = Rgbf.Black;
            foreach (var b in beams)
            {
                double dy = (p.Y - b.Y) / 0.07;
                if (dy is > 3 or < -3) continue;
                double dx = Math.Abs(x - b.X);
                double head = Math.Exp(-Math.Pow((dx - b.Front) / 0.22, 2));
                double trail = dx < b.Front ? 0.45 * Math.Exp(-(b.Front - dx) * 1.2) : 0;
                double v = (head + trail) * Math.Exp(-dy * dy) * b.Fade;
                if (v > best) { best = v; color = b.C; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(Rgbf.Lerp(color, Rgbf.White, 0.35 * best), Math.Min(1, best));
        };
    }

    /// <summary>Each press lights its key in the next colour of the rainbow (or your colours), fading slowly.</summary>
    static Shader RainbowTyping(LayerFrame f)
    {
        double now = f.Seconds, life = 3.2 / f.SpeedFactor;
        var pal = f.Palette;
        bool single = pal.Count == 1;
        var lit = new List<(double X, double Y, double Fade, Rgbf C)>();
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age < 0 || age > life || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            var c = single ? Rgbf.Hsv(press.At * 1.7) : pal.SampleCyclic(press.At * 0.9); // quick presses step through colours
            lit.Add((k.X * Aspect, k.Y, Math.Pow(1 - age / life, 1.3), c));
        }
        if (lit.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, best = 0;
            Rgbf color = Rgbf.Black;
            foreach (var l in lit)
            {
                double d2 = (x - l.X) * (x - l.X) + (p.Y - l.Y) * (p.Y - l.Y);
                double v = Math.Exp(-d2 / 0.01) * l.Fade;
                if (v > best) { best = v; color = l.C; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, best);
        };
    }

    /// <summary>
    /// Typing speed as a fighting-game combo: the frame fills around the keyboard and the keys warm up the faster you
    /// type; flat out, it all turns into a rainbow. Stop typing and it drains away.
    /// </summary>
    static Shader ComboMeter(LayerFrame f)
    {
        double now = f.Seconds, energy = 0;
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age >= 0 && age < 4) energy += Math.Exp(-age / 0.9);
        }
        double level = Clamp01(energy / (5.5 / f.SpeedFactor));        // ~6 keys a second fills it at speed 5
        var pal = f.Palette.Count > 1 ? f.Palette : TemperaturePalette;
        double shimmer = SmoothStep(0.85, 1, level);
        KeyPress? last = f.Ctx.KeyPresses.Count > 0 ? f.Ctx.KeyPresses[^1] : null;
        double lastAge = last is { } lp ? now - lp.At : 9;
        bool lastKnown = last is { } lk && f.Scene.TryGetKey(lk.KeyId, out _);
        return (in Pixel p) =>
        {
            if (!p.IsKey)
            {
                double pos = Frac(Angle(p.X, p.Y) + 0.25);              // from the top, clockwise
                if (pos > level) return new Rgba(pal.Sample(pos) * 0.06, 1);
                var bar = pal.Sample(pos);
                return Opaque(shimmer > 0 ? Rgbf.Lerp(bar, Rgbf.Hsv(pos + now * 0.8), shimmer) : bar);
            }
            var glow = pal.Sample(level) * (0.05 + 0.55 * level);
            if (shimmer > 0) glow = Rgbf.Lerp(glow, Rgbf.Hsv(p.X * 0.8 - now * 0.6) * 0.7, shimmer);
            if (lastKnown && lastAge < 0.35 && p.KeyId == last!.Value.KeyId) glow = Rgbf.Lerp(glow, Rgbf.White, 1 - lastAge / 0.35);
            return Opaque(glow);
        };
    }

    /// <summary>Sparks burst out of every key you press and fall away with gravity.</summary>
    static Shader KeySparks(LayerFrame f)
    {
        const int perPress = 7;
        double now = f.Seconds, life = 1.1;
        var pal = f.Palette;
        var sparks = new List<(double X, double Y, double Fade, Rgbf C)>();
        foreach (var press in f.Ctx.KeyPresses)
        {
            double t = now - press.At;
            if (t < 0 || t > life || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            int seed = press.KeyId * 31 + (int)(press.At * 100);
            for (int i = 0; i < perPress; i++)
            {
                double angle = Tau * (i / (double)perPress + Noise.Hash(seed, i, 1501) * 0.12) - Math.PI / 2; // mostly upwards first
                double v = (1.1 + 0.9 * Noise.Hash(seed, i, 1502)) * f.SpeedFactor;
                double x = k.X * Aspect + Math.Cos(angle) * v * t;
                double y = k.Y + Math.Sin(angle) * v * t * 0.6 + 2.6 * t * t;  // gravity pulls them down
                var c = pal.Count == 1 ? pal.First : pal[(int)(Noise.Hash(seed, i, 1503) * pal.Count)];
                sparks.Add((x, y, Math.Pow(1 - t / life, 1.6), Rgbf.Lerp(c, Rgbf.White, 0.6 * SmoothStep(0.25, 0, t))));
            }
        }
        if (sparks.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, best = 0;
            Rgbf color = Rgbf.Black;
            foreach (var s in sparks)
            {
                double dx = x - s.X, dy = p.Y - s.Y;
                if (dx is > 0.4 or < -0.4) continue;
                double v = Math.Exp(-(dx * dx + dy * dy) / 0.009) * s.Fade;
                if (v > best) { best = v; color = s.C; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, Math.Min(1, best * 1.3));
        };
    }

    // ------------------------------------------------------------------ music

    /// <summary>Every beat of the music sends a ring out from the centre and flashes the frame.</summary>
    static Shader BeatRings(LayerFrame f)
    {
        double now = f.Seconds, life = 1.1;
        var pal = f.Palette;
        var rings = new List<(double Radius, double Fade, Rgbf C)>();
        double edgeFlash = 0;
        Rgbf edgeColor = pal.First;
        foreach (double at in f.Ctx.BeatTimes)
        {
            double age = now - at;
            if (age < 0 || age > life) continue;
            var c = pal[(int)(Noise.Hash((int)(at * 100), 3, 1601) * pal.Count)];
            rings.Add((age * 3.4 * f.SpeedFactor, Math.Pow(1 - age / life, 1.3), c));
            double flash = Math.Exp(-age * 7);
            if (flash > edgeFlash) { edgeFlash = flash; edgeColor = c; }
        }
        if (rings.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            if (!p.IsKey) return edgeFlash < 0.01 ? Rgba.Clear : new Rgba(edgeColor, edgeFlash);
            double r = Radius(p.X, p.Y), best = 0;
            Rgbf color = Rgbf.Black;
            foreach (var ring in rings)
            {
                double off = (r - ring.Radius) / 0.2;
                double v = Math.Exp(-off * off) * ring.Fade;
                if (v > best) { best = v; color = ring.C; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, best);
        };
    }

    /// <summary>A glowing oscilloscope line across the keys: its waves grow with the music (bass on the left).</summary>
    static Shader Waveform(LayerFrame f)
    {
        var bands = f.Ctx.AudioBands.Count > 0 ? f.Ctx.AudioBands : DerivedBands(f.Ctx.AudioLevel, f.Seconds);
        int n = bands.Count;
        double t = f.T, level = Clamp01(f.Ctx.AudioLevel);
        var pal = f.Palette;
        double Band(double x)
        {
            double u = Clamp01(x) * (n - 1);
            int i = Math.Min((int)u, n - 2);
            return n == 1 ? bands[0] : bands[i] + (bands[i + 1] - bands[i]) * (u - i);
        }
        return (in Pixel p) =>
        {
            if (!p.IsKey)
            {
                double glow = 0.1 + 0.9 * level;
                return new Rgba(pal.Sample(p.X), glow * 0.8);
            }
            double amp = 0.08 + 0.42 * Clamp01(Band(p.X));
            double line = 0.5 + amp * Math.Sin(Tau * (p.X * 2.6 - t * 0.9));
            double d = (p.Y - line) / 0.13;
            double v = Math.Exp(-d * d);
            return v < 0.02 ? Rgba.Clear : new Rgba(pal.Count == 1 ? pal.First : pal.Sample(p.X), v);
        };
    }

    /// <summary>A nightclub: the bass kicks the frame, the mids colour the keys, the treble throws white sparkles.</summary>
    static Shader ClubLights(LayerFrame f)
    {
        var bands = f.Ctx.AudioBands.Count > 0 ? f.Ctx.AudioBands : DerivedBands(f.Ctx.AudioLevel, f.Seconds);
        int n = bands.Count;
        double Avg(double from, double to)
        {
            int a = Math.Clamp((int)(from * n), 0, n - 1), b = Math.Clamp((int)Math.Ceiling(to * n), a + 1, n);
            double sum = 0;
            for (int i = a; i < b; i++) sum += bands[i];
            return Clamp01(sum / (b - a));
        }
        double bass = Avg(0, 0.15), mid = Avg(0.15, 0.55), treble = Avg(0.55, 1);
        double t = f.T;
        var pal = f.Palette;
        int sparkleTick = (int)Math.Floor(f.Seconds * 14);
        var kick = pal.First * Math.Pow(bass, 1.4);
        return (in Pixel p) =>
        {
            if (!p.IsKey) return Opaque(kick);
            if (Noise.Hash(p.LampId, sparkleTick, 1701) < treble * 0.22) return Opaque(Rgbf.White * (0.6 + 0.4 * treble));
            var c = (pal.Count > 1 ? pal.SampleCyclic(p.X * 0.6 + t * 0.12) : Rgbf.Hsv(p.X * 0.6 + t * 0.05)) * (0.08 + 0.92 * mid);
            return Opaque(c + kick * 0.25);
        };
    }

    // ------------------------------------------------------------------ screen

    /// <summary>The whole keyboard glows in the screen's overall colour, a little richer; the frame brightest.</summary>
    static Shader ScreenMood(LayerFrame f)
    {
        var grid = f.Ctx.ScreenGrid;
        if (grid.Count == 0)
        {
            var dim = Opaque(f.Scene.Background * 0.5);
            return (in Pixel _) => dim;
        }
        double r = 0, g = 0, b = 0;
        foreach (var c in grid) { r += c.R; g += c.G; b += c.B; }
        var mean = new Rgbf(r / grid.Count / 255, g / grid.Count / 255, b / grid.Count / 255);
        double grey = (mean.R + mean.G + mean.B) / 3;
        var rich = new Rgbf(grey + (mean.R - grey) * 1.8, grey + (mean.G - grey) * 1.8, grey + (mean.B - grey) * 1.8);
        double peak = Math.Max(0.05, Math.Max(rich.R, Math.Max(rich.G, rich.B)));
        var mood = rich * Math.Min(1 / peak, 3) * (0.35 + 0.65 * Clamp01(peak));  // brightened, but a dark scene stays dim
        var keys = Opaque(mood * 0.75);
        var edges = Opaque(mood);
        return (in Pixel p) => p.IsKey ? keys : edges;
    }

    /// <summary>Explosions, muzzle flashes and lightning on screen flash the keyboard in their colour.</summary>
    static Shader ScreenFlashes(LayerFrame f)
    {
        double now = f.Seconds, best = 0;
        Rgbf color = Rgbf.White;
        double age0 = 1;
        foreach (var flash in f.Ctx.ScreenFlashes)
        {
            double age = now - flash.At;
            if (age < 0 || age > 0.7) continue;
            double v = Clamp01(flash.Strength) * Math.Exp(-age * 5 / f.SpeedFactor);
            if (v <= best) continue;
            best = v;
            age0 = age;
            color = Rgbf.Lerp(Rgbf.From(flash.Color), Rgbf.White, 0.35);
        }
        if (best < 0.01) return (in Pixel _) => Rgba.Clear;
        var pal = f.Palette;
        if (pal.Count > 1) color = Rgbf.Lerp(color, pal.First, 0.5);
        double radius = age0 * 6;
        return (in Pixel p) =>
        {
            double burst = Math.Exp(-Math.Pow((Radius(p.X, p.Y) - radius) / 0.5, 2)) * 0.4; // a shockwave on top
            return new Rgba(color, Math.Min(1, best * (0.75 + burst)));
        };
    }

    // ------------------------------------------------------------------ mouse

    /// <summary>
    /// The keyboard as a mini-map of your screens: a soft spotlight follows the mouse pointer, and clicks send out
    /// ripples (Dual: left click, right click).
    /// </summary>
    static Shader MouseSpotlight(LayerFrame f)
    {
        if (f.Ctx.MouseX is not { } mx || f.Ctx.MouseY is not { } my) return (in Pixel _) => Rgba.Clear;
        double now = f.Seconds;
        var pal = f.Palette;
        var clicks = new List<(double X, double Y, double Radius, double Fade, Rgbf C)>();
        foreach (var click in f.Ctx.MouseClicks)
        {
            double age = now - click.At;
            if (age < 0 || age > 0.9) continue;
            var c = click.Button == 1 && pal.Count > 1 ? pal[1] : click.Button == 2 && pal.Count > 2 ? pal[2] : pal.First;
            clicks.Add((click.X, click.Y, age * 2.6 * f.SpeedFactor, Math.Pow(1 - age / 0.9, 1.4), c));
        }
        var spot = pal.First;
        return (in Pixel p) =>
        {
            double d = Distance(p.X, p.Y, mx, my);
            double best = Math.Exp(-d * d / 0.09);
            Rgbf color = Rgbf.Lerp(spot, Rgbf.White, 0.35 * best);
            foreach (var c in clicks)
            {
                double off = (Distance(p.X, p.Y, c.X, c.Y) - c.Radius) / 0.16;
                double v = Math.Exp(-off * off) * c.Fade;
                if (v > best) { best = v; color = c.C; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, best);
        };
    }

    // ------------------------------------------------------------------ calm typing

    /// <summary>
    /// The key you press eases into its press colour, holds a moment and glides back; its neighbours glow faintly. Made
    /// to sit on a static colour for everyday typing (Gradient: each key gets its own colour from the list).
    /// </summary>
    static Shader SoftPress(LayerFrame f)
    {
        // The engine keeps 4 s of presses (SceneContext.KeyPresses): every fade here ends before that, even at speed 1.
        double now = f.Seconds, hold = 0.12, fade = Math.Min(3.5, 1.4 / f.SpeedFactor);
        var pal = f.Palette;
        var lit = new List<(int Key, double X, double Y, double Level, Rgbf C)>();
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age < 0 || age > hold + fade || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            double level = 1 - SmoothStep(hold, hold + fade, age);
            var c = pal.Count == 1 ? pal.First : pal[(int)(Noise.Hash(press.KeyId, 5, 1801) * pal.Count)];
            lit.Add((press.KeyId, k.X * Aspect, k.Y, level, c));
        }
        if (lit.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            if (!p.IsKey) return Rgba.Clear;
            double x = p.X * Aspect, best = 0;
            Rgbf color = Rgbf.Black;
            foreach (var l in lit)
            {
                double v = p.KeyId == l.Key ? l.Level
                    : 0.22 * l.Level * Math.Exp(-((x - l.X) * (x - l.X) + (p.Y - l.Y) * (p.Y - l.Y)) / 0.03); // reaches the next key only
                if (v > best) { best = v; color = l.C; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, best);
        };
    }

    /// <summary>
    /// For a dark room: the keyboard rests very dim and softly brightens while you type, easing back when you stop
    /// (Gradient: the colours laid out left to right).
    /// </summary>
    static Shader WakeOnType(LayerFrame f)
    {
        // Bright right after a press, easing back to rest within the 4 s of presses the engine keeps (so it never snaps).
        double now = f.Seconds, awake = 0, hold = 0.6, fade = Math.Min(3.2, 1.6 / f.SpeedFactor);
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age >= 0) awake = Math.Max(awake, 1 - SmoothStep(hold, hold + fade, age));
        }
        double level = 0.12 + 0.88 * SmoothStep(0, 1, awake);
        var pal = f.Palette;
        if (pal.Count == 1) { var c = Opaque(pal.First * level); return (in Pixel _) => c; }
        return (in Pixel p) => Opaque(pal.Sample(p.X) * level);
    }

    /// <summary>
    /// The whole keyboard eases from a calm colour (the first) towards an active one (the last) the more you type, and
    /// drifts back when you pause. Subtle: no flashes, just the mood of your typing.
    /// </summary>
    static Shader TypingMood(LayerFrame f)
    {
        double now = f.Seconds, energy = 0;
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age >= 0 && age < 4) energy += Math.Exp(-age / 1.2) * (1 - SmoothStep(3, 4, age)); // gone smoothly by 4 s
        }
        double t = SmoothStep(0, 1, Clamp01(energy / (6 / f.SpeedFactor)));
        var pal = f.Palette;
        var c = Opaque(pal.Count == 1 ? pal.First * (0.35 + 0.65 * t) : pal.Sample(t));
        return (in Pixel _) => c;
    }
}
