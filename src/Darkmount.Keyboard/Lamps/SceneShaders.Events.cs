using static Darkmount.Keyboard.Lamps.SceneMath;

namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// More event-driven effects: paint, drops and a snake that follow your typing, Space/Enter/Backspace waves, a VU meter
/// and the time of day. Like every shader they depend only on the frame's <see cref="SceneContext"/>, and every typing
/// fade ends within the 4 s of presses the engine keeps.
/// </summary>
internal static partial class SceneShaders
{
    static readonly HashSet<int> EnterKeyIds = KeyIdsWithUsage(0x28, 0x58);
    static readonly HashSet<int> BackspaceKeyIds = KeyIdsWithUsage(0x2A);
    static readonly HashSet<int> SpaceKeyIds = KeyIdsWithUsage(0x2C);

    static HashSet<int> KeyIdsWithUsage(params int[] usages) =>
        [.. KeyIds.All.Where(k => k.HidUsage is { } u && usages.Contains(u)).Select(k => (int)k.Id)];

    // ------------------------------------------------------------------ typing

    /// <summary>
    /// Every key you press throws a splash of paint (a blob with a few droplets) that lands on top of the older ones,
    /// dries and fades (Gradient: a random colour from the list each time).
    /// </summary>
    static Shader PaintSplash(LayerFrame f)
    {
        double now = f.Seconds, life = Math.Min(3.8, 2.4 / f.SpeedFactor);
        var pal = f.Palette;
        var splats = new List<(double X, double Y, double R, double Grow, double Level, double Nx, double Ny, (double X, double Y)[] Drops, Rgbf C)>();
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age < 0 || age > life || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            int seed = press.KeyId * 131 + (int)(press.At * 1000);
            var c = pal.Count == 1 ? pal.First : pal[(int)(Noise.Hash(seed, 1, 1901) * pal.Count)];
            c = Rgbf.Lerp(c, Rgbf.White, 0.35 * SmoothStep(0.3, 0, age)); // wet paint shines for a moment
            double grow = SmoothStep(0, 0.12, age), r = 0.2 + 0.12 * Noise.Hash(seed, 2, 1902);
            var drops = new (double, double)[3];
            for (int i = 0; i < drops.Length; i++)
            {
                double a = Tau * Noise.Hash(seed, i, 1903), reach = r * (1.5 + 0.6 * Noise.Hash(seed, i, 1904)) * grow;
                drops[i] = (Math.Cos(a) * reach, Math.Sin(a) * reach * 0.6);
            }
            splats.Add((k.X * Aspect, k.Y, r, grow, 1 - SmoothStep(life * 0.45, life, age),
                Noise.Hash(seed, 3, 1905) * 40, Noise.Hash(seed, 4, 1906) * 40, drops, c));
        }
        if (splats.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, alpha = 0;
            Rgbf color = Rgbf.Black;
            foreach (var s in splats) // oldest first, so newer paint lands on top
            {
                double dx = x - s.X, dy = p.Y - s.Y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > s.R * 2.4) continue;
                double v = 0;
                if (d > 1e-6)
                {
                    // A wobbly rim: noise sampled around a circle has no seam.
                    double edge = s.R * s.Grow * (0.65 + 0.7 * Noise.Value(s.Nx + dx / d * 1.3, s.Ny + dy / d * 1.3));
                    v = SmoothStep(edge + 0.05, edge - 0.05, d);
                }
                else v = s.Grow;
                foreach (var (ox, oy) in s.Drops)
                {
                    double ex = dx - ox, ey = dy - oy;
                    v = Math.Max(v, Math.Exp(-(ex * ex + ey * ey) / 0.004) * s.Grow);
                }
                v *= s.Level;
                if (v <= 0.01) continue;
                color = alpha == 0 ? s.C : Rgbf.Lerp(color, s.C, v);
                alpha = Math.Max(alpha, v);
            }
            return alpha < 0.01 ? Rgba.Clear : new Rgba(color, alpha);
        };
    }

    /// <summary>Each key you press lets a drop of light fall down the keyboard; it splashes along the bottom edge.</summary>
    static Shader KeyDrops(LayerFrame f)
    {
        double now = f.Seconds, gravity = 2.6 * f.SpeedFactor, splash = 0.7;
        var pal = f.Palette;
        var drops = new List<(double X, double Y, Rgbf C)>();
        var splashes = new List<(double X, double Spread, double Fade, Rgbf C)>();
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age < 0 || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            var c = pal.Count == 1 ? pal.First : pal[(int)(Noise.Hash(press.KeyId, (int)(press.At * 10), 2001) * pal.Count)];
            double fall = Math.Sqrt(2 * Math.Max(0.02, 1.02 - k.Y) / gravity); // under 1.7 s even at speed 1
            if (age < fall) drops.Add((k.X * Aspect, k.Y + 0.5 * gravity * age * age, c));
            else if (age < fall + splash) splashes.Add((k.X, (age - fall) * 1.2, Math.Pow(1 - (age - fall) / splash, 1.5), c));
        }
        if (drops.Count == 0 && splashes.Count == 0) return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, best = 0;
            Rgbf color = Rgbf.Black;
            if (p.IsKey)
                foreach (var d in drops)
                {
                    double dx = x - d.X, dy = p.Y - d.Y;
                    if (dx is > 0.4 or < -0.4) continue;
                    double body = Math.Exp(-dy * dy / 0.004);
                    if (dy < 0) body = Math.Max(body, 0.5 * Math.Exp(dy / 0.12)); // a short tail above the drop
                    double v = Math.Exp(-dx * dx / 0.008) * body;
                    if (v > best) { best = v; color = Rgbf.Lerp(d.C, Rgbf.White, 0.3 * v); }
                }
            double low = SmoothStep(p.IsKey ? 0.8 : 0.7, 1, p.Y) * (p.IsKey ? 0.6 : 1);
            if (low > 0)
                foreach (var s in splashes)
                {
                    double off = (Math.Abs(p.X - s.X) - s.Spread * 0.3) / 0.05;
                    double v = (Math.Exp(-off * off) + 0.5 * Math.Exp(-Math.Pow((p.X - s.X) / 0.04, 2))) * s.Fade * low;
                    if (v > best) { best = v; color = s.C; }
                }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, Math.Min(1, best));
        };
    }

    /// <summary>
    /// The keys that end words and lines get their own moments: Space sends a wave sideways from the space bar, Enter a
    /// burst up from the Enter key (and a flash of the frame), Backspace blinks; every other key glows softly.
    /// Colours: space, enter, backspace.
    /// </summary>
    static Shader WordWaves(LayerFrame f)
    {
        double now = f.Seconds, sf = f.SpeedFactor;
        var pal = f.Palette;
        Rgbf space = pal.First, enter = pal.Count > 1 ? pal[1] : pal.First, back = pal.Count > 2 ? pal[2] : new Rgbf(1, 0.12, 0.16);
        var waves = new List<(double X, double Y, double Radius, double Fade)>();
        var bursts = new List<(double X, double Y, double Radius, double Fade)>();
        var blinks = new List<(double X, double Y, double Level)>();
        var taps = new Dictionary<int, double>();
        double frame = 0;
        foreach (var press in f.Ctx.KeyPresses)
        {
            double age = now - press.At;
            if (age < 0 || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            if (SpaceKeyIds.Contains(press.KeyId))
            {
                if (age < 1.2) waves.Add((k.X * Aspect, k.Y, age * 4.5 * sf, Math.Pow(1 - age / 1.2, 1.2)));
            }
            else if (EnterKeyIds.Contains(press.KeyId))
            {
                if (age < 1.4) bursts.Add((k.X * Aspect, k.Y, age * 3.2 * sf, Math.Pow(1 - age / 1.4, 1.3)));
                frame = Math.Max(frame, Math.Exp(-age * 5));
            }
            else if (BackspaceKeyIds.Contains(press.KeyId))
            {
                if (age < 0.6) blinks.Add((k.X * Aspect, k.Y, (0.55 + 0.45 * Math.Cos(age * Tau / 0.3)) * (1 - age / 0.6)));
            }
            else if (age < 0.5) taps[press.KeyId] = Math.Max(taps.GetValueOrDefault(press.KeyId), 0.6 * Math.Pow(1 - age / 0.5, 2));
        }
        if (waves.Count == 0 && bursts.Count == 0 && blinks.Count == 0 && taps.Count == 0 && frame < 0.01)
            return (in Pixel _) => Rgba.Clear;
        return (in Pixel p) =>
        {
            if (!p.IsKey) return frame < 0.01 ? Rgba.Clear : new Rgba(enter, frame);
            double x = p.X * Aspect, best = taps.GetValueOrDefault(p.KeyId);
            Rgbf color = space;
            foreach (var w in waves)
            {
                double dy = p.Y - w.Y, off = (Math.Abs(x - w.X) - w.Radius) / 0.25;
                double v = Math.Exp(-off * off - dy * dy * 2) * w.Fade;
                if (v > best) { best = v; color = space; }
            }
            foreach (var b in bursts)
            {
                double off = (Math.Sqrt((x - b.X) * (x - b.X) + (p.Y - b.Y) * (p.Y - b.Y)) - b.Radius) / 0.22;
                double v = Math.Exp(-off * off) * b.Fade;
                if (v > best) { best = v; color = enter; }
            }
            foreach (var bl in blinks)
            {
                double d2 = (x - bl.X) * (x - bl.X) + (p.Y - bl.Y) * (p.Y - bl.Y);
                double v = Math.Exp(-d2 / 0.02) * bl.Level;
                if (v > best) { best = v; color = back; }
            }
            return best < 0.01 ? Rgba.Clear : new Rgba(color, Math.Min(1, best));
        };
    }

    /// <summary>
    /// A glowing snake slithers from key to key along the path you type: its head glides to each new key, the body
    /// follows your last keys (the colours run tail → head), and it curls away when you stop.
    /// </summary>
    static Shader TypingSnake(LayerFrame f)
    {
        double now = f.Seconds;
        var pts = new List<(double X, double Y, double At)>(); // At: when the head arrived at that key
        double last = double.NegativeInfinity;                  // the latest press, repeats of the same key included
        foreach (var press in f.Ctx.KeyPresses)
        {
            if (now - press.At is < 0 or >= 3.9 || !f.Scene.TryGetKey(press.KeyId, out var k)) continue;
            last = Math.Max(last, press.At);
            if (pts.Count == 0 || Math.Abs(pts[^1].X - k.X * Aspect) + Math.Abs(pts[^1].Y - k.Y) > 1e-6) pts.Add((k.X * Aspect, k.Y, press.At));
        }
        if (pts.Count == 0) return (in Pixel _) => Rgba.Clear;
        if (pts.Count > 9) pts = pts[^9..];
        double glide = Math.Clamp(0.14 / f.SpeedFactor, 0.05, 0.4);
        double since = now - pts[^1].At, idle = 1 - SmoothStep(1.5, 3.9, now - last); // holding a key keeps it awake
        double headT = pts.Count > 1 ? SmoothStep(0, 1, Clamp01(since / glide)) : 1;
        var head = pts.Count > 1
            ? (X: pts[^2].X + (pts[^1].X - pts[^2].X) * headT, Y: pts[^2].Y + (pts[^1].Y - pts[^2].Y) * headT)
            : (X: pts[0].X, Y: pts[0].Y);
        int n = pts.Count;
        var segments = new List<(double Ax, double Ay, double Dx, double Dy, double L2, double U0, double U1)>();
        for (int i = 1; i < n; i++)
        {
            var (bx, by) = i == n - 1 ? head : (pts[i].X, pts[i].Y);
            double dx = bx - pts[i - 1].X, dy = by - pts[i - 1].Y;
            segments.Add((pts[i - 1].X, pts[i - 1].Y, dx, dy, Math.Max(1e-9, dx * dx + dy * dy), (i - 1) / (double)(n - 1), i / (double)(n - 1)));
        }
        var pal = f.Palette;
        return (in Pixel p) =>
        {
            double x = p.X * Aspect, best = 0, bestU = 1;
            foreach (var s in segments)
            {
                double t = Clamp01(((x - s.Ax) * s.Dx + (p.Y - s.Ay) * s.Dy) / s.L2);
                double ex = x - (s.Ax + s.Dx * t), ey = p.Y - (s.Ay + s.Dy * t);
                double u = s.U0 + (s.U1 - s.U0) * t;
                double v = Math.Exp(-(ex * ex + ey * ey) / 0.012) * (0.25 + 0.75 * u);
                if (v > best) { best = v; bestU = u; }
            }
            double hx = x - head.X, hy = p.Y - head.Y, eye = Math.Exp(-(hx * hx + hy * hy) / 0.02);
            best = Math.Max(best, eye) * idle;
            if (best < 0.01) return Rgba.Clear;
            var c = pal.Count == 1 ? pal.First : pal.Sample(bestU);
            return new Rgba(Rgbf.Lerp(c, Rgbf.White, 0.45 * eye * eye), Math.Min(1, best * 1.2));
        };
    }

    // ------------------------------------------------------------------ music

    /// <summary>
    /// A stereo-style VU meter: every key row is a level bar growing out from the centre (bass at the bottom, treble on
    /// top) and the frame fills up from the bottom with the volume. Colours run from the centre outwards.
    /// </summary>
    static Shader VuMeter(LayerFrame f)
    {
        var bands = f.Ctx.AudioBands.Count > 0 ? f.Ctx.AudioBands : DerivedBands(f.Ctx.AudioLevel, f.Seconds);
        int n = bands.Count;
        double level = Math.Pow(Clamp01(f.Ctx.AudioLevel), 0.8);
        var pal = f.Palette.Count > 1 ? f.Palette : f.Palette.AsRamp();
        double Band(double u)
        {
            double b = Clamp01(u) * (n - 1);
            int i = Math.Min((int)b, Math.Max(0, n - 2));
            return n == 1 ? bands[0] : bands[i] + (bands[i + 1] - bands[i]) * (b - i);
        }
        return (in Pixel p) =>
        {
            if (!p.IsKey)
            {
                double up = Math.Abs(Frac(Angle(p.X, p.Y) + 0.25) - 0.5) * 2; // 0 at the bottom centre, 1 at the top
                var c = pal.Sample(up);
                return Opaque(up <= level ? c : c * 0.05);
            }
            double reach = Math.Pow(Clamp01(Band(1 - p.Y)), 0.8), outward = Math.Abs(p.X - 0.5) * 2;
            var k = pal.Sample(outward);
            return Opaque(outward <= reach ? k : k * 0.05);
        };
    }

    // ------------------------------------------------------------------ time

    // Hour, colour stop (night, morning, day, evening) and brightness through the day.
    static readonly (double Hour, int Stop, double Level)[] DayKeys =
        [(0, 0, 0.28), (5, 0, 0.28), (7.5, 1, 0.85), (11, 2, 1), (16.5, 2, 1), (19, 3, 0.8), (22.5, 0, 0.28), (24, 0, 0.28)];

    /// <summary>
    /// Follows the clock: a dim warm glow at night, a sunrise glow in the morning, clean light by day and amber in the
    /// evening, with a little sun crossing the top edge in daytime (Gradient: night, morning, day, evening).
    /// </summary>
    static Shader DayNight(LayerFrame f)
    {
        double h = f.Ctx.LocalHours is { } lh ? ((lh % 24) + 24) % 24 : 12;
        int i = 0;
        while (i < DayKeys.Length - 2 && h >= DayKeys[i + 1].Hour) i++;
        var (a, b) = (DayKeys[i], DayKeys[i + 1]);
        double t = SmoothStep(0, 1, (h - a.Hour) / (b.Hour - a.Hour));
        var pal = f.Palette;
        Rgbf Stop(int s) => pal.Count >= 4 ? pal[s] : pal.Count == 1 ? pal.First : pal.Sample(s switch { 0 => 0, 2 => 1, _ => 0.5 });
        var color = Rgbf.Lerp(Stop(a.Stop), Stop(b.Stop), t) * (a.Level + (b.Level - a.Level) * t);
        double day = SmoothStep(6, 8, h) * SmoothStep(19.5, 17.5, h), sunX = (h - 6) / 13;
        var sun = new Rgbf(1, 0.85, 0.55);
        return (in Pixel p) =>
        {
            if (p.IsKey || day <= 0 || p.Y > 0.25) return Opaque(color);
            double glow = Math.Exp(-Math.Pow((p.X - sunX) / 0.08, 2)) * day;
            return Opaque(Rgbf.Lerp(color, sun, glow));
        };
    }
}
