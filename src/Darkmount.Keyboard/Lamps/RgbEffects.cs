namespace Darkmount.Keyboard.Lamps;

/// <summary>Host-driven RGB animations (the keyboard's own effects are in <see cref="Lighting"/>).</summary>
public enum RgbEffectKind { RainbowWave, Plasma, Breathing, ColorCycle, CpuTemperature, Static }

public sealed record RgbEffectSettings
{
    public RgbEffectKind Effect { get; init; } = RgbEffectKind.RainbowWave;

    /// <summary>1 (slow) … 10 (fast).</summary>
    public int Speed { get; init; } = 5;

    /// <summary>0 … 100 %.</summary>
    public int Brightness { get; init; } = 100;

    /// <summary>Main colour for Breathing and Static ("RRGGBB").</summary>
    public string Color { get; init; } = "FF2800";

    /// <summary>Colour of the edge lights for per-key effects: null = follow the effect's average hue.</summary>
    public string? EdgeColor { get; init; }
}

/// <summary>
/// A lamp and its normalised position on the keyboard (0..1 in both axes). <see cref="KeyId"/> is the Dark Mount key id
/// (<see cref="KeyIds"/>) of a key lamp when known, else 0. <see cref="Edge"/> is the edge-light number (1..64 around the
/// keyboard, 65..96 around the numpad, clockwise from each top-left corner) of a Dark Mount edge LED, else 0.
/// </summary>
public readonly record struct LampPoint(int LampId, double X, double Y, bool IsKey, int KeyId = 0, int Edge = 0);

/// <summary>
/// One animation frame. Whole-keyboard effects set <see cref="All"/> (one range report, very fast); per-key
/// effects set <see cref="Keys"/> plus <see cref="Edges"/> (the 96 edge LEDs share one colour to keep the frame
/// small: ~14 reports instead of 26).
/// </summary>
public sealed record RgbFrame(LampColor? All, IReadOnlyDictionary<int, LampColor>? Keys, LampColor? Edges);

/// <summary>Pure effect maths: time + lamp positions (+ CPU temperature) → colours. Unit-testable.</summary>
public static class RgbEffects
{
    public static bool IsPerKey(RgbEffectKind k) => k is RgbEffectKind.RainbowWave or RgbEffectKind.Plasma;

    public static RgbFrame Render(RgbEffectSettings s, double seconds, IReadOnlyList<LampPoint> lamps, double? cpuTemp = null)
    {
        double speed = Math.Clamp(s.Speed, 1, 10) / 5.0;
        double bright = Math.Clamp(s.Brightness, 0, 100) / 100.0;
        var main = Parse(s.Color);
        switch (s.Effect)
        {
            case RgbEffectKind.Static:
                return new(Scale(main, bright), null, null);

            case RgbEffectKind.Breathing:
                double level = 0.08 + 0.92 * (0.5 - 0.5 * Math.Cos(seconds * speed * Math.PI * 0.8));
                return new(Scale(main, bright * level), null, null);

            case RgbEffectKind.ColorCycle:
                return new(Hsv((seconds * speed * 0.08) % 1.0, bright), null, null);

            case RgbEffectKind.CpuTemperature:
                return new(Scale(TemperatureColor(cpuTemp), bright), null, null);

            case RgbEffectKind.RainbowWave:
            case RgbEffectKind.Plasma:
            {
                var keys = new Dictionary<int, LampColor>(lamps.Count);
                double t = seconds * speed;
                foreach (var l in lamps.Where(l => l.IsKey))
                {
                    double hue = s.Effect == RgbEffectKind.RainbowWave
                        ? l.X * 0.9 - t * 0.25
                        : 0.5 + (Math.Sin(l.X * 7 + t) + Math.Sin(l.Y * 5 - t * 0.8) + Math.Sin((l.X + l.Y) * 4 + t * 0.6)) / 6;
                    keys[l.LampId] = Hsv(Frac(hue), bright);
                }
                var edge = s.EdgeColor is { } e ? Scale(Parse(e), bright) : Hsv(Frac(-t * 0.25 + 0.45), bright);
                return new(null, keys, edge);
            }
        }
        return new(LampColor.Off, null, null);
    }

    /// <summary>Green below 50 °C, through yellow and orange, to red from 85 °C; blue when unknown.</summary>
    public static LampColor TemperatureColor(double? celsius)
    {
        if (celsius is not { } c) return new(0, 80, 255);
        double hue = Math.Clamp((85 - c) / (85 - 50), 0, 1) * (1.0 / 3); // 1/3 = green, 0 = red
        return Hsv(hue, 1);
    }

    public static LampColor Hsv(double h, double value)
    {
        h = Frac(h) * 6;
        int i = (int)h;
        double f = h - i;
        byte v = (byte)Math.Round(255 * value), p = 0, q = (byte)Math.Round(255 * value * (1 - f)), u = (byte)Math.Round(255 * value * f);
        return (i % 6) switch
        {
            0 => new(v, u, p), 1 => new(q, v, p), 2 => new(p, v, u), 3 => new(p, q, v), 4 => new(u, p, v), _ => new(v, p, q),
        };
    }

    static double Frac(double x) => x - Math.Floor(x);

    static LampColor Scale(LampColor c, double k) =>
        new((byte)Math.Round(c.R * k), (byte)Math.Round(c.G * k), (byte)Math.Round(c.B * k));

    public static LampColor Parse(string hex)
    {
        var rgb = Rgb.Parse(hex);
        return new(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// Normalised lamp positions for any LampArray device, straight from the positions it reports (no be quiet!
    /// geometry). Lamps the map identifies as keys get their Dark Mount key id (from the HID usage), so typing effects
    /// and overlays find them; a device with no key bindings counts its lamps as keys when it is a keyboard.
    /// </summary>
    public static IReadOnlyList<LampPoint> LayoutFromPositions(IReadOnlyList<LampInfo> lamps, LampMap map, bool keyboard)
    {
        if (lamps.Count == 0) return [];
        int minX = lamps.Min(l => l.PositionX), maxX = lamps.Max(l => l.PositionX);
        int minY = lamps.Min(l => l.PositionY), maxY = lamps.Max(l => l.PositionY);
        double X(int x) => maxX > minX ? (x - minX) / (double)(maxX - minX) : 0.5;
        double Y(int y) => maxY > minY ? (y - minY) / (double)(maxY - minY) : 0.5;
        bool anyKeys = map.Keys.Count > 0;
        return lamps.Select(l => map.Keys.TryGetValue(l.Id, out var key)
                ? new LampPoint(l.Id, X(l.PositionX), Y(l.PositionY), true, key.KeyId)
                : new LampPoint(l.Id, X(l.PositionX), Y(l.PositionY), IsKey: keyboard && !anyKeys))
            .ToList();
    }

    /// <summary>
    /// Normalised lamp positions: keys from the keyboard geometry (accurate) with their Dark Mount key id, other lamps
    /// (edge lights) from the positions the firmware reports. The ISO-only key (105) gets its ISO position.
    /// </summary>
    public static IReadOnlyList<LampPoint> Layout(IReadOnlyList<LampInfo> lamps, LampMap map, NumpadSide numpad = NumpadSide.Right)
    {
        if (lamps.Count == 0) return [];
        if (numpad == NumpadSide.None) numpad = NumpadSide.Right;
        var rects = KeyGeometry.Keys(PhysicalLayout.Ansi, numpad).ToDictionary(r => (int)r.KeyId);
        double kw = rects.Values.Max(r => r.X + r.Width), kh = rects.Values.Max(r => r.Y + r.Height);
        foreach (var r in KeyGeometry.Keys(PhysicalLayout.Iso, numpad)) rects.TryAdd(r.KeyId, r);
        int minX = lamps.Min(l => l.PositionX), maxX = Math.Max(minX + 1, lamps.Max(l => l.PositionX));
        int minY = lamps.Min(l => l.PositionY), maxY = Math.Max(minY + 1, lamps.Max(l => l.PositionY));
        var keyIdByLamp = map.KeyIdToLamp.ToDictionary(kv => kv.Value, kv => kv.Key);
        return lamps.Select(l =>
            keyIdByLamp.TryGetValue(l.Id, out int keyId) && rects.TryGetValue(keyId, out var r)
                ? new LampPoint(l.Id, (r.X + r.Width / 2.0) / kw, (r.Y + r.Height / 2.0) / kh, true, keyId)
                : new LampPoint(l.Id, (l.PositionX - minX) / (double)(maxX - minX), (l.PositionY - minY) / (double)(maxY - minY), false,
                    Edge: map.BindingKind == LampBindingKind.DarkmountKeyId && l.InputBinding >= DarkmountKeys.FirstEdgeLightBinding
                        ? l.InputBinding - DarkmountKeys.FirstEdgeLightBinding + 1 : 0))
            .ToList();
    }
}
