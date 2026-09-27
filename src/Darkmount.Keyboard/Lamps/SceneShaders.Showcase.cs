using static Darkmount.Keyboard.Lamps.SceneMath;

namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// The showcase effects: starfield, fireworks, comets, lightning, glitch, radar, disco and the particle family (snow,
/// bubbles, embers, fireflies). Like the classic ones, all randomness comes from <see cref="Noise.Hash"/> of the time slot
/// and index, so a frame depends only on the clock: reproducible, and nothing to keep between frames.
/// </summary>
internal static partial class SceneShaders
{
    // ------------------------------------------------------------------ space

    /// <summary>Stars streaking out of the centre like a jump to hyperspace (Inward: flying backwards).</summary>
    static Shader Starfield(LayerFrame f)
    {
        const int count = 44;
        bool inward = f.Direction == SceneDirection.Inward;
        double t = f.T * 0.32;
        var pal = f.Palette;
        bool single = f.Mode == SceneColorMode.Single;
        var stars = new (double Ux, double Uy, double Head, double From, double To, double Glow, Rgbf C)[count];
        for (int i = 0; i < count; i++)
        {
            double angle = Tau * Noise.Hash(i, 1, 501);
            double life = Frac(t * (0.55 + 0.9 * Noise.Hash(i, 2, 501)) + Noise.Hash(i, 3, 501)); // 0 centre → 1 rim
            double q = inward ? 1 - life : life;
            double head = q * q * MaxRadius * 1.1;                     // accelerates away from the centre
            double length = 0.04 + 0.55 * q * q;                       // and stretches into a streak
            double from = inward ? head : Math.Max(0, head - length), to = inward ? head + length : head;
            var c = single ? Rgbf.Lerp(pal.First, Rgbf.White, 0.4 * (1 - q)) : pal[(int)(Noise.Hash(i, 4, 501) * pal.Count)];
            stars[i] = (Math.Cos(angle), Math.Sin(angle), head, from, to, SmoothStep(0.02, 0.3, q), c);
        }
        return (in Pixel p) =>
        {
            double x = (p.X - 0.5) * Aspect, y = p.Y - 0.5;
            double best = 0;
            Rgbf color = Rgbf.Black;
            foreach (var s in stars)
            {
                double along = x * s.Ux + y * s.Uy;                    // position along the star's ray
                if (along < s.From - 0.3 || along > s.To + 0.3) continue;
                double across = -x * s.Uy + y * s.Ux;
                if (across is > 0.3 or < -0.3) continue;
                double a = Math.Clamp(along, s.From, s.To);
                double d2 = (along - a) * (along - a) + across * across;
                double towardsHead = 1 - Math.Abs(a - s.Head) / Math.Max(1e-6, s.To - s.From); // bright head, fading tail
                double v = Math.Exp(-d2 / 0.012) * (0.3 + 0.7 * towardsHead) * s.Glow;
                if (v > best) { best = v; color = s.C; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, Math.Min(1, best * 1.3));
        };
    }

    // ------------------------------------------------------------------ celebrations

    /// <summary>Bursts at random places: a flash, then a glittering ring that grows, slows and fades.</summary>
    static Shader Fireworks(LayerFrame f)
    {
        const int slots = 5;                                           // bursts in the air at once
        double t = f.T * 0.5;
        var pal = f.Palette;
        int glitter = (int)Math.Floor(f.Seconds * 14);
        var bursts = new List<(double X, double Y, double R, double Age, double Fade, Rgbf C, int Seed)>();
        for (int s = 0; s < slots; s++)
        {
            double pos = t * (0.8 + 0.4 * Noise.Hash(s, 0, 601)) + Noise.Hash(s, 9, 601);
            int cycle = (int)Math.Floor(pos);
            int seed = cycle * 31 + s;
            if (Noise.Hash(seed, 1, 601) > 0.8) continue;              // now and then a quiet moment
            double age = pos - cycle;
            double size = 0.55 + 0.65 * Noise.Hash(seed, 4, 601);
            var c = pal.Count == 1 ? pal.First : pal[(int)(Noise.Hash(seed, 5, 601) * pal.Count)];
            bursts.Add((0.1 + 0.8 * Noise.Hash(seed, 2, 601), 0.15 + 0.7 * Noise.Hash(seed, 3, 601),
                size * (1 - Math.Pow(1 - age, 3)), age, Math.Pow(1 - age, 1.5), c, seed));
        }
        if (bursts.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            double sum = 0, r = 0, g = 0, b = 0;
            foreach (var burst in bursts)
            {
                double d = Distance(p.X, p.Y, burst.X, burst.Y);
                double ring = (d - burst.R) / 0.14;
                double v = ring is > 3 or < -3 ? 0 : Math.Exp(-ring * ring) * (0.55 + 0.45 * Noise.Hash(p.LampId, glitter, burst.Seed));
                v += burst.Age < 0.1 ? Math.Exp(-d * d / 0.03) * (1 - burst.Age / 0.1) : 0; // the launch flash
                v *= burst.Fade;
                if (v < 0.004) continue;
                sum += v;
                r += burst.C.R * v; g += burst.C.G * v; b += burst.C.B * v;
            }
            return sum < 0.01 ? Rgba.Clear : new Rgba(new Rgbf(r / sum, g / sum, b / sum), Math.Min(1, sum));
        };
    }

    /// <summary>Blocks of keys jump to new colours on every beat; the edge lights chase around the keyboard.</summary>
    static Shader Disco(LayerFrame f)
    {
        double pos = f.Seconds * (1.2 + 1.1 * f.SpeedFactor);           // beats per second: ~2.3 at speed 5
        int beat = (int)Math.Floor(pos);
        double punch = 0.3 + 0.7 * Math.Pow(1 - (pos - beat), 2);
        var pal = f.Palette;
        bool single = pal.Count == 1;
        Rgbf Pick(double h) => single ? Rgbf.Hsv(h) : pal[(int)(h * pal.Count)];
        return (in Pixel p) =>
        {
            if (!p.IsKey)
            {
                int segment = (int)Math.Floor(Angle(p.X, p.Y) * 24);    // 24 chasing segments
                return Opaque(Pick(Noise.Hash((segment + beat) % 24, 3, 1003)) * (0.35 + 0.65 * punch));
            }
            int cx = (int)(p.X * 9), cy = (int)(p.Y * 3);               // 9 × 3 tiles of keys, like a dance floor
            if (Noise.Hash(cx, cy * 101 + beat, 1002) < 0.22) return Opaque(Rgbf.Black);
            return Opaque(Pick(Noise.Hash(cx, cy * 101 + beat, 1001)) * punch);
        };
    }

    // ------------------------------------------------------------------ motion

    /// <summary>
    /// Comets racing around the frame and orbiting across the keys, their long tails fading behind them (one per colour,
    /// up to four, evenly spaced).
    /// </summary>
    static Shader Comet(LayerFrame f)
    {
        var pal = f.Palette;
        int count = Math.Clamp(pal.Count, 1, 4);
        double sign = f.Direction == SceneDirection.CounterClockwise ? -1 : 1;
        double lap = Frac(f.T * 0.17 * sign);
        const double trail = 0.3;                                      // tail length, in laps
        return (in Pixel p) =>
        {
            double angle = Angle(p.X, p.Y), onTrack = 1;
            if (p.IsKey)
            {
                // The keys' track: an ellipse through the middle of the key area.
                double ex = (p.X - 0.5) * Aspect / 1.35, ey = (p.Y - 0.5) / 0.34;
                double off = (Math.Sqrt(ex * ex + ey * ey) - 1) / 0.3;
                onTrack = Math.Exp(-off * off);
            }
            double best = 0;
            Rgbf color = Rgbf.Black;
            for (int i = 0; i < count; i++)
            {
                double behind = Frac((lap + i / (double)count - angle) * sign); // 0 at the head, growing along the tail
                if (behind > trail) continue;
                double v = Math.Pow(1 - behind / trail, 2.4) * onTrack;
                if (v <= best) continue;
                best = v;
                color = Rgbf.Lerp(pal[i], Rgbf.White, 0.55 * SmoothStep(0.05, 0, behind)); // white-hot head
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, best);
        };
    }

    /// <summary>A radar beam sweeping around the centre, contacts glowing up as it passes, faint range rings.</summary>
    static Shader Radar(LayerFrame f)
    {
        var pal = f.Palette;
        var beam = pal.First;
        var screen = pal.Count > 1 ? pal[1] : pal.First * 0.04;
        double sign = f.Direction == SceneDirection.CounterClockwise ? -1 : 1;
        double turns = f.T * 0.22;
        double sweep = Frac(turns * sign);
        int round = (int)Math.Floor(turns);
        return (in Pixel p) =>
        {
            double behind = Frac((sweep - Angle(p.X, p.Y)) * sign);
            double v = Math.Exp(-behind * 10);
            double rings = 0.5 + 0.5 * Math.Cos(Tau * Radius(p.X, p.Y) * 2.4);
            v = Math.Max(v, 0.12 * SmoothStep(0.85, 1, rings));
            if (Noise.Hash(p.LampId, round, 901) < 0.06) v = Math.Max(v, Math.Exp(-behind * 2.2)); // a contact
            return Opaque(Rgbf.Lerp(screen, beam, Clamp01(v)));
        };
    }

    // ------------------------------------------------------------------ weather

    /// <summary>A dark sky; now and then a jagged bolt strikes with a double flash that lights up its side of the keyboard.</summary>
    static Shader Lightning(LayerFrame f)
    {
        var pal = f.Palette;
        var flash = pal.First;
        var sky = pal.Count > 1 ? pal[1] : new Rgbf(0.01, 0.015, 0.05);
        double slotLength = 1.1 / f.SpeedFactor, now = f.Seconds;
        int slot = (int)Math.Floor(now / slotLength);
        var strikes = new List<(double X, double Level, int Seed, double Reach)>();
        for (int s = slot - 1; s <= slot; s++)                        // a strike late in the last slot may still glow
        {
            if (Noise.Hash(s, 1, 701) > 0.42) continue;
            double age = now - (s + 0.6 * Noise.Hash(s, 2, 701)) * slotLength;
            if (age is < 0 or > 0.9) continue;
            double level = Math.Exp(-age * 7) + 0.8 * Math.Exp(-Math.Pow((age - 0.18) / 0.05, 2)); // flash, flicker
            strikes.Add((0.08 + 0.84 * Noise.Hash(s, 3, 701), Clamp01(level), s, 0.55 + 0.45 * Noise.Hash(s, 4, 701)));
        }
        if (strikes.Count == 0) { var calm = Opaque(sky); return (in Pixel _) => calm; }
        return (in Pixel p) =>
        {
            double light = 0;
            foreach (var s in strikes)
            {
                double side = (p.X - s.X) * Aspect;
                double glow = Math.Exp(-side * side / 3.0) * 0.45;
                double boltX = s.X + (Noise.Value(p.Y * 3.5 + s.Seed * 7.3, 5.1) - 0.5) * 0.12; // the jagged channel
                double d = (p.X - boltX) * Aspect / 0.14;
                double bolt = p.Y <= s.Reach ? Math.Exp(-d * d) : 0;
                light = Math.Max(light, (glow + bolt) * s.Level);
            }
            return Opaque(sky + flash * Math.Min(1.2, light));
        };
    }

    // ------------------------------------------------------------------ digital

    /// <summary>A dim flowing gradient hit by digital corruption: torn rows, flickering blocks, dead pixels.</summary>
    static Shader Glitch(LayerFrame f)
    {
        var pal = f.Palette;
        double t = f.T;
        int tick = (int)Math.Floor(f.Seconds * (5 + 4 * f.SpeedFactor));
        var blocks = new List<(double X0, double X1, double Y0, double Y1, Rgbf C)>();
        for (int k = 0; k < 3; k++)
        {
            if (Noise.Hash(tick, k, 802) > 0.45) continue;
            double x = Noise.Hash(tick, k, 803), y = Noise.Hash(tick, k, 804);
            double w = 0.05 + 0.18 * Noise.Hash(tick, k, 805), h = 0.15 + 0.35 * Noise.Hash(tick, k, 806);
            var c = Noise.Hash(tick, k, 807) < 0.2 ? Rgbf.White : pal[(int)(Noise.Hash(tick, k, 808) * pal.Count)];
            blocks.Add((x, x + w, y, y + h, c));
        }
        return (in Pixel p) =>
        {
            double u = p.X * 0.7 + t * 0.06;
            var c = pal.SampleCyclic(u) * 0.28;
            int row = (int)(p.Y * 6);
            if (Noise.Hash(tick, row, 801) < 0.1) c = pal.SampleCyclic(u + 0.5 + 0.3 * Noise.Hash(tick, row, 809)); // torn row
            foreach (var b in blocks)
                if (p.X >= b.X0 && p.X <= b.X1 && p.Y >= b.Y0 && p.Y <= b.Y1) c = b.C;
            if (Noise.Hash(p.LampId, tick, 810) < 0.03) c = Rgbf.Black;  // dead pixels
            return Opaque(c);
        };
    }

    // ------------------------------------------------------------------ particles: snow, bubbles, embers, fireflies

    /// <param name="Speed">Heights per effect-second (min, max); fireflies wander instead.</param>
    /// <param name="Size">Radius in keyboard heights (a key is ~0.16 wide).</param>
    sealed record ParticleStyle(int Count, double MinSpeed, double MaxSpeed, double Size, double Sway, bool Rise,
        bool Wander, bool Flicker, bool Ring, int Salt);

    static readonly ParticleStyle SnowStyle = new(34, 0.07, 0.15, 0.13, 0.22, Rise: false, Wander: false, Flicker: false, Ring: false, 1101);
    static readonly ParticleStyle BubbleStyle = new(12, 0.1, 0.2, 0.27, 0.12, Rise: true, Wander: false, Flicker: false, Ring: true, 1201);
    static readonly ParticleStyle EmberStyle = new(28, 0.16, 0.34, 0.11, 0.3, Rise: true, Wander: false, Flicker: true, Ring: false, 1301);
    static readonly ParticleStyle FireflyStyle = new(12, 0, 0, 0.14, 0, Rise: false, Wander: true, Flicker: false, Ring: false, 1401);

    /// <summary>
    /// Soft round particles: falling (snow), rising with a bright rim (bubbles), rising and flickering out (embers) or
    /// wandering and blinking (fireflies). Single/Gradient: particles over the layers below; Dual: particles, background.
    /// </summary>
    static Shader Particles(LayerFrame f, ParticleStyle s)
    {
        var pal = f.Palette;
        var mode = f.Mode;
        double t = f.T;
        var parts = new (double X, double Y, double R, double A, Rgbf C)[s.Count];
        for (int i = 0; i < s.Count; i++)
        {
            double H(int k) => Noise.Hash(i, k, s.Salt);
            double x, y, a = 1;
            if (s.Wander)
            {
                x = Aspect * (0.5 + 0.95 * (Noise.Value(t * 0.12 + H(1) * 60, 1.7) - 0.5));
                y = 0.5 + 1.1 * (Noise.Value(t * 0.1 + H(2) * 60, 5.3) - 0.5);
                double period = 2.2 + 2.4 * H(3), q = Frac(t / period + H(4));
                a = q < 0.4 ? Math.Sin(Math.PI * q / 0.4) : 0;          // glow, then dark for a while
            }
            else
            {
                double travel = Frac(t * (s.MinSpeed + (s.MaxSpeed - s.MinSpeed) * H(1)) + H(2)) * (1 + 2 * s.Size) - s.Size;
                y = s.Rise ? 1 - travel : travel;
                x = Aspect * H(3) + s.Sway * Math.Sin(t * (0.5 + 0.7 * H(4)) + Tau * H(5));
                if (s.Flicker) a = (0.5 + 0.5 * Noise.Value(t * 6 + H(6) * 40)) * SmoothStep(-0.1, 0.45, y); // die out rising
            }
            var c = mode == SceneColorMode.Gradient ? pal[(int)(H(7) * pal.Count)] : pal.First;
            parts[i] = (x, y, s.Size * (0.75 + 0.5 * H(8)), a, c);
        }
        bool dual = mode == SceneColorMode.Dual;
        var background = dual ? pal[1] : Rgbf.Black;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, y = p.Y, best = 0;
            Rgbf color = Rgbf.Black;
            foreach (var part in parts)
            {
                if (part.A <= 0.01) continue;
                double dx = x - part.X, dy = y - part.Y;
                if (dx > part.R * 3 || dx < -part.R * 3 || dy > part.R * 3 || dy < -part.R * 3) continue;
                double d = Math.Sqrt(dx * dx + dy * dy) / part.R;
                double v = s.Ring ? Math.Exp(-Math.Pow((d - 0.85) / 0.3, 2)) + 0.2 * Math.Exp(-d * d * 2) : Math.Exp(-d * d * 1.4);
                v *= part.A;
                if (v > best) { best = v; color = part.C; }
            }
            best = Math.Min(1, best);
            if (dual) return Opaque(Rgbf.Lerp(background, color, best));
            return best < 0.01 ? Rgba.Clear : new Rgba(color, best);
        };
    }
}
